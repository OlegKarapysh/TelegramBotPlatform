namespace TelegramBotPlatform.Application;

/// <summary>
/// Tracks per-bot update-handling health. After repeated consecutive failures a bot is marked
/// <see cref="BotStatus.Failing"/> while its receiver keeps running at normal cadence — no backoff, no
/// auto-disable; the operator decides whether to disable/rotate/remove. A later success clears the flag
/// back to <see cref="BotStatus.Active"/>, including a success in a <em>later process</em> than the one
/// that set it — the flag is durable, so clearing it must not depend on in-memory counts that are not.
/// Never touches a bot the operator has explicitly disabled.
/// </summary>
public sealed class BotHealthTracker(
    IBotRegistry botRegistry, BotFailureCounter failureCounter, ILogger<BotHealthTracker> logger)
{
    public const int FailureThreshold = 3;

    public async Task RecordFailure(long botId, CancellationToken cancellationToken = default)
    {
        var failures = failureCounter.Increment(botId);
        if (failures < FailureThreshold)
        {
            return;
        }

        var registration = await botRegistry.Get(botId, cancellationToken);
        if (registration is null || registration.Status == BotStatus.Disabled)
        {
            return;
        }

        if (registration.Status != BotStatus.Failing)
        {
            await botRegistry.UpdateStatus(botId, BotStatus.Failing, cancellationToken);
            logger.LogWarning("Bot {BotId} marked Failing after {Failures} consecutive errors.", botId, failures);
        }
    }

    public async Task RecordSuccess(long botId, CancellationToken cancellationToken = default)
    {
        // Asks the counter, not itself, whether the persisted status is worth a look: a bot can be
        // Failing on record with no in-memory streak behind it, which is what every restart produces.
        if (!failureCounter.RecordSuccess(botId))
        {
            return;
        }

        var registration = await botRegistry.Get(botId, cancellationToken);
        if (registration is { Status: BotStatus.Failing })
        {
            await botRegistry.UpdateStatus(botId, BotStatus.Active, cancellationToken);
            logger.LogInformation("Bot {BotId} recovered; status reset to Active.", botId);
        }
    }
}