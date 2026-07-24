using Telegram.Bot;
using TelegramBotPlatform.Public.Behaviors;

namespace ReverseBehavior;

/// <summary>
/// A minimal sample behavior extension demonstrating the Behavior SDK. Replies to every text message with
/// the text reversed. Build this project, then upload the resulting <c>ReverseBehavior.dll</c> via
/// <c>POST /admin/behaviors</c> to add a brand-new behavior type to a running platform — no redeploy — then
/// register a bot with <c>behaviorKey: "reverse"</c>.
/// </summary>
public sealed class ReverseBotBehavior : IBotBehavior
{
    public string Key => "reverse";

    public string DisplayName => "Reverse";

    public string ContractVersion => BehaviorContractVersion.Current;

    public async Task HandleUpdateAsync(IBotUpdateContext context, CancellationToken cancellationToken)
    {
        var message = context.Update.Message;
        if (message?.Text is not { } text)
        {
            return;
        }

        var reversed = new string(text.Reverse().ToArray());
        await context.Client.SendMessage(message.Chat.Id, reversed, cancellationToken: cancellationToken);
    }
}