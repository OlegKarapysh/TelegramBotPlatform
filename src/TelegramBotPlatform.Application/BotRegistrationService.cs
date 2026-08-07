namespace TelegramBotPlatform.Application;

/// <summary>
/// Orchestrates bot fleet management for the admin API: validates a candidate bot against the behavior
/// catalog and Telegram itself before ever touching the registry, then hands the running receiver
/// over to <see cref="IBotLifecycle"/> — never both at once, so a bot is either fully registered and
/// running, or not registered at all.
/// <para>
/// Bringing the receiver up is the step that can still fail after the registry has been written, because
/// it talks to Telegram (<c>setWebhook</c>) on a connection that may be down and a URL that may be
/// misconfigured. Every call that starts a receiver therefore undoes its own registry write when that
/// happens, so the two systems cannot end up disagreeing — and reports a refusal rather than letting the
/// exception escape as an opaque 500.
/// </para>
/// </summary>
public sealed class BotRegistrationService(
    IBotRegistry botRegistry,
    IBehaviorCatalog behaviorCatalog,
    ITokenProtector tokenProtector,
    IBotTokenValidator botTokenValidator,
    IBotLifecycle botLifecycle,
    BotFailureCounter failureCounter)
{
    public async Task<Result<BotRegistration>> Register(
        string label, string behaviorKey, string token, CancellationToken cancellationToken = default)
    {
        if (!behaviorCatalog.TryGet(behaviorKey, out _))
        {
            return new Error($"Unknown behavior \"{behaviorKey}\".");
        }

        var validation = await botTokenValidator.Validate(token, cancellationToken);
        if (validation.IsFailed)
        {
            return new Error(validation.Errors.First().Message);
        }

        var (telegramBotId, username) = validation.Value;
        var encryptedToken = tokenProtector.Protect(token);

        var addResult = await botRegistry.Add(telegramBotId, username, label, behaviorKey, encryptedToken, cancellationToken);
        if (addResult.IsFailed)
        {
            return addResult;
        }

        var registration = addResult.Value;
        System.Diagnostics.Activity.Current?.SetTag("bot.id", registration.Id);

        var started = await TryStart(registration.Id, token, cancellationToken);
        if (started.IsFailed)
        {
            // The row is seconds old and nothing else can be pointing at it yet, so taking it back out is
            // what keeps "registered and running, or not registered at all" true.
            await botLifecycle.Remove(registration.Id, cancellationToken);
            await botRegistry.Remove(registration.Id, cancellationToken);

            return Result.Fail<BotRegistration>(started.Errors);
        }

        return registration;
    }

    public Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default) =>
        botRegistry.List(cancellationToken);

    public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default) =>
        botRegistry.Get(botId, cancellationToken);

    public async Task<Result> Disable(long botId, CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Activity.Current?.SetTag("bot.id", botId);
        var updateResult = await botRegistry.UpdateStatus(botId, BotStatus.Disabled, cancellationToken);
        if (updateResult.IsFailed)
        {
            return updateResult;
        }

        await botLifecycle.Stop(botId, cancellationToken);

        // A bot the operator took down must come back without the failure streak it went down with —
        // otherwise its first failure after being re-enabled flags it, on a count about the deployment it
        // was taken down from.
        failureCounter.Forget(botId);

        return Result.Ok();
    }

    public async Task<Result> Enable(long botId, CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Activity.Current?.SetTag("bot.id", botId);
        var encryptedToken = await botRegistry.GetEncryptedToken(botId, cancellationToken);
        if (encryptedToken is null)
        {
            return new Error($"Bot {botId} was not found.");
        }

        var updateResult = await botRegistry.UpdateStatus(botId, BotStatus.Active, cancellationToken);
        if (updateResult.IsFailed)
        {
            return updateResult;
        }

        var token = tokenProtector.Unprotect(encryptedToken);

        var started = await TryStart(botId, token, cancellationToken);
        if (started.IsFailed)
        {
            // Back to Disabled: an Active bot that is not running is the one state an operator cannot see
            // and cannot act on, because everything reports it as healthy.
            await botRegistry.UpdateStatus(botId, BotStatus.Disabled, cancellationToken);

            return started;
        }

        return Result.Ok();
    }

    public async Task<Result> RotateToken(long botId, string newToken, CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Activity.Current?.SetTag("bot.id", botId);
        var registration = await botRegistry.Get(botId, cancellationToken);
        if (registration is null)
        {
            return new Error($"Bot {botId} was not found.");
        }

        var validation = await botTokenValidator.Validate(newToken, cancellationToken);
        if (validation.IsFailed)
        {
            return new Error(validation.Errors.First().Message);
        }

        var (telegramBotId, _) = validation.Value;
        var encryptedToken = tokenProtector.Protect(newToken);

        var updateResult = await botRegistry.UpdateToken(botId, telegramBotId, encryptedToken, cancellationToken);
        if (updateResult.IsFailed)
        {
            return updateResult;
        }

        // Not rolled back, unlike the two above: the operator rotated for a reason and the superseded
        // token may already be revoked, so restoring it would be restoring a credential that no longer
        // works. The new one is valid — it was just checked against Telegram — and the message says so,
        // leaving "enable it again" as the repair.
        var started = await TryStart(botId, newToken, cancellationToken);

        return started.IsFailed
            ? new Error(
                $"The new token was saved, but bot {botId}'s receiver could not be restarted: "
                + $"{started.Errors.First().Message}")
            : Result.Ok();
    }

    public async Task<Result> Remove(long botId, CancellationToken cancellationToken = default)
    {
        System.Diagnostics.Activity.Current?.SetTag("bot.id", botId);
        var removeResult = await botRegistry.Remove(botId, cancellationToken);
        if (removeResult.IsFailed)
        {
            return removeResult;
        }

        await botLifecycle.Remove(botId, cancellationToken);
        failureCounter.Forget(botId);

        return Result.Ok();
    }

    /// <summary>
    /// Brings a receiver up, turning the one step that reaches Telegram into a <see cref="Result"/> its
    /// callers can undo their own work on. <see cref="IBotLifecycle"/> throws rather than returning a
    /// failure — it is the boundary where a webhook registration either happened or did not — so without
    /// this every caller's rollback would be a <c>catch</c> block, and a missed one an opaque 500 with a
    /// half-registered bot behind it.
    /// </summary>
    private async Task<Result> TryStart(long botId, string token, CancellationToken cancellationToken)
    {
        try
        {
            await botLifecycle.Start(botId, token, cancellationToken);

            return Result.Ok();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message, never the exception: a bot token is carried in the Telegram request path, and
            // an inner exception can surface the URI into a log meant to stay token-free.
            return new Error($"The bot's receiver could not be started: {exception.Message}");
        }
    }
}