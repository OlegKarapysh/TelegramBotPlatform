namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// Loads an operator-supplied behavior-extension assembly into its own collectible
/// <see cref="AssemblyLoadContext"/> and discovers its <see cref="IBotBehavior"/> implementations.
/// A bad assembly — one that fails to load, or contains no usable behavior — is reported without touching
/// anything already running; the platform and every existing bot are unaffected.
/// </summary>
public sealed class ExtensionAssemblyLoader(IOptions<PlatformOptions> platformOptions) : IExtensionLoader
{
    /// <summary>
    /// Staged copies live in a dedicated subdirectory, never in <see cref="PlatformOptions.PluginsDirectory"/>
    /// itself. That directory doubles as the filesystem store's root, and a handle deletes its staged copy on
    /// disposal — staging directly into the root would make a successful replace delete the package it had
    /// just stored. The store's listing globs the root only, so nothing here is ever mistaken for a package.
    /// </summary>
    private string StagingDirectory => Path.Combine(platformOptions.Value.PluginsDirectory, ".staging");

    private bool _sweptStaleStaging;

    public Result<ILoadedExtension> Load(string packageName, byte[] content)
    {
        string? stagedFilePath = null;

        try
        {
            // AssemblyDependencyResolver is built from a file path and reads the .deps.json beside it, so a
            // package whose store is remote still needs a local copy for its PRIVATE dependencies to
            // resolve. The bytes are the source of truth; this file is a cache the handle owns.
            stagedFilePath = Stage(packageName, content);

            var loadContext = new PluginLoadContext(stagedFilePath);
            // Load from a byte copy rather than LoadFromAssemblyPath, which memory-maps and locks the file
            // on Windows for the lifetime of the (collectible) context. Loading from bytes keeps the staged
            // .dll unlocked so a rejected upload can be deleted immediately (its private dependencies still
            // resolve from alongside the file via the AssemblyDependencyResolver).
            using var assemblyStream = new MemoryStream(content);
            var assembly = loadContext.LoadFromStream(assemblyStream);

            var behaviors = assembly.GetExportedTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(IBotBehavior).IsAssignableFrom(type))
                .Select(CreateInstance)
                .OfType<IBotBehavior>()
                .ToArray();

            if (behaviors.Length == 0)
            {
                loadContext.Unload();
                Cleanup(stagedFilePath);

                return new Error($"Assembly \"{packageName}\" does not contain any usable IBotBehavior implementation.");
            }

            return Result.Ok<ILoadedExtension>(new LoadedExtension(packageName, behaviors, loadContext, stagedFilePath));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Cleanup(stagedFilePath);

            return new Error($"Failed to load behavior extension \"{packageName}\": {exception.Message}");
        }
    }

    /// <summary>
    /// Each load gets its own staging directory. Two handles for the same package coexist during a
    /// replacement, so a shared path would have one handle's disposal delete the other's copy.
    /// </summary>
    private string Stage(string packageName, byte[] content)
    {
        SweepStaleStagingOnce();

        var directory = Path.Combine(StagingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, packageName);
        File.WriteAllBytes(path, content);

        return path;
    }

    /// <summary>
    /// Clears staged copies left by a previous process. A handle deletes its own directory on disposal, so
    /// anything still here at the first load of this process was orphaned by a crash or a kill — nothing in
    /// it is referenced, and left alone it would accumulate a copy per load, forever.
    /// <para>
    /// Once per process, on the first load rather than in the constructor: the loader is a singleton the DI
    /// container may build eagerly, and deleting directories is not something a constructor should do.
    /// Callers are serialised by <c>BehaviorExtensionService</c>'s mutation lock, so the flag needs no
    /// interlocking; a redundant sweep would be harmless anyway.
    /// </para>
    /// </summary>
    private void SweepStaleStagingOnce()
    {
        if (_sweptStaleStaging)
        {
            return;
        }

        _sweptStaleStaging = true;

        try
        {
            if (Directory.Exists(StagingDirectory))
            {
                Directory.Delete(StagingDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort, exactly as on the disposal path: leftover staged copies cost disk, never
            // correctness. The per-load directory created next is unaffected either way.
        }
    }

    private static void Cleanup(string? stagedFilePath)
    {
        if (stagedFilePath is null)
        {
            return;
        }

        try
        {
            // Removes the per-load directory, which also takes any private dependencies staged beside it.
            var directory = Path.GetDirectoryName(stagedFilePath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort: a leftover staged copy is wasted disk, never wrong behavior.
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