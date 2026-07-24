var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

// Liveness probe: no registered checks, so `/health` returns 200 without touching Telegram or the
// database. The container healthcheck and any deploy poll use it.
builder.Services.AddHealthChecks();

builder.Services.AddPlatformMessaging();
builder.Services.AddPlatformModule();

// Built-in behaviors are composed here in the host. Register each as a service so it can be resolved
// and added to the behavior catalog at startup (below).
builder.Services.AddSingleton<EchoBehavior>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

var app = builder.Build();

// `dotnet TelegramBotPlatform.WebApi.dll migrate` applies pending EF migrations and exits without
// starting the host. Run this in a one-off container before each rollout. The platform owns one
// DbContext (registered as a base DbContext too), so migrate every one discovered.
if (args.Contains("migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    foreach (var dbContext in scope.ServiceProvider.GetServices<DbContext>())
    {
        await dbContext.Database.MigrateAsync();
    }

    return;
}

// Registers the platform's built-in behaviors, then reloads every previously-uploaded behavior extension.
// Runs before the host starts serving traffic/receivers, so every bot's assigned behavior is already
// present by the time BotUpdateRouter looks it up.
using (var startupScope = app.Services.CreateScope())
{
    var behaviorCatalog = startupScope.ServiceProvider.GetRequiredService<IBehaviorCatalog>();
    var startupLogger = startupScope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BehaviorStartup");

    // A built-in behavior must register, or a bot assigned to it would have its updates silently dropped by
    // the router — fail fast rather than start a host that can't serve its own built-in behavior.
    var echoBehavior = startupScope.ServiceProvider.GetRequiredService<EchoBehavior>();
    var builtInResult = behaviorCatalog.Register(echoBehavior, "built-in");
    if (builtInResult.IsFailed)
    {
        throw new InvalidOperationException(
            $"Failed to register the built-in \"{echoBehavior.Key}\" behavior: {builtInResult.Errors.First().Message}");
    }

    var pluginStore = startupScope.ServiceProvider.GetRequiredService<PluginStore>();
    var extensionLoader = startupScope.ServiceProvider.GetRequiredService<ExtensionAssemblyLoader>();

    foreach (var assemblyPath in pluginStore.ListStoredAssemblyPaths())
    {
        var loadResult = extensionLoader.Load(assemblyPath);
        if (loadResult.IsFailed)
        {
            startupLogger.LogError("Failed to reload behavior extension {Path}: {Error}", assemblyPath, loadResult.Errors.First().Message);
            continue;
        }

        foreach (var behavior in loadResult.Value)
        {
            var registerResult = behaviorCatalog.Register(behavior, $"extension:{Path.GetFileName(assemblyPath)}");
            if (registerResult.IsFailed)
            {
                startupLogger.LogError("Failed to register behavior from {Path}: {Error}", assemblyPath, registerResult.Errors.First().Message);
            }
        }
    }
}

app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapHealthChecks("/health");

app.MapAdminApi();
app.MapBotWebhook();

await app.RunAsync();