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

        public void RegisterBehaviors()
        {
            using var scope = webApplication.Services.CreateScope();
            var behaviorCatalog = scope.ServiceProvider.GetRequiredService<IBehaviorCatalog>();
            var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("BehaviorStartup");

            var echoBehavior = scope.ServiceProvider.GetRequiredService<EchoBehavior>();
            var builtInResult = behaviorCatalog.Register(echoBehavior, BehaviorSource.BuiltIn);
            if (builtInResult.IsFailed)
            {
                throw new InvalidOperationException(
                    $"Failed to register the built-in \"{echoBehavior.Key}\" behavior: {builtInResult.Errors.First().Message}");
            }

            var pluginStore = scope.ServiceProvider.GetRequiredService<PluginStore>();
            var extensionLoader = scope.ServiceProvider.GetRequiredService<ExtensionAssemblyLoader>();

            foreach (var assemblyPath in pluginStore.ListStoredAssemblyPaths())
            {
                var loadResult = extensionLoader.Load(assemblyPath);
                if (loadResult.IsFailed)
                {
                    startupLogger.LogError(
                        "Failed to reload behavior extension {Path}: {Error}", assemblyPath, loadResult.Errors[0].Message);
                    continue;
                }

                foreach (var behavior in loadResult.Value)
                {
                    var registerResult = behaviorCatalog.Register(behavior, BehaviorSource.Extension(Path.GetFileName(assemblyPath)));
                    if (registerResult.IsFailed)
                    {
                        startupLogger.LogError(
                            "Failed to register behavior from {Path}: {Error}", assemblyPath, registerResult.Errors[0].Message);
                    }
                }
            }
        }
    }
}