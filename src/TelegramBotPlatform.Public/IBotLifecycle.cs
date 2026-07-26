namespace TelegramBotPlatform.Public;

/// <summary>
/// Starts/stops a bot's receiver (long-polling or webhook). Implemented by the Infrastructure-level
/// <c>BotSupervisor</c>; depended on from Application (<c>BotRegistrationService</c>) through this
/// abstraction so Application never references Infrastructure directly.
/// </summary>
public interface IBotLifecycle
{
    Task Start(long botId, string token, CancellationToken cancellationToken = default);
    Task Stop(long botId, CancellationToken cancellationToken = default);
    Task Remove(long botId, CancellationToken cancellationToken = default);
}