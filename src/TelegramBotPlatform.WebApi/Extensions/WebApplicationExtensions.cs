namespace TelegramBotPlatform.WebApi.Extensions;

public static class WebApplicationExtensions
{
    extension(WebApplication webApplication)
    {
        public async Task Migrate()
        {
            await using var scope = webApplication.Services.CreateAsyncScope();
            foreach (var dbContext in scope.ServiceProvider.GetServices<DbContext>())
            {
                await dbContext.Database.MigrateAsync();
            }
        }

        /// <summary>
        /// Registers the host's built-in behaviors, then restores every operator-uploaded extension from
        /// durable storage — all before the app starts serving, so no update can reach a behavior that has
        /// not been restored yet.
        /// <para>
        /// Throws if the extension store cannot be read within its retry budget. That is deliberate: the
        /// process exits without binding a port, the task never reports healthy, and the deployment's
        /// health-gated rollout rolls back — far better than going green with behaviors silently missing.
        /// A single package that fails to load is a different matter; it is recorded and skipped.
        /// </para>
        /// </summary>
        public async Task RegisterBehaviors()
        {
            await using var scope = webApplication.Services.CreateAsyncScope();
            var behaviorCatalog = scope.ServiceProvider.GetRequiredService<IBehaviorCatalog>();
            var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BehaviorStartup");

            var echoBehavior = scope.ServiceProvider.GetRequiredService<EchoBehavior>();
            var builtInResult = behaviorCatalog.Register(echoBehavior, BehaviorSource.BuiltIn);
            if (builtInResult.IsFailed)
            {
                throw new InvalidOperationException(
                    $"Failed to register the built-in \"{echoBehavior.Key}\" behavior: {builtInResult.Errors.First().Message}");
            }

            var extensions = scope.ServiceProvider.GetRequiredService<BehaviorExtensionService>();
            var platformOptions = scope.ServiceProvider.GetRequiredService<IOptions<PlatformOptions>>().Value;

            var restored = await extensions.RestoreAll(platformOptions.ExtensionStoreStartupTimeout);
            if (restored.IsFailed)
            {
                throw new InvalidOperationException(
                    "Could not read the behavior extension store, so the platform would start with an "
                    + $"incomplete behavior catalog. Refusing to serve. {restored.Errors.First().Message}");
            }

            foreach (var package in extensions.Packages.Where(package => !package.Loaded))
            {
                startupLogger.LogWarning(
                    "Behavior extension {Package} is stored but not loaded: {Error}", package.PackageName, package.Error);
            }
        }
    }
}