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

    /// <summary>
    /// Bots whose persisted status this process has already reconciled after a success. Separate from the
    /// counts because it answers a different question — see <see cref="RecordSuccess"/>.
    /// </summary>
    private readonly ConcurrentDictionary<long, byte> _reconciled = new();

    /// <summary>Records a failure for the bot and returns its new consecutive-failure count.</summary>
    public int Increment(long botId) => _consecutiveFailures.AddOrUpdate(botId, 1, (_, count) => count + 1);

    /// <summary>
    /// Records a success, reporting whether the bot's <em>persisted</em> status now has to be re-checked.
    /// <para>
    /// True when a failure streak was just broken — and also on the first success this process sees for a
    /// bot, even with no streak to break. That second case is the one a restart produces: a bot is
    /// persisted <see cref="BotStatus.Failing"/> by a process whose counts died with it, so a
    /// counter-only test says "nothing to clear" and the bot would stay flagged for as long as it kept
    /// working. Afterwards a healthy bot's successes cost nothing: no count to clear, already reconciled.
    /// </para>
    /// </summary>
    public bool RecordSuccess(long botId)
    {
        var brokeAStreak = _consecutiveFailures.TryRemove(botId, out var previous) && previous > 0;
        var firstSuccessInThisProcess = _reconciled.TryAdd(botId, 0);

        return brokeAStreak || firstSuccessInThisProcess;
    }

    /// <summary>
    /// Drops everything remembered about a bot, so one taken down and brought back starts clean.
    /// <para>
    /// Called when the operator disables or removes a bot. Without it a bot disabled part-way to the
    /// threshold would be flagged <see cref="BotStatus.Failing"/> by its first failure after being
    /// re-enabled — carrying a verdict about the deployment it was taken down from — and every removed
    /// bot would leave an entry behind for the life of the process.
    /// </para>
    /// </summary>
    public void Forget(long botId)
    {
        _consecutiveFailures.TryRemove(botId, out _);
        _reconciled.TryRemove(botId, out _);
    }
}