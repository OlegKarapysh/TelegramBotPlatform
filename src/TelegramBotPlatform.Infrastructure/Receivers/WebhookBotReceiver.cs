namespace TelegramBotPlatform.Infrastructure.Receivers;

/// <summary>Handles one incoming webhook POST for a bot (production/non-Development). Resolved per-request scope.</summary>
public sealed class WebhookBotReceiver(IPublishEndpoint publishEndpoint)
{
    public Task Handle(long botId, Update update, CancellationToken cancellationToken) =>
        publishEndpoint.Publish(new BotUpdate(botId, update), cancellationToken);
}