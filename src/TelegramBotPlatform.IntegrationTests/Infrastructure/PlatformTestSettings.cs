using Microsoft.EntityFrameworkCore.Storage;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// The store the platform's tables live in for one test. Held as an object rather than a name so two
/// <see cref="PlatformTestHost"/>s can deliberately share one — which is how a restart is simulated:
/// the second host starts against the first host's data, exactly as a redeployed container does.
/// </summary>
public sealed class PlatformDatabase
{
    public string Name { get; } = $"platform-{Guid.NewGuid():N}";

    /// <summary>
    /// Passed explicitly so sharing is a property of this object rather than of EF's process-wide
    /// provider cache — two hosts share data when, and only when, they share a <see cref="PlatformDatabase"/>.
    /// </summary>
    public InMemoryDatabaseRoot Root { get; } = new();
}

/// <summary>What a test may vary about the platform it boots. Every default is the ordinary case.</summary>
public sealed record PlatformTestSettings
{
    public string AdminApiKey { get; init; } = "integration-tests-admin-key";

    /// <summary>
    /// Deliberately shares its path with the webhook route this host maps, so the URL the supervisor
    /// registers with Telegram can be posted straight back to this app — see
    /// <see cref="PlatformTestHost.RegisteredWebhookPath"/>.
    /// </summary>
    public string WebhookBaseUrl { get; init; } = "https://platform.test/telegram-bot/webhook";

    public long MaxExtensionPackageBytes { get; init; } = 25L * 1024 * 1024;

    /// <summary>Short so a test that deliberately breaks the store fails fast instead of retrying for 30s.</summary>
    public TimeSpan ExtensionStoreStartupTimeout { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Where extension packages are stored. A fresh temp directory, owned and deleted by the host, when null.</summary>
    public string? PluginsDirectory { get; init; }

    /// <summary>A fresh, empty database when null.</summary>
    public PlatformDatabase? Database { get; init; }

    /// <summary>
    /// Replaces the real extension store. Only for the failures a healthy local directory cannot produce —
    /// an unreachable store — which the platform is required to treat very differently from an empty one.
    /// </summary>
    public IExtensionStore? ExtensionStore { get; init; }
}