namespace TelegramBotPlatform.Infrastructure.Plugins;

/// <summary>
/// Stores behavior-extension packages in a local directory. The default when no bucket is configured, and
/// the reason local development and the test suite need no cloud credentials and no storage emulator.
/// <para>
/// On a container with ephemeral disk this store does <em>not</em> survive the instance being replaced —
/// that is exactly why a deployment configures <see cref="PlatformOptions.PluginsBucket"/> instead.
/// </para>
/// </summary>
public sealed class FileSystemExtensionStore(IOptions<PlatformOptions> platformOptions) : IExtensionStore
{
    private string Directory => platformOptions.Value.PluginsDirectory;

    public Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default)
    {
        try
        {
            // A missing directory is an empty store, not a failure — the same as a reachable, empty bucket.
            IReadOnlyList<string> names = System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.GetFiles(Directory, "*.dll").Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray()
                : [];

            return Task.FromResult(Result.Ok(names));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Fail<IReadOnlyList<string>>(Unreachable(exception)));
        }
    }

    public async Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default)
    {
        var path = PathFor(packageName);

        if (!File.Exists(path))
        {
            return NotFound(packageName);
        }

        try
        {
            return Result.Ok(await File.ReadAllBytesAsync(path, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail<byte[]>(Unreachable(exception));
        }
    }

    public async Task<Result> Write(string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        var path = PathFor(packageName);

        if (!overwrite && File.Exists(path))
        {
            return new ExtensionConflictError($"A behavior extension named \"{packageName}\" already exists.");
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            await using var fileStream = File.Create(path);
            await content.CopyToAsync(fileStream, cancellationToken);

            return Result.Ok();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(Unreachable(exception));
        }
    }

    public Task<Result> Delete(string packageName, CancellationToken cancellationToken = default)
    {
        try
        {
            var path = PathFor(packageName);

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.FromResult(Result.Ok());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Fail(Unreachable(exception)));
        }
    }

    private string PathFor(string packageName) => Path.Combine(Directory, packageName);

    private static PackageNotFoundError NotFound(string packageName) =>
        new($"Behavior extension \"{packageName}\" was not found.");

    private StoreUnavailableError Unreachable(Exception exception) =>
        new($"The behavior extension store at \"{Directory}\" could not be reached: {exception.Message}");
}