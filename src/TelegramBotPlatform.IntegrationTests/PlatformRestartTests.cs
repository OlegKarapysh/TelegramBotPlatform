using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// What survives the process being replaced — the case a container deployment hits on every release.
/// A second host is started against the first one's database and extension store, which is exactly what
/// a redeployed task does, and nothing is carried over in memory between them.
/// </summary>
public sealed class PlatformRestartTests
{
    private const string ActiveBotToken = "111:survives-restart-token";
    private const string DisabledBotToken = "222:stays-down-token";
    private const string ReverseBotToken = "333:reverse-bot-token";

    [Fact]
    public async Task EveryNonDisabledBot_IsBroughtBackUp_WithTheTokenItStored()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        long activeBotId;
        long disabledBotId;

        await using (var before = Start(database, store))
        {
            var active = await before.RegisterBot("Stays up", "echo", ActiveBotToken);
            var disabled = await before.RegisterBot("Stays down", "echo", DisabledBotToken);
            await before.Admin.DisableBotOk(disabled.Id);
            (activeBotId, disabledBotId) = (active.Id, disabled.Id);
        }

        await using var after = Start(database, store);

        // Brought back with the token it was registered with — decrypted by a key ring this process did
        // not generate, but read back out of the same database. Nothing was re-registered by hand.
        Assert.Equal([activeBotId], after.Clients.LiveBotIds);
        Assert.Equal(ActiveBotToken, after.Clients.Client(activeBotId).Token);
        Assert.Equal(
            ["echo: after the restart"],
            await (await after.Bot(activeBotId)).DeliverAndAwaitReply("after the restart", TestContext.Current.CancellationToken));
        Assert.Equal(nameof(BotStatus.Disabled), (await after.Admin.GetBotOk(disabledBotId)).Status);
    }

    [Fact]
    public async Task ADisabledBot_CanBeEnabled_AfterARestart()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        long botId;

        await using (var before = Start(database, store))
        {
            var registered = await before.RegisterBot("Stays down", "echo", DisabledBotToken);
            await before.Admin.DisableBotOk(registered.Id);
            botId = registered.Id;
        }

        await using var after = Start(database, store);

        var response = await after.Admin.EnableBot(botId);

        // The bot came back down with the restart, and its token outlived both — so enabling is all it
        // takes to serve again.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bot = await after.Bot(botId);
        Assert.Equal(DisabledBotToken, bot.Client.Token);
        Assert.Equal(["echo: hello"], await bot.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AStoredExtension_ThatStopsLoading_IsReported_AndTheRestOfThePlatformStillServes()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        long reverseBotId;
        long echoBotId;

        await using (var before = Start(database, store))
        {
            await before.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
            var reverse = await before.RegisterBot("Reverse", SamplePlugin.BehaviorKey, ReverseBotToken);
            var echo = await before.RegisterBot("Echo", "echo", ActiveBotToken);
            await reverse.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken);
            (reverseBotId, echoBotId) = (reverse.Id, echo.Id);
        }

        // The stored package is now something the loader cannot use — a truncated object, or a bad build
        // pushed into the bucket by some other route.
        await store.Write(SamplePlugin.FileName, SamplePlugin.Corrupt, TestContext.Current.CancellationToken);

        await using var after = Start(database, store);

        var behaviors = await after.Admin.ListBehaviors();
        var package = Assert.Single(behaviors.Packages);
        Assert.Equal(SamplePlugin.FileName, package.PackageName);
        Assert.False(package.Loaded);
        Assert.NotNull(package.Error);
        Assert.DoesNotContain(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);
        var echoBot = await after.Bot(echoBotId);
        Assert.Equal(["echo: fine"], await echoBot.DeliverAndAwaitReply("fine", TestContext.Current.CancellationToken));
        // The orphaned bot is still registered and still running; an update it cannot route is answered
        // normally rather than faulting the endpoint.
        var reverseBot = await after.Bot(reverseBotId);
        Assert.Equal(HttpStatusCode.OK, (await reverseBot.Deliver("abc")).StatusCode);
        Assert.Empty(reverseBot.Client.SentMessages);
    }

    [Fact]
    public async Task ABotLeftFailing_IsStillFailing_AfterTheRestart()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        var botId = await LeaveABotFailing(database, store);

        await using var after = Start(database, store);

        // Durable, which is the whole point of writing the flag to the registry rather than keeping it
        // beside the counts.
        Assert.Equal(nameof(BotStatus.Failing), (await after.Admin.GetBotOk(botId)).Status);
    }

    [Fact]
    public async Task ABotLeftFailing_IsResetToActive_ByItsFirstSuccessAfterTheRestart()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        var botId = await LeaveABotFailing(database, store);
        await using var after = Start(database, store);
        after.RegisterBehavior(new ControllableBehavior());
        var restored = await after.Bot(botId);

        var replies = await restored.DeliverAndAwaitReply("fixed", TestContext.Current.CancellationToken);

        // The counts that set the flag died with the previous process, so a platform that decides "is
        // there anything to clear?" from those alone leaves this bot reported broken for as long as it
        // keeps working — visible only here, where the flag outlives the process that wrote it.
        Assert.Equal(["handled: fixed"], replies);
        await WaitForStatus(restored, BotStatus.Active);
    }

    [Fact]
    public async Task ARotatedAdminKey_ReSynchronisesEveryBotsWebhookSecret()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        string secretUnderTheOldKey;
        long botId;

        await using (var before = Start(database, store))
        {
            var bot = await before.RegisterBot("Support", "echo", ActiveBotToken);
            (botId, secretUnderTheOldKey) = (bot.Id, bot.WebhookSecret);
        }

        await using var after = Start(database, store, "a-rotated-admin-key");

        // Each secret is derived from the admin key rather than stored, so rotating it makes every secret
        // Telegram holds stale. Restore re-registering each webhook is the only thing that fixes that: an
        // optimisation skipping setWebhook for a bot already running would strand the fleet behind 401s
        // with every status still reporting Active.
        var restored = await after.Bot(botId);
        Assert.NotEqual(secretUnderTheOldKey, restored.WebhookSecret);
        Assert.Equal(
            ["echo: hello"], await restored.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASecretFromBeforeAnAdminKeyRotation_NoLongerOpensTheWebhook()
    {
        var database = new PlatformDatabase();
        using var store = TemporaryDirectory.Create();
        string secretUnderTheOldKey;
        long botId;

        await using (var before = Start(database, store))
        {
            var bot = await before.RegisterBot("Support", "echo", ActiveBotToken);
            (botId, secretUnderTheOldKey) = (bot.Id, bot.WebhookSecret);
        }

        await using var after = Start(database, store, "a-rotated-admin-key");
        var restored = await after.Bot(botId);
        await restored.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken);

        var stale = await after.Anonymous.PostWebhook(restored.WebhookPath, secretUnderTheOldKey, "stale");

        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(["echo: hello"], restored.Client.SentMessages);
    }

    /// <summary>Runs a bot's behavior into the ground on a first host, and returns the bot it left Failing.</summary>
    private static async Task<long> LeaveABotFailing(PlatformDatabase database, TemporaryDirectory store)
    {
        await using var before = Start(database, store);
        var failing = before.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await before.RegisterBot("Flaky", failing.Key, ActiveBotToken);

        for (var update = 0; update < BotHealthTracker.FailureThreshold; update++)
        {
            await bot.DeliverOk($"update {update}");
        }

        await WaitForStatus(bot, BotStatus.Failing);

        return bot.Id;
    }

    private static PlatformTestHost Start(PlatformDatabase database, TemporaryDirectory store) =>
        PlatformTestHost.Start(new PlatformTestSettings { Database = database, PluginsDirectory = store.Path });

    private static PlatformTestHost Start(PlatformDatabase database, TemporaryDirectory store, string adminApiKey) =>
        PlatformTestHost.Start(new PlatformTestSettings
        {
            Database = database,
            PluginsDirectory = store.Path,
            AdminApiKey = adminApiKey
        });

    private static Task WaitForStatus(HostedBot bot, BotStatus expected) =>
        Wait.Until(
            async () => (await bot.Current()).Status == expected.ToString(),
            () => $"bot {bot.Id} to be reported {expected}",
            TestContext.Current.CancellationToken);
}