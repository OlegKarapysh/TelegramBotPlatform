namespace TelegramBotPlatform.Infrastructure;

public static class ServiceInstaller
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddPlatformModule()
        {
            services.ConfigurePlatformOptions();

            services.AddPlatformPersistence();

            services.AddDataProtection()
                .PersistKeysToDbContext<PlatformDbContext>();
            services.AddSingleton<ITokenProtector, DataProtectionTokenProtector>();

            services.AddHttpClient();
            services.AddTelegramHttpClients();
            services.AddSingleton<IBotClientRegistry, BotClientRegistry>();
            services.AddSingleton<IBotTokenValidator, TelegramBotTokenValidator>();

            // Scoped so it resolves the CURRENT message's bot — see BotContextAccessor/BotScopeFilter.
            // A command handler that injects ITelegramBotClient gets that bot's client for free; this
            // registration is what makes that injection resolve to the correct bot.
            services.AddScoped<BotContextAccessor>();
            services.AddScoped<IBotContext>(serviceProvider => serviceProvider.GetRequiredService<BotContextAccessor>());
            services.AddScoped<ITelegramBotClient>(serviceProvider =>
                serviceProvider.GetRequiredService<IBotClientRegistry>()
                    .Get(serviceProvider.GetRequiredService<IBotContext>().BotId));

            services.AddSingleton<IBehaviorCatalog, BehaviorCatalog>();
            services.AddScoped<BotRegistrationService>();
            // The tracker is scoped because it reads and writes the scoped registry, and every update is
            // consumed in its own scope. Its counts therefore cannot live on it — see BotFailureCounter.
            services.AddSingleton<BotFailureCounter>();
            services.AddScoped<BotHealthTracker>();

            services.AddSingleton<WebhookSecretProvider>();
            services.AddSingleton<PollingBotReceiver>();
            services.AddScoped<WebhookBotReceiver>();

            services.AddSingleton<BotSupervisor>();
            services.AddSingleton<IBotLifecycle>(serviceProvider => serviceProvider.GetRequiredService<BotSupervisor>());
            services.AddHostedService<BotRestoreHostedService>();

            services.AddExtensionStore();
            services.AddSingleton<IExtensionLoader, ExtensionAssemblyLoader>();
            // Composed by hand for two reasons. The size ceiling is passed as a value because
            // PlatformOptions lives here in Infrastructure and Application must not reference it. And the
            // bot lookup is a scoped-per-call delegate, because IBotRegistry wraps a DbContext and is
            // scoped while this service is a singleton — injecting it directly would be a captive
            // dependency (and the DI validator rejects it outright).
            services.AddSingleton(serviceProvider =>
            {
                var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

                return new BehaviorExtensionService(
                    serviceProvider.GetRequiredService<IExtensionStore>(),
                    serviceProvider.GetRequiredService<IExtensionLoader>(),
                    serviceProvider.GetRequiredService<IBehaviorCatalog>(),
                    async cancellationToken =>
                    {
                        await using var scope = scopeFactory.CreateAsyncScope();

                        return await scope.ServiceProvider.GetRequiredService<IBotRegistry>().List(cancellationToken);
                    },
                    serviceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value.MaxExtensionPackageBytes,
                    serviceProvider.GetRequiredService<ILogger<BehaviorExtensionService>>());
            });

            return services;
        }

        /// <summary>
        /// Registers the two named clients used to reach Telegram, with the factory's default logging
        /// removed.
        /// <para>
        /// This is a confidentiality control, not a noise-reduction one. A bot token is carried in the
        /// request <em>path</em> (<c>api.telegram.org/bot{token}/getMe</c>), and
        /// <see cref="IHttpClientFactory"/>'s default handlers log the full request URI at Information
        /// level — so out of the box every bot's credential is written to the log sink on every call, in
        /// plaintext, for the sink's whole retention period. Stripping those loggers is what makes the
        /// platform's "tokens are never logged" property actually true; the outcomes worth knowing about
        /// are already logged by <see cref="BotSupervisor"/> and <see cref="TelegramBotTokenValidator"/>
        /// without the URI.
        /// </para>
        /// </summary>
        public IServiceCollection AddTelegramHttpClients()
        {
            services.AddHttpClient(nameof(BotClientRegistry)).RemoveAllLoggers();
            services.AddHttpClient(nameof(TelegramBotTokenValidator)).RemoveAllLoggers();

            return services;
        }

        /// <summary>
        /// Picks where behavior extensions live: a configured bucket selects durable object storage,
        /// without one they stay in the local plugins directory — which is what keeps local development
        /// and the test suite free of cloud credentials.
        /// <para>
        /// The choice is made when <see cref="IExtensionStore"/> is first resolved, not here, so the S3
        /// client and its credential lookup are never constructed at all on a machine with no bucket
        /// configured.
        /// </para>
        /// </summary>
        public IServiceCollection AddExtensionStore()
        {
            services.AddSingleton<FileSystemExtensionStore>();

            // Region and credentials come from the ambient environment (AWS_REGION plus the task role on
            // ECS), so there is nothing here to keep in sync with the infrastructure.
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client());
            services.AddSingleton<S3ExtensionStore>();

            services.AddSingleton<IExtensionStore>(serviceProvider =>
            {
                var options = serviceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value;

                return string.IsNullOrWhiteSpace(options.PluginsBucket)
                    ? serviceProvider.GetRequiredService<FileSystemExtensionStore>()
                    : serviceProvider.GetRequiredService<S3ExtensionStore>();
            });

            return services;
        }

        public IServiceCollection ConfigurePlatformOptions()
        {
            services.AddOptions<PlatformOptions>()
                .BindConfiguration(PlatformOptions.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            return services;
        }
    }

    /// <summary>Maps the operator-only admin API.</summary>
    public static WebApplication MapAdminApi(this WebApplication app)
    {
        AdminEndpoints.Map(app);
        return app;
    }

    /// <summary>Maps the per-bot webhook endpoint used outside Development — see <see cref="BotSupervisor"/>.</summary>
    public static WebApplication MapBotWebhook(this WebApplication app)
    {
        app.MapPost("/telegram-bot/webhook/{botId:long}", async (
            long botId,
            Update update,
            HttpRequest request,
            IBotRegistry botRegistry,
            WebhookSecretProvider webhookSecretProvider,
            WebhookBotReceiver receiver,
            CancellationToken cancellationToken) =>
        {
            // Validate the secret first (constant-time) and independently of whether the bot exists, so an
            // unauthenticated caller always sees Unauthorized and cannot enumerate which bot ids are
            // registered (NotFound vs Unauthorized) — the secret is derived from the bot id, not stored.
            var providedSecret = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
            if (!FixedTimeEquals(providedSecret, webhookSecretProvider.GetSecret(botId)))
            {
                return Results.Unauthorized();
            }

            // A missing or Disabled bot must not process updates — Telegram may still retry a webhook for a
            // bot that was just disabled (its webhook is deleted, but retries are already in flight).
            var registration = await botRegistry.Get(botId, cancellationToken);
            if (registration is null || registration.Status == BotStatus.Disabled)
            {
                return Results.NotFound();
            }

            await receiver.Handle(botId, update, cancellationToken);
            return Results.Ok();
        });

        return app;
    }

    /// <summary>Registers the bot-scope consume filter (see <see cref="BotScopeFilter{T}"/>) on the bus.</summary>
    public static void UseBotScopeFilter(this IConsumePipeConfigurator configurator, IRegistrationContext context) =>
        configurator.UseConsumeFilter(typeof(BotScopeFilter<>), context);

    /// <summary>Constant-time comparison of the webhook secret, mirroring <see cref="AdminApiKeyAuth"/>.</summary>
    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}