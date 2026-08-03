namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// A bot registered through the admin API and now running on the platform, together with everything a
/// test needs to talk to it as Telegram would.
/// <para>
/// The webhook path and secret are read back off the <c>setWebhook</c> call the supervisor made, never
/// re-derived here — so delivering through them exercises the agreement between the URL and secret the
/// platform <em>published</em> and the ones its endpoint <em>accepts</em>.
/// </para>
/// </summary>
public sealed record HostedBot(PlatformTestHost Platform, BotResponse Registration)
{
    public long Id => Registration.BotId;

    /// <summary>This bot's own Telegram client, recording everything the platform sends through it.</summary>
    public RecordingTelegramBotClient Client => Platform.Clients.Client(Id);

    public string WebhookPath => Platform.RegisteredWebhookPath(Id);

    public string WebhookSecret => Platform.RegisteredWebhookSecret(Id);

    /// <summary>Delivers one text message to this bot exactly as Telegram's webhook does.</summary>
    public Task<HttpResponseMessage> Deliver(string text) =>
        Platform.Anonymous.PostWebhook(WebhookPath, WebhookSecret, text);

    /// <summary>Delivers a message and waits for the bot's reply, returning every reply it has sent so far.</summary>
    public async Task<IReadOnlyList<string>> DeliverAndAwaitReply(string text, CancellationToken cancellationToken)
    {
        var expected = Client.SentMessages.Count + 1;

        var response = await Deliver(text);
        await AdminApi.AssertStatus(response, HttpStatusCode.OK);

        return await Client.WaitForSentMessages(expected, cancellationToken);
    }

    /// <summary>This bot's current server-side record — the status a lifecycle or health change shows up in.</summary>
    public Task<BotResponse> Current() => Platform.Admin.GetBotOk(Id);
}