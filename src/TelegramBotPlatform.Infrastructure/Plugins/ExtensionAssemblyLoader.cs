namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// Loads an operator-supplied behavior-extension assembly into its own collectible
/// <see cref="AssemblyLoadContext"/> and discovers its <see cref="IBotBehavior"/> implementations.
/// A bad assembly — one that fails to load, or contains no usable behavior — is reported without touching
/// anything already running; the platform and every existing bot are unaffected.
/// </summary>
public sealed class ExtensionAssemblyLoader
{
    public Result<IReadOnlyList<IBotBehavior>> Load(string assemblyPath)
    {
        try
        {
            var loadContext = new PluginLoadContext(assemblyPath);
            // Load the main assembly from a byte copy rather than LoadFromAssemblyPath, which memory-maps and
            // locks the file on Windows for the lifetime of the (collectible) context. Loading from bytes keeps
            // the on-disk .dll unlocked so a rejected upload can be deleted immediately (its private
            // dependencies still resolve from alongside the file via the AssemblyDependencyResolver).
            using var assemblyStream = new MemoryStream(File.ReadAllBytes(assemblyPath));
            var assembly = loadContext.LoadFromStream(assemblyStream);

            var behaviors = assembly.GetExportedTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IBotBehavior).IsAssignableFrom(type))
                .Select(CreateInstance)
                .OfType<IBotBehavior>()
                .ToArray();

            if (behaviors.Length == 0)
            {
                return new Error($"Assembly \"{Path.GetFileName(assemblyPath)}\" does not contain any usable IBotBehavior implementation.");
            }

            return Result.Ok<IReadOnlyList<IBotBehavior>>(behaviors);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new Error($"Failed to load behavior extension \"{Path.GetFileName(assemblyPath)}\": {exception.Message}");
        }
    }

    private static IBotBehavior? CreateInstance(Type type)
    {
        try
        {
            return Activator.CreateInstance(type) as IBotBehavior;
        }
        catch
        {
            // A behavior that can't be constructed (e.g. no parameterless constructor) is simply skipped,
            // not a load failure — other behaviors in the same assembly can still be usable.
            return null;
        }
    }
}