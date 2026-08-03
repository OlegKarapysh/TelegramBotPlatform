namespace TelegramBotPlatform.Infrastructure;

public sealed record PlatformOptions
{
    public const string SectionName = "Platform";

    /// <summary>Static admin API key required on every <c>/admin/*</c> request.</summary>
    [Required]
    public required string AdminApiKey { get; init; }

    /// <summary>
    /// Local directory for behavior extension packages. When <see cref="PluginsBucket"/> is not set this
    /// <em>is</em> the store; when it is, packages live in the bucket and this is only the staging
    /// directory they are written to so their private dependencies can be resolved from alongside them.
    /// </summary>
    public string PluginsDirectory { get; init; } = "plugins";

    /// <summary>
    /// Object-storage bucket holding behavior extension packages. Setting it selects durable shared
    /// storage; leaving it unset keeps packages in <see cref="PluginsDirectory"/> — which is what makes
    /// local development and the test suite work with no cloud credentials.
    /// </summary>
    public string? PluginsBucket { get; init; }

    /// <summary>Key prefix for packages within <see cref="PluginsBucket"/>. Must match the prefix the access policy grants.</summary>
    public string PluginsPrefix { get; init; } = "behaviors/";

    /// <summary>
    /// How long to keep retrying the extension store at startup before giving up. Exhausting it aborts
    /// startup rather than serving an incomplete behavior catalog.
    /// </summary>
    public TimeSpan ExtensionStoreStartupTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Largest accepted extension package. Checked before the upload is buffered, so an oversized package
    /// cannot exhaust the platform's memory. Defaults to 25 MB — packages are normally single-digit MB.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxExtensionPackageBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Base URL bots' webhooks are registered under (each bot's is <c>{WebhookBaseUrl}/{botId}</c>); required outside Development.</summary>
    public string? WebhookBaseUrl { get; init; }
}