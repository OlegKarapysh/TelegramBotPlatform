namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// What became of one stored extension package — reported by <c>GET /admin/behaviors</c> alongside the
/// assignable behaviors. Every name in the store gets an entry, <em>including</em> packages that failed to
/// load, so a broken package is visible and still addressable by name for repair or removal.
/// </summary>
/// <param name="PackageName">The stored package's file name, e.g. <c>Reminders.dll</c>.</param>
/// <param name="Loaded">Whether its behaviors are currently registered.</param>
/// <param name="BehaviorKeys">The keys it contributed; empty when it did not load.</param>
/// <param name="Error">
/// Why it did not load — set only when <paramref name="Loaded"/> is false. Never carries package
/// contents, credentials, or bot tokens.
/// </param>
public sealed record ExtensionPackageStatus(
    string PackageName,
    bool Loaded,
    IReadOnlyList<string> BehaviorKeys,
    string? Error);