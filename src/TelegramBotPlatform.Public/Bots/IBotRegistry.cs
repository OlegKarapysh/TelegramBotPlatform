namespace TelegramBotPlatform.Public.Bots;

/// <summary>
/// The durable store of registered bots (schema <c>platform</c>). Pure persistence — validation
/// (behavior catalog lookup, Telegram token check) is the caller's (<c>BotRegistrationService</c>) job.
/// </summary>
public interface IBotRegistry
{
    /// <summary>Fails if <paramref name="telegramBotId"/> is already registered.</summary>
    Task<Result<BotRegistration>> Add(
        long telegramBotId,
        string? username,
        string label,
        string behaviorKey,
        byte[] encryptedToken,
        CancellationToken cancellationToken = default);

    Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default);

    /// <summary>The bot's token ciphertext, for internal use only (building its Telegram client). Never exposed via the admin API.</summary>
    Task<byte[]?> GetEncryptedToken(long botId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default);

    Task<Result> UpdateStatus(long botId, BotStatus status, CancellationToken cancellationToken = default);

    /// <summary>Fails if <paramref name="telegramBotId"/> does not match the bot already registered under <paramref name="botId"/>.</summary>
    Task<Result> UpdateToken(
        long botId, long telegramBotId, byte[] encryptedToken, CancellationToken cancellationToken = default);

    Task<Result> Remove(long botId, CancellationToken cancellationToken = default);
}