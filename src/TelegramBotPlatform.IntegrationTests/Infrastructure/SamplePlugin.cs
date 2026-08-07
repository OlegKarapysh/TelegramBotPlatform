namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// The repository's sample behavior extension, as compiled bytes.
/// <para>
/// Using a real, separately-built assembly is the point of these tests: the extension path is the one part
/// of the platform whose correctness depends on reflection, a collectible <c>AssemblyLoadContext</c> and
/// type identity unifying across it — none of which a stand-in loader can exercise. The bytes are uploaded
/// through the admin API exactly as an operator would upload them.
/// </para>
/// </summary>
internal static class SamplePlugin
{
    public const string FileName = "ReverseBehavior.dll";

    /// <summary>The behavior key the sample declares — a bot is assigned this once the package is loaded.</summary>
    public const string BehaviorKey = "reverse";

    private static readonly Lazy<byte[]> _bytes = new(ReadFromTestOutput);

    /// <summary>The compiled package, byte for byte.</summary>
    public static byte[] Bytes => _bytes.Value;

    /// <summary>
    /// A package that is not a managed assembly — what an operator uploading the wrong file, or a
    /// truncated transfer, produces. The platform must reject it and leave itself untouched.
    /// </summary>
    public static byte[] Corrupt => "MZ this is not a managed assembly"u8.ToArray();

    private static byte[] ReadFromTestOutput()
    {
        // Copied next to the test assembly by the ProjectReference in this project's .csproj.
        var path = Path.Combine(AppContext.BaseDirectory, FileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The sample behavior extension was not found at \"{path}\". It is built and copied by the "
                + "ProjectReference to samples/ReverseBehavior in TelegramBotPlatform.IntegrationTests.csproj.",
                path);
        }

        return File.ReadAllBytes(path);
    }
}