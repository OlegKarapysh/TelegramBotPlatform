namespace TelegramBotPlatform.Public;

/// <summary>
/// Validates a candidate bot token against Telegram before it is ever persisted. A dedicated seam
/// (rather than constructing a Telegram client inline) so <c>BotRegistrationService</c> stays
/// unit-testable with a fake, never touching the network in tests.
/// </summary>
public interface IBotTokenValidator
{
    Task<Result<(long TelegramBotId, string? Username)>> Validate(string token, CancellationToken cancellationToken = default);
}