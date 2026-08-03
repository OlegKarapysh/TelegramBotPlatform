using Telegram.Bot.Requests;
using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// How a bot whose behavior keeps failing is reported. The contract is deliberately mild — the bot is
/// flagged, never throttled and never disabled — and it spans the whole path from a webhook POST to a
/// status change an operator can see, which is why it is checked here rather than against the tracker.
/// </summary>
public class BotHealthTests
{
    private const string FirstBotToken = "111:health-first-token";
    private const string SecondBotToken = "222:health-second-token";

    [Fact]
    public async Task RepeatedFailures_MarkTheBot_Failing()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);

        await Deliver(bot, BotHealthTracker.FailureThreshold);
        await behavior.WaitForHandled(BotHealthTracker.FailureThreshold, TestContext.Current.CancellationToken);

        await WaitForStatus(bot, BotStatus.Failing);
    }

    [Fact]
    public async Task AFailingBot_KeepsRunning_AtNormalCadence()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);

        await Deliver(bot, BotHealthTracker.FailureThreshold);
        await WaitForStatus(bot, BotStatus.Failing);

        // Flagged, not throttled and not taken down: the operator decides what to do about it, and in the
        // meantime the bot is still reachable, still registered with Telegram, and still being served.
        Assert.Contains(bot.Id, platform.Clients.LiveBotIds);
        Assert.Empty(bot.Client.RequestsOf<DeleteWebhookRequest>());

        var handledBefore = behavior.Handled;
        Assert.Equal(HttpStatusCode.OK, (await bot.Deliver("another")).StatusCode);
        await behavior.WaitForHandled(handledBefore + 1, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASuccessAfterFailures_ClearsTheFailingStatus()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);

        await Deliver(bot, BotHealthTracker.FailureThreshold);
        await WaitForStatus(bot, BotStatus.Failing);

        behavior.ShouldThrow = false;
        var replies = await bot.DeliverAndAwaitReply("recovered", TestContext.Current.CancellationToken);

        // Recovery needs no operator action — the next update that works clears the flag by itself.
        Assert.Equal(["handled: recovered"], replies);
        await WaitForStatus(bot, BotStatus.Active);
    }

    [Fact]
    public async Task FailuresAreCountedPerBot_NotAcrossTheFleet()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var first = await platform.RegisterBot("First", behavior.Key, FirstBotToken);
        var second = await platform.RegisterBot("Second", behavior.Key, SecondBotToken);

        // Both bots run the same failing behavior, and between them they fail more than the threshold —
        // but neither reaches it alone yet.
        for (var attempt = 0; attempt < BotHealthTracker.FailureThreshold - 1; attempt++)
        {
            await Deliver(first, 1);
            await Deliver(second, 1);
        }

        await behavior.WaitForHandled(2 * (BotHealthTracker.FailureThreshold - 1), TestContext.Current.CancellationToken);

        // Only the first bot is pushed over.
        await Deliver(first, 1);
        await WaitForStatus(first, BotStatus.Failing);

        // A fleet-wide counter would have flagged the second bot on its own second failure, which the
        // platform had already handled and recorded before the first bot was pushed over here.
        Assert.Equal(nameof(BotStatus.Active), (await second.Current()).Status);
    }

    private static async Task Deliver(HostedBot bot, int count)
    {
        for (var delivered = 0; delivered < count; delivered++)
        {
            await AdminApi.AssertStatus(await bot.Deliver($"update {delivered}"), HttpStatusCode.OK);
        }
    }

    private static Task WaitForStatus(HostedBot bot, BotStatus expected) =>
        Wait.Until(
            async () => (await bot.Current()).Status == expected.ToString(),
            () => $"bot {bot.Id} to be reported {expected}",
            TestContext.Current.CancellationToken);
}