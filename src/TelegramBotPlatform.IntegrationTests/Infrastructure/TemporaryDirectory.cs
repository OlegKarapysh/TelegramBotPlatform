namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// A scratch directory for a test that needs to seed or inspect the extension store's contents itself —
/// for example placing a package there before the host starts, which is the only way to observe what
/// startup does with one.
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static TemporaryDirectory Create()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "tbp-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return new TemporaryDirectory(path);
    }

    /// <summary>Writes a file into the directory — a stored extension package, as a previous run left it.</summary>
    public Task Write(string fileName, byte[] content, CancellationToken cancellationToken) =>
        File.WriteAllBytesAsync(System.IO.Path.Combine(Path, fileName), content, cancellationToken);

    public bool Contains(string fileName) => File.Exists(System.IO.Path.Combine(Path, fileName));

    /// <summary>The package names the store holds, which is what a restarted host would find.</summary>
    public IReadOnlyList<string> Packages =>
        Directory.GetFiles(Path, "*.dll").Select(System.IO.Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort. A leftover temp directory is the operating system's problem, never a reason to
            // fail a test that has already made its point.
        }
    }
}