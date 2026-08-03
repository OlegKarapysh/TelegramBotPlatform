using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using TelegramBotPlatform.Infrastructure.Receivers;

namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// The platform's main path: Telegram POSTs an update, and it comes out as a reply from the right bot,
/// through the right behavior, on the right credentials. Every layer participates — endpoint, secret
/// check, registry lookup, MassTransit, the bot-scope filter, the router, the catalog — so this is the
/// only level at which the path can be checked at all.
/// </summary>
public class WebhookIngestionTests
{
    private const string EchoBotToken = "111:echo-bot-token";
    private const string OtherBotToken = "222:other-bot-token";

    [Theory]
    [InlineData("hello", "echo: hello")]
    [InlineData("/start", "Hi! I'm an echo bot running on TelegramBotPlatform. Send me any text and I'll echo it back.")]
    public async Task AnUpdate_IsAnswered_ByTheBotsAssignedBehavior(string sent, string expectedReply)
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        var replies = await bot.DeliverAndAwaitReply(sent, TestContext.Current.CancellationToken);

        Assert.Equal([expectedReply], replies);
    }

    [Fact]
    public async Task TheWebhookThePlatformPublished_IsTheOneItServes()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        // Both the path and the secret come from the setWebhook call the supervisor made — nothing here
        // re-derives them. If the URL the platform advertises to Telegram and the route it maps ever
        // disagreed, or the secret it registered and the one it checks were derived differently, this
        // would stop being an accepted request.
        var url = bot.Client.SingleRequest<SetWebhookRequest>();
        var response = await platform.Anonymous.PostWebhook(new Uri(url.Url).AbsolutePath, url.SecretToken, "hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["echo: hello"], await bot.Client.WaitForSentMessages(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EachBotsUpdate_GoesToItsOwnBehavior_OnItsOwnClient()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);

        var echoBot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);
        var reverseBot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, OtherBotToken);

        await AdminApi.AssertStatus(await echoBot.Deliver("abc"), HttpStatusCode.OK);
        await AdminApi.AssertStatus(await reverseBot.Deliver("abc"), HttpStatusCode.OK);

        // Same text, two bots, two behaviors — and neither reply came out of the other bot's client.
        Assert.Equal(["echo: abc"], await echoBot.Client.WaitForSentMessages(1, TestContext.Current.CancellationToken));
        Assert.Equal(["cba"], await reverseBot.Client.WaitForSentMessages(1, TestContext.Current.CancellationToken));
        Assert.Equal(EchoBotToken, echoBot.Client.Token);
        Assert.Equal(OtherBotToken, reverseBot.Client.Token);
    }

    [Fact]
    public async Task AWebhookWithNoSecret_IsUnauthorized_AndTheUpdateIsNeverDispatched()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        var rejected = await platform.Anonymous.PostWebhook(bot.WebhookPath, secret: null, "smuggled");

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);

        // A negative is only worth asserting once something has provably flowed through: this delivery is
        // accepted and answered, and the rejected one still produced nothing.
        Assert.Equal(["echo: legitimate"], await bot.DeliverAndAwaitReply("legitimate", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWebhookWithTheWrongSecret_IsUnauthorized_AndTheUpdateIsNeverDispatched()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        var rejected = await platform.Anonymous.PostWebhook(bot.WebhookPath, "not-this-bots-secret", "smuggled");

        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(["echo: legitimate"], await bot.DeliverAndAwaitReply("legitimate", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OneBotsSecret_DoesNotOpenAnotherBotsWebhook()
    {
        await using var platform = PlatformTestHost.Start();
        var first = await platform.RegisterBot("First", "echo", EchoBotToken);
        var second = await platform.RegisterBot("Second", "echo", OtherBotToken);

        var response = await platform.Anonymous.PostWebhook(second.WebhookPath, first.WebhookSecret, "hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(["echo: hello"], await second.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken));
        Assert.Empty(first.Client.SentMessages);
    }

    [Fact]
    public async Task AWrongSecret_LooksTheSame_WhetherOrNotTheBotExists()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        var registered = await platform.Anonymous.PostWebhook(bot.WebhookPath, "wrong-secret", "hello");
        var neverRegistered = await platform.Anonymous.PostWebhook("/telegram-bot/webhook/987654", "wrong-secret", "hello");

        // Answering NotFound for one and Unauthorized for the other would let anyone without the secret
        // enumerate which bot ids this platform hosts.
        Assert.Equal(HttpStatusCode.Unauthorized, registered.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, neverRegistered.StatusCode);
    }

    [Fact]
    public async Task AnUpdateForAnUnregisteredBot_IsNotFound_EvenWithTheRightSecret()
    {
        await using var platform = PlatformTestHost.Start();

        // The secret is derived from the bot id, so it exists for ids that were never registered. Asking
        // the platform's own provider for it is the only way to reach the branch past the secret check.
        var secret = platform.Services.GetRequiredService<WebhookSecretProvider>().GetSecret(987654);

        var response = await platform.Anonymous.PostWebhook("/telegram-bot/webhook/987654", secret, "hello");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ADisabledBot_RejectsUpdates_ThatTelegramIsStillRetrying()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);
        var secret = bot.WebhookSecret;
        await AdminApi.AssertStatus(await platform.Admin.DisableBot(bot.Id), HttpStatusCode.OK);

        // Disabling deletes the webhook, but deliveries already in flight keep arriving with a valid
        // secret. A disabled bot must not process them.
        var response = await platform.Anonymous.PostWebhook($"/telegram-bot/webhook/{bot.Id}", secret, "hello");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(bot.Client.SentMessages);
    }

    [Fact]
    public async Task AnUpdateCarryingNoMessage_IsAccepted_AndLeavesThePipelineWorking()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Echo bot", "echo", EchoBotToken);

        // Telegram sends plenty of updates a text behavior has nothing to say about.
        var response = await platform.Anonymous.PostWebhookRaw(
            bot.WebhookPath, bot.WebhookSecret, TelegramPayload.NonMessageUpdate());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["echo: hello"], await bot.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheUpdatesScope_ResolvesTheClient_OfTheBotThatOwnsIt()
    {
        await using var platform = PlatformTestHost.Start();
        platform.RegisterBehavior(new ScopeResolvingBehavior());
        var first = await platform.RegisterBot("First", ScopeResolvingBehavior.BehaviorKey, EchoBotToken);
        var second = await platform.RegisterBot("Second", ScopeResolvingBehavior.BehaviorKey, OtherBotToken);

        await AdminApi.AssertStatus(await first.Deliver("hello"), HttpStatusCode.OK);
        await AdminApi.AssertStatus(await second.Deliver("hello"), HttpStatusCode.OK);

        // Nothing tells the scope which bot it belongs to except the consume filter, which sets it from the
        // message before the consumer is built. Get that wrong and a bot answers with someone else's
        // credentials — or, for an id nothing is registered under, cannot answer at all.
        Assert.Equal(
            [$"resolved for bot {first.Id}"],
            await first.Client.WaitForSentMessages(1, TestContext.Current.CancellationToken));
        Assert.Equal(
            [$"resolved for bot {second.Id}"],
            await second.Client.WaitForSentMessages(1, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Reaches into the update's own DI scope for a bot-scoped <see cref="ITelegramBotClient"/> instead of
    /// using the one it was handed. That resolution is the platform's headline convenience — it is what
    /// lets a behavior or a consumer constructor-inject the right bot's client — and it only works because
    /// <c>BotScopeFilter</c> populates the scope first, so nothing below the composed host can check it.
    /// </summary>
    private sealed class ScopeResolvingBehavior : IBotBehavior
    {
        public const string BehaviorKey = "test-scope-resolving";

        public string Key => BehaviorKey;

        public string DisplayName => "Scope resolving";

        public string ContractVersion => BehaviorContractVersion.Current;

        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken)
        {
            var scopedClient = context.Services.GetRequiredService<ITelegramBotClient>();
            var scopedBot = context.Services.GetRequiredService<IBotContext>();

            return scopedClient.SendMessage(
                context.Update.Message!.Chat.Id,
                $"resolved for bot {scopedBot.BotId}",
                cancellationToken: cancellationToken);
        }
    }

    [Fact]
    public async Task AFailingBehavior_IsContained_AndOtherBotsKeepBeingServed()
    {
        await using var platform = PlatformTestHost.Start();
        var failing = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });

        var brokenBot = await platform.RegisterBot("Broken", failing.Key, EchoBotToken);
        var healthyBot = await platform.RegisterBot("Healthy", "echo", OtherBotToken);

        await AdminApi.AssertStatus(await brokenBot.Deliver("boom"), HttpStatusCode.OK);
        await failing.WaitForHandled(1, TestContext.Current.CancellationToken);

        // The throw is swallowed at the router, so the bus keeps draining and every other bot is unaffected.
        Assert.Equal(["echo: fine"], await healthyBot.DeliverAndAwaitReply("fine", TestContext.Current.CancellationToken));
        Assert.Empty(brokenBot.Client.SentMessages);
    }
}