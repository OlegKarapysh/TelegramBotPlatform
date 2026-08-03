namespace TelegramBotPlatform.Application;

/// <summary>
/// Per-bot consecutive update-handling failure counts, shared for the lifetime of the host.
/// <para>
/// Kept apart from <see cref="BotHealthTracker"/> deliberately, because the two have different lifetimes.
/// The tracker is resolved <em>per update</em> — it needs the scoped <c>IBotRegistry</c>, and every update
/// is consumed in its own DI scope — so a count living on the tracker would start again from zero on
/// every update, no bot could ever reach <see cref="BotHealthTracker.FailureThreshold"/>, and
/// <see cref="BotStatus.Failing"/> would be unreachable. This is the part that has to outlive the scope,
/// so this is the part that is a singleton.
/// </para>
/// </summary>
public sealed class BotFailureCounter
{
    private readonly ConcurrentDictionary<long, int> _consecutiveFailures = new();

    /// <summary>Records a failure for the bot and returns its new consecutive-failure count.</summary>
    public int Increment(long botId) => _consecutiveFailures.AddOrUpdate(botId, 1, (_, count) => count + 1);

    /// <summary>Clears the bot's count, reporting whether there was in fact anything to clear.</summary>
    public bool Clear(long botId) => _consecutiveFailures.TryRemove(botId, out var previous) && previous > 0;
}