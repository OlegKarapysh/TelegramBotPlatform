using FluentResults;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

public sealed class BotHealthTrackerTests
{
    [Fact]
    public async Task RecordFailure_DoesNotMarkFailing_BelowThreshold()
    {
        var registry = Seeded(BotStatus.Active);
        var tracker = CreateTracker(registry);

        await Fail(tracker, BotHealthTracker.FailureThreshold - 1);

        Assert.Equal(BotStatus.Active, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordFailure_MarksFailing_AtThreshold()
    {
        var registry = Seeded(BotStatus.Active);
        var tracker = CreateTracker(registry);

        await Fail(tracker, BotHealthTracker.FailureThreshold);

        Assert.Equal(BotStatus.Failing, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordFailure_NeverTouchesADisabledBot()
    {
        var registry = Seeded(BotStatus.Disabled);
        var tracker = CreateTracker(registry);

        await Fail(tracker, BotHealthTracker.FailureThreshold + 5);

        Assert.Equal(BotStatus.Disabled, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordFailure_KeepsSeparateCounters_PerBot()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        registry.Seed(Registration(2, BotStatus.Active));
        var tracker = CreateTracker(registry);

        await Fail(tracker, BotHealthTracker.FailureThreshold);

        Assert.Equal(BotStatus.Failing, await StatusOf(registry, botId: 1));
        Assert.Equal(BotStatus.Active, await StatusOf(registry, botId: 2));
    }

    [Fact]
    public async Task RecordFailure_Accumulates_AcrossTrackerInstances()
    {
        // The platform resolves a tracker per update, in that update's own DI scope. Counting on the
        // tracker leaves every other test here passing while making Failing unreachable in the host.
        var registry = Seeded(BotStatus.Active);
        var counter = new BotFailureCounter();

        for (var update = 0; update < BotHealthTracker.FailureThreshold; update++)
        {
            await CreateTracker(registry, counter).RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Failing, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordSuccess_ResetsFailingBackToActive()
    {
        var registry = Seeded(BotStatus.Active);
        var tracker = CreateTracker(registry);
        await Fail(tracker, BotHealthTracker.FailureThreshold);

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(BotStatus.Active, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordSuccess_ClearsAFailingStatus_LeftBehindByAPreviousProcess()
    {
        // A restart: the bot is Failing on record — durable — while the counts that put it there died
        // with the process that kept them. Deciding on the counter alone leaves it flagged for as long as
        // it keeps working, releasable only by three fresh failures followed by a success.
        var registry = Seeded(BotStatus.Failing);
        var tracker = CreateTracker(registry, new BotFailureCounter());

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(BotStatus.Active, await StatusOf(registry));
    }

    [Fact]
    public async Task RecordSuccess_NeverRevivesADisabledBot()
    {
        var registry = Seeded(BotStatus.Disabled);
        var tracker = CreateTracker(registry);

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(BotStatus.Disabled, await StatusOf(registry));
        Assert.Equal(0, registry.UpdateStatusCallCount);
    }

    [Fact]
    public async Task RecordSuccess_WritesNothing_WhenThereWereNoPriorFailures()
    {
        var registry = Seeded(BotStatus.Active);
        var tracker = CreateTracker(registry);

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(0, registry.UpdateStatusCallCount);
    }

    [Fact]
    public async Task RecordSuccess_ReadsTheRegistryOnce_ForABotThatKeepsWorking()
    {
        // Reconciling is a once-per-process cost per bot, not a database round trip added to every update
        // every healthy bot on the platform handles.
        var registry = Seeded(BotStatus.Active);
        var counter = new BotFailureCounter();

        for (var update = 0; update < 20; update++)
        {
            await CreateTracker(registry, counter).RecordSuccess(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, registry.GetCallCount);
        Assert.Equal(0, registry.UpdateStatusCallCount);
    }

    private static async Task Fail(BotHealthTracker tracker, int times, long botId = 1)
    {
        for (var failure = 0; failure < times; failure++)
        {
            await tracker.RecordFailure(botId, TestContext.Current.CancellationToken);
        }
    }

    private static FakeBotRegistry Seeded(BotStatus status)
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, status));

        return registry;
    }

    private static async Task<BotStatus> StatusOf(FakeBotRegistry registry, long botId = 1) =>
        (await registry.Get(botId, TestContext.Current.CancellationToken))!.Status;

    private static BotHealthTracker CreateTracker(IBotRegistry registry, BotFailureCounter? counter = null) =>
        new(registry, counter ?? new BotFailureCounter(), NullLogger<BotHealthTracker>.Instance);

    private static BotRegistration Registration(long id, BotStatus status) =>
        new(id, TelegramBotId: 1000 + id, Username: "bot", Label: "Bot", "echo", status, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class FakeBotRegistry : IBotRegistry
    {
        private readonly Dictionary<long, BotRegistration> _bots = new();

        public int UpdateStatusCallCount { get; private set; }

        /// <summary>Reads too, not just writes — the success path's cost is what makes it the hot one.</summary>
        public int GetCallCount { get; private set; }

        public void Seed(BotRegistration registration) => _bots[registration.Id] = registration;

        public Task<Result<BotRegistration>> Add(long telegramBotId, string? username, string label, string behaviorKey, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default)
        {
            GetCallCount++;

            return Task.FromResult(_bots.GetValueOrDefault(botId));
        }

        public Task<byte[]?> GetEncryptedToken(long botId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateStatus(long botId, BotStatus status, CancellationToken cancellationToken = default)
        {
            UpdateStatusCallCount++;
            if (_bots.TryGetValue(botId, out var registration))
            {
                _bots[botId] = registration with { Status = status };
            }

            return Task.FromResult(Result.Ok());
        }

        public Task<Result> UpdateToken(long botId, long telegramBotId, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> Remove(long botId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}