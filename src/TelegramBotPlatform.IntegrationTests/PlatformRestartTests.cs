namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// What survives the process being replaced — the case a container deployment hits on every release.
/// A second host is started against the first one's database and extension store, which is exactly what
/// a redeployed task does, and nothing is carried over in memory between them.
/// </summary>
public class PlatformRestartTests
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
            await AdminApi.AssertStatus(await before.Admin.DisableBot(disabled.Id), HttpStatusCode.OK);

            (activeBotId, disabledBotId) = (active.Id, disabled.Id);
        }

        await using var after = Start(database, store);

        // Only the enabled bot is brought back, and it is brought back with the token it was registered
        // with — decrypted by a key ring this process did not generate, but read back out of the same
        // database. Nothing was re-registered by hand.
        Assert.Equal([activeBotId], after.Clients.LiveBotIds);
        Assert.Equal(ActiveBotToken, after.Clients.Client(activeBotId).Token);

        var restored = await after.Bot(activeBotId);
        Assert.Equal(
            ["echo: after the restart"],
            await restored.DeliverAndAwaitReply("after the restart", TestContext.Current.CancellationToken));

        // The disabled bot is still on record — disabled, not forgotten, and ready to be enabled again.
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
            await AdminApi.AssertStatus(await before.Admin.DisableBot(registered.Id), HttpStatusCode.OK);
            botId = registered.Id;
        }

        await using var after = Start(database, store);
        Assert.Empty(after.Clients.LiveBotIds);

        await AdminApi.AssertStatus(await after.Admin.EnableBot(botId), HttpStatusCode.OK);

        // The token outlived both the disable and the restart, so enabling is enough to serve again.
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

            Assert.Equal(["cba"], await reverse.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken));

            (reverseBotId, echoBotId) = (reverse.Id, echo.Id);
        }

        // The stored package is now something the loader cannot use — a truncated object, or a bad build
        // pushed into the bucket by some other route.
        await store.Write(SamplePlugin.FileName, SamplePlugin.Corrupt, TestContext.Current.CancellationToken);

        await using var after = Start(database, store);

        // One package that will not load is contained, not fatal: it is named and explained, so an
        // operator can repair it, while the host came up and everything else works.
        var behaviors = await after.Admin.ListBehaviors();
        var package = Assert.Single(behaviors.Packages);
        Assert.Equal(SamplePlugin.FileName, package.PackageName);
        Assert.False(package.Loaded);
        Assert.NotNull(package.Error);
        Assert.DoesNotContain(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);

        var echoBot = await after.Bot(echoBotId);
        Assert.Equal(["echo: fine"], await echoBot.DeliverAndAwaitReply("fine", TestContext.Current.CancellationToken));

        // The orphaned bot is still registered and still running — its updates simply have nowhere to go.
        // That its behavior is gone is settled by the catalog assertion above; this adds that an update it
        // cannot route is answered normally rather than faulting the endpoint.
        var reverseBot = await after.Bot(reverseBotId);
        Assert.Equal(HttpStatusCode.OK, (await reverseBot.Deliver("abc")).StatusCode);
        Assert.Empty(reverseBot.Client.SentMessages);
    }

    private static PlatformTestHost Start(PlatformDatabase database, TemporaryDirectory store) =>
        PlatformTestHost.Start(new PlatformTestSettings { Database = database, PluginsDirectory = store.Path });
}