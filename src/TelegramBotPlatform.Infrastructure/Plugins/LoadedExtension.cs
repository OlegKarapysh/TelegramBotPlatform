namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// A behavior-extension assembly loaded into its own collectible <see cref="AssemblyLoadContext"/>, plus
/// the file it was staged to so its private dependencies resolve from alongside it.
/// <para>
/// Disposing unloads the context and deletes the staged file. The unload is cooperative — the runtime
/// collects the context once nothing references it — so an update still being handled by one of these
/// behaviors finishes safely against the old instance while the next update resolves the new one from the
/// catalog. That is what makes replacing a live extension work without draining in-flight work.
/// </para>
/// </summary>
internal sealed class LoadedExtension(
    string packageName,
    IReadOnlyList<IBotBehavior> behaviors,
    PluginLoadContext loadContext,
    string stagedFilePath) : ILoadedExtension
{
    private bool _disposed;

    public string PackageName { get; } = packageName;

    public IReadOnlyList<IBotBehavior> Behaviors { get; } = behaviors;

    /// <summary>Idempotent, and never throws — a failure to clean up must not break the operation that triggered it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            loadContext.Unload();
        }
        catch (InvalidOperationException)
        {
            // A non-collectible context cannot be unloaded. Not reachable here (PluginLoadContext is always
            // collectible), but disposal must stay silent regardless.
        }

        try
        {
            // The per-load staging directory, not the file alone — it also holds any private dependencies.
            // Deleting the directory is why staging must never be the filesystem store's own root.
            var stagingDirectory = Path.GetDirectoryName(stagedFilePath);
            if (stagingDirectory is not null && Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The staged copy is a cache, not state. Leaving one behind costs disk, never correctness —
            // the next load creates a fresh directory.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}