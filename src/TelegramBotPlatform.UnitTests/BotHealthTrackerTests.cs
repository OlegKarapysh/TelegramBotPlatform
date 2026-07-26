using FluentResults;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

public class BotHealthTrackerTests
{
    [Fact]
    public async Task RecordFailure_DoesNotMarkFailing_BelowThreshold()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);

        for (var i = 0; i < BotHealthTracker.FailureThreshold - 1; i++)
        {
            await tracker.RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Active, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RecordFailure_MarksFailing_AtThreshold()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);

        for (var i = 0; i < BotHealthTracker.FailureThreshold; i++)
        {
            await tracker.RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Failing, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RecordFailure_NeverTouchesADisabledBot()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Disabled));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);

        for (var i = 0; i < BotHealthTracker.FailureThreshold + 5; i++)
        {
            await tracker.RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Disabled, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RecordSuccess_ResetsFailingBackToActive()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);
        for (var i = 0; i < BotHealthTracker.FailureThreshold; i++)
        {
            await tracker.RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Failing, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(BotStatus.Active, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RecordSuccess_IsANoOp_WhenThereWereNoPriorFailures()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);

        await tracker.RecordSuccess(1, TestContext.Current.CancellationToken);

        Assert.Equal(0, registry.UpdateStatusCallCount);
    }

    [Fact]
    public async Task RecordFailure_KeepsSeparateCounters_PerBot()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, BotStatus.Active));
        registry.Seed(Registration(2, BotStatus.Active));
        var tracker = new BotHealthTracker(registry, NullLogger<BotHealthTracker>.Instance);

        for (var i = 0; i < BotHealthTracker.FailureThreshold; i++)
        {
            await tracker.RecordFailure(1, TestContext.Current.CancellationToken);
        }

        Assert.Equal(BotStatus.Failing, (await registry.Get(1, TestContext.Current.CancellationToken))!.Status);
        Assert.Equal(BotStatus.Active, (await registry.Get(2, TestContext.Current.CancellationToken))!.Status);
    }

    private static BotRegistration Registration(long id, BotStatus status) =>
        new(id, TelegramBotId: 1000 + id, Username: "bot", Label: "Bot", "echo", status, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class FakeBotRegistry : IBotRegistry
    {
        private readonly Dictionary<long, BotRegistration> _bots = new();

        public int UpdateStatusCallCount { get; private set; }

        public void Seed(BotRegistration registration) => _bots[registration.Id] = registration;

        public Task<Result<BotRegistration>> Add(long telegramBotId, string? username, string label, string behaviorKey, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.GetValueOrDefault(botId));

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