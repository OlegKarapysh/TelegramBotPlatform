namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// Shares the platform SDK and Telegram.Bot assemblies from the host's default load context (so a
/// plugin's <see cref="IBotBehavior"/> instances unify with the host's interface type), resolving
/// everything else — the plugin's own private dependencies — from alongside the plugin DLL.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> _sharedAssemblyNames = ["TelegramBotPlatform.Public", "Telegram.Bot"];

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null && _sharedAssemblyNames.Contains(assemblyName.Name))
        {
            return null; // Fall back to the default ALC, unifying the type with the host's.
        }

        var resolvedPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return resolvedPath is null ? null : LoadFromAssemblyPath(resolvedPath);
    }
}