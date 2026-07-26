namespace TelegramBotPlatform.WebApi.Behaviors;

/// <summary>
/// The platform's built-in demo behavior. Replies to <c>/start</c> with a greeting and echoes every other
/// text message back. Registered as a "built-in" behavior at host startup so a bot can be assigned the
/// <c>echo</c> behavior the moment the platform is running — no plugin upload required.
/// <para>
/// It is intentionally trivial: a real deployment replaces or augments it with its own built-in behaviors
/// (composed here in the host) and operator-uploaded behavior extensions (see <c>samples/ReverseBehavior</c>).
/// </para>
/// </summary>
public sealed class EchoBehavior : IBotBehavior
{
    public string Key => "echo";

    public string DisplayName => "Echo";

    public string ContractVersion => BehaviorContractVersion.Current;

    public async Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken)
    {
        var message = context.Update.Message;
        if (message?.Text is not { } text)
        {
            return;
        }

        var reply = text.Trim() == "/start"
            ? "Hi! I'm an echo bot running on TelegramBotPlatform. Send me any text and I'll echo it back."
            : $"echo: {text}";

        await context.Client.SendMessage(message.Chat.Id, reply, cancellationToken: cancellationToken);
    }
}