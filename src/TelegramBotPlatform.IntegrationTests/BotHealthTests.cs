using Telegram.Bot.Requests;
using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// How a bot whose behavior keeps failing is reported. The contract is deliberately mild — the bot is
/// flagged, never throttled and never disabled — and it spans the whole path from a webhook POST to a
/// status change an operator can see, which is why it is checked here rather than against the tracker.
/// </summary>
public sealed class BotHealthTests
{
    private const string FirstBotToken = "111:health-first-token";
    private const string SecondBotToken = "222:health-second-token";

    [Fact]
    public async Task RepeatedFailures_MarkTheBot_Failing()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);

        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold);

        await WaitForStatus(bot, BotStatus.Failing);
    }

    [Fact]
    public async Task AFailingBot_KeepsRunning_AtNormalCadence()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);
        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold);
        await WaitForStatus(bot, BotStatus.Failing);
        var handledBefore = behavior.Handled;
        var response = await bot.Deliver("another");
        // Flagged, not throttled and not taken down: the operator decides what to do about it, and
        // meanwhile the bot is still reachable, still registered with Telegram, and still being served.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await behavior.WaitForHandled(handledBefore + 1, TestContext.Current.CancellationToken);
        Assert.Contains(bot.Id, platform.Clients.LiveBotIds);
        Assert.Empty(bot.Client.RequestsOf<DeleteWebhookRequest>());
    }

    [Fact]
    public async Task ASuccessAfterFailures_ClearsTheFailingStatus()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);
        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold);
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
        await Deliver(first, behavior, BotHealthTracker.FailureThreshold - 1);
        await Deliver(second, behavior, BotHealthTracker.FailureThreshold - 1);

        await Deliver(first, behavior, 1);

        // A fleet-wide counter would have flagged the second bot on its own second failure, long before
        // this last update pushed the first bot over.
        await WaitForStatus(first, BotStatus.Failing);
        Assert.Equal(nameof(BotStatus.Active), (await second.Current()).Status);
    }

    [Fact]
    public async Task AReEnabledBot_IsNotFlagged_ByOneFailure()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);
        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold - 1);
        await platform.Admin.DisableBotOk(bot.Id);
        await platform.Admin.EnableBotOk(bot.Id);

        await Deliver(bot, behavior, 1);

        // Carrying the streak across the takedown would flag the bot here, on evidence from the
        // deployment the operator disabled it to fix.
        Assert.Equal(nameof(BotStatus.Active), (await bot.Current()).Status);
    }

    [Fact]
    public async Task AReEnabledBot_IsStillFlagged_ByAFullThresholdOfFailures()
    {
        await using var platform = PlatformTestHost.Start();
        var behavior = platform.RegisterBehavior(new ControllableBehavior { ShouldThrow = true });
        var bot = await platform.RegisterBot("Flaky bot", behavior.Key, FirstBotToken);
        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold - 1);
        await platform.Admin.DisableBotOk(bot.Id);
        await platform.Admin.EnableBotOk(bot.Id);

        await Deliver(bot, behavior, BotHealthTracker.FailureThreshold);

        // The count restarted rather than stopped: health tracking still works on a bot that came back.
        await WaitForStatus(bot, BotStatus.Failing);
    }

    /// <summary>
    /// Delivers <paramref name="count"/> updates and waits until the behavior has been handed all of
    /// them, so a following act never races the ones before it off the bus.
    /// </summary>
    private static async Task Deliver(HostedBot bot, ControllableBehavior behavior, int count)
    {
        var handledBefore = behavior.Handled;

        for (var delivered = 0; delivered < count; delivered++)
        {
            await bot.DeliverOk($"update {delivered}");
        }

        await behavior.WaitForHandled(handledBefore + count, TestContext.Current.CancellationToken);
    }

    private static Task WaitForStatus(HostedBot bot, BotStatus expected) =>
        Wait.Until(
            async () => (await bot.Current()).Status == expected.ToString(),
            () => $"bot {bot.Id} to be reported {expected}",
            TestContext.Current.CancellationToken);
}