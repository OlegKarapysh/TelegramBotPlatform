namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// The kinds of failure the extension lifecycle distinguishes, as types rather than message text.
/// <para>
/// The admin API answers a different HTTP status for each, and startup treats one of them (an unreachable
/// store) as fatal. Encoding that in the type keeps the classification from depending on wording that a
/// reworded message — or a store implementation phrasing things its own way — would silently break.
/// </para>
/// </summary>
public abstract class ExtensionError(string message) : Error(message);

/// <summary>
/// The store itself could not be reached: a permission problem, a misconfiguration, or an outage. Never
/// the caller's fault and never a missing package, so it is worth retrying and must not be reported as a
/// bad request. Exhausting the startup retry budget on one of these aborts the host deliberately.
/// </summary>
public sealed class StoreUnavailableError(string message) : ExtensionError(message);

/// <summary>The named package is not in the store.</summary>
public sealed class PackageNotFoundError(string message) : ExtensionError(message);

/// <summary>
/// The operation collides with something already there — a package name already in the store, or a
/// behavior key already registered — and refuses to overwrite it.
/// </summary>
public sealed class ExtensionConflictError(string message) : ExtensionError(message);

/// <summary>
/// The operation would take away a behavior a registered bot is still assigned to. Carries the blocking
/// bot ids both in <see cref="BotIds"/> and as <c>bots</c> metadata, so tooling need not parse the message.
/// </summary>
public sealed class BehaviorInUseError : ExtensionError
{
    public BehaviorInUseError(string message, IReadOnlyList<long> botIds)
        : base(message)
    {
        BotIds = botIds;
        WithMetadata("bots", botIds);
    }

    public IReadOnlyList<long> BotIds { get; }
}