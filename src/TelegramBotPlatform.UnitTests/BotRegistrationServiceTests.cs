using System.Text;
using FluentResults;
using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public;
using TelegramBotPlatform.Public.Behaviors;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

public sealed class BotRegistrationServiceTests
{
    private static readonly InvalidOperationException TelegramRefusedTheWebhook = new("Telegram refused the webhook.");

    [Fact]
    public async Task Register_Succeeds_AndStartsTheBot()
    {
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(lifecycle: lifecycle);

        var result = await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("echo", result.Value.BehaviorKey);
        Assert.Equal(BotStatus.Active, result.Value.Status);
        Assert.Equal((result.Value.Id, "111:token"), Assert.Single(lifecycle.Started));
    }

    [Fact]
    public async Task Register_Fails_ForUnknownBehavior()
    {
        var service = CreateService();

        var result = await service.Register("My Bot", "unknown-behavior", "111:token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("Unknown behavior", result.Errors.First().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_Fails_WhenTokenValidationFails()
    {
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(validator: new FakeTokenValidator(succeeds: false), lifecycle: lifecycle);

        var result = await service.Register("My Bot", "echo", "bad-token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Empty(lifecycle.Started);
    }

    [Fact]
    public async Task Register_Fails_WhenTelegramBotAlreadyRegistered()
    {
        var registry = new FakeBotRegistry();
        var service = CreateService(registry);
        await service.Register("First", "echo", "111:token", TestContext.Current.CancellationToken);

        var result = await service.Register("Second", "echo", "111:token-again", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task Disable_SetsStatus_AndStopsTheBot()
    {
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(registry, lifecycle: lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;

        var result = await service.Disable(bot.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(BotStatus.Disabled, (await service.Get(bot.Id, TestContext.Current.CancellationToken))!.Status);
        Assert.Contains(bot.Id, lifecycle.Stopped);
    }

    [Fact]
    public async Task Enable_StartsTheBot_WithTheDecryptedToken()
    {
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(registry, lifecycle: lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;
        await service.Disable(bot.Id, TestContext.Current.CancellationToken);
        lifecycle.Started.Clear();

        var result = await service.Enable(bot.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(BotStatus.Active, (await service.Get(bot.Id, TestContext.Current.CancellationToken))!.Status);
        Assert.Equal((bot.Id, "111:token"), Assert.Single(lifecycle.Started));
    }

    [Fact]
    public async Task RotateToken_StartsWithTheNewToken_WhenSameTelegramBot()
    {
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(registry, new FakeTokenValidator(telegramBotId: 111), lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:old-token", TestContext.Current.CancellationToken)).Value;
        lifecycle.Started.Clear();

        var result = await service.RotateToken(bot.Id, "111:new-token", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal((bot.Id, "111:new-token"), Assert.Single(lifecycle.Started));
    }

    [Fact]
    public async Task RotateToken_Fails_WhenTokenBelongsToADifferentTelegramBot()
    {
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var bot = (await CreateService(registry, lifecycle: lifecycle)
            .Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;
        var serviceSeeingAnotherBot = CreateService(registry, new FakeTokenValidator(telegramBotId: 999), lifecycle);
        lifecycle.Started.Clear();

        var result = await serviceSeeingAnotherBot.RotateToken(
            bot.Id, "999:other-bot-token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Empty(lifecycle.Started);
    }

    [Fact]
    public async Task Remove_RemovesFromRegistryAndLifecycle()
    {
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(registry, lifecycle: lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;

        var result = await service.Remove(bot.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(await service.Get(bot.Id, TestContext.Current.CancellationToken));
        Assert.Contains(bot.Id, lifecycle.Removed);
    }

    // Starting the receiver is the only step that can fail after the registry has been written: it is a
    // live setWebhook, over a connection that may be down and a URL that may be misconfigured.

    [Fact]
    public async Task Register_LeavesNothingRegistered_WhenTheReceiverWillNotStart()
    {
        var registry = new FakeBotRegistry();
        var service = CreateService(registry, lifecycle: new FakeBotLifecycle { StartThrows = TelegramRefusedTheWebhook });

        var result = await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Empty(await registry.List(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Register_ReportsARefusal_WhenTheReceiverWillNotStart_RatherThanThrowing()
    {
        // An escaping exception is an opaque 500 with a bot left behind it; a Result is a refusal the
        // endpoint can map and the caller can retry.
        var service = CreateService(lifecycle: new FakeBotLifecycle { StartThrows = TelegramRefusedTheWebhook });

        var result = await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("could not be started", result.Errors.First().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Register_ForgetsTheHalfStartedClient_WhenTheReceiverWillNotStart()
    {
        // The supervisor registers the bot's client before it reaches the call that throws, so undoing the
        // registry row alone would leave a live client for a bot that no longer exists.
        var lifecycle = new FakeBotLifecycle { StartThrows = TelegramRefusedTheWebhook };
        var service = CreateService(lifecycle: lifecycle);

        await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);

        Assert.Single(lifecycle.Removed);
    }

    [Fact]
    public async Task Register_IsRetryable_AfterTheReceiverFailedToStart()
    {
        var lifecycle = new FakeBotLifecycle { StartThrows = TelegramRefusedTheWebhook };
        var service = CreateService(lifecycle: lifecycle);
        await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);
        lifecycle.StartThrows = null;

        var retried = await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken);

        // A stranded row would still hold the unique Telegram bot id, and answer 409 forever.
        Assert.True(retried.IsSuccess);
        Assert.Single(lifecycle.Started);
    }

    [Fact]
    public async Task Enable_StaysDisabled_WhenTheReceiverWillNotStart()
    {
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(lifecycle: lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;
        await service.Disable(bot.Id, TestContext.Current.CancellationToken);
        lifecycle.StartThrows = TelegramRefusedTheWebhook;

        var result = await service.Enable(bot.Id, TestContext.Current.CancellationToken);

        // Active-but-not-running is the one state an operator cannot act on: every read says it is fine.
        Assert.True(result.IsFailed);
        Assert.Equal(BotStatus.Disabled, (await service.Get(bot.Id, TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task RotateToken_KeepsTheNewToken_ButReportsThatTheReceiverDidNotRestart()
    {
        // Deliberately not symmetrical with the two above: the superseded token may already be revoked, so
        // putting it back would restore a credential that no longer works.
        var registry = new FakeBotRegistry();
        var lifecycle = new FakeBotLifecycle();
        var service = CreateService(registry, new FakeTokenValidator(telegramBotId: 111), lifecycle);
        var bot = (await service.Register("My Bot", "echo", "111:old-token", TestContext.Current.CancellationToken)).Value;
        lifecycle.StartThrows = TelegramRefusedTheWebhook;

        var result = await service.RotateToken(bot.Id, "111:new-token", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("was saved", result.Errors.First().Message, StringComparison.Ordinal);
        Assert.Equal("111:new-token", Encoding.UTF8.GetString(
            (await registry.GetEncryptedToken(bot.Id, TestContext.Current.CancellationToken))!));
    }

    [Fact]
    public async Task Disable_ForgetsTheBotsFailureStreak()
    {
        // Carrying the count over means the bot's first failure after being re-enabled flags it Failing,
        // on evidence from the deployment the operator took it down to fix.
        var counter = new BotFailureCounter();
        var service = CreateService(failureCounter: counter);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;
        counter.Increment(bot.Id);
        counter.Increment(bot.Id);

        await service.Disable(bot.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, counter.Increment(bot.Id));
    }

    [Fact]
    public async Task Remove_ForgetsTheBotsFailureStreak()
    {
        // Nothing else drops these, so every removed bot would leave an entry behind for the life of the
        // process.
        var counter = new BotFailureCounter();
        var service = CreateService(failureCounter: counter);
        var bot = (await service.Register("My Bot", "echo", "111:token", TestContext.Current.CancellationToken)).Value;
        counter.Increment(bot.Id);

        await service.Remove(bot.Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, counter.Increment(bot.Id));
    }

    private static BotRegistrationService CreateService(
        FakeBotRegistry? registry = null,
        FakeTokenValidator? validator = null,
        FakeBotLifecycle? lifecycle = null,
        BotFailureCounter? failureCounter = null) =>
        new(registry ?? new FakeBotRegistry(),
            new FakeBehaviorCatalog(),
            new FakeTokenProtector(),
            validator ?? new FakeTokenValidator(),
            lifecycle ?? new FakeBotLifecycle(),
            failureCounter ?? new BotFailureCounter());

    private sealed class FakeBotRegistry : IBotRegistry
    {
        private long _nextId = 1;
        private readonly Dictionary<long, (BotRegistration Registration, byte[] Token)> _bots = new();

        public Task<Result<BotRegistration>> Add(long telegramBotId, string? username, string label, string behaviorKey, byte[] encryptedToken, CancellationToken cancellationToken = default)
        {
            if (_bots.Values.Any(bot => bot.Registration.TelegramBotId == telegramBotId))
            {
                return Task.FromResult(Result.Fail<BotRegistration>($"Telegram bot {telegramBotId} is already registered."));
            }

            var registration = new BotRegistration(_nextId++, telegramBotId, username, label, behaviorKey, BotStatus.Active, DateTime.UtcNow, DateTime.UtcNow);
            _bots[registration.Id] = (registration, encryptedToken);

            return Task.FromResult(Result.Ok(registration));
        }

        public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.TryGetValue(botId, out var entry) ? entry.Registration : null);

        public Task<byte[]?> GetEncryptedToken(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.TryGetValue(botId, out var entry) ? entry.Token : null);

        public Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BotRegistration>>(_bots.Values.Select(bot => bot.Registration).ToArray());

        public Task<Result> UpdateStatus(long botId, BotStatus status, CancellationToken cancellationToken = default)
        {
            if (!_bots.TryGetValue(botId, out var entry))
            {
                return Task.FromResult(Result.Fail($"Bot {botId} was not found."));
            }

            _bots[botId] = (entry.Registration with { Status = status }, entry.Token);

            return Task.FromResult(Result.Ok());
        }

        public Task<Result> UpdateToken(long botId, long telegramBotId, byte[] encryptedToken, CancellationToken cancellationToken = default)
        {
            if (!_bots.TryGetValue(botId, out var entry))
            {
                return Task.FromResult(Result.Fail($"Bot {botId} was not found."));
            }

            if (entry.Registration.TelegramBotId != telegramBotId)
            {
                return Task.FromResult(Result.Fail("The new token belongs to a different Telegram bot than the one currently registered."));
            }

            _bots[botId] = (entry.Registration, encryptedToken);

            return Task.FromResult(Result.Ok());
        }

        public Task<Result> Remove(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.Remove(botId) ? Result.Ok() : Result.Fail($"Bot {botId} was not found."));
    }

    private sealed class FakeTokenValidator(bool succeeds = true, long telegramBotId = 111, string username = "bot") : IBotTokenValidator
    {
        public Task<Result<(long TelegramBotId, string? Username)>> Validate(string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(succeeds
                ? Result.Ok<(long, string?)>((telegramBotId, username))
                : Result.Fail<(long, string?)>("Telegram rejected the bot token."));
    }

    private sealed class FakeTokenProtector : ITokenProtector
    {
        public byte[] Protect(string plaintextToken) => Encoding.UTF8.GetBytes(plaintextToken);
        public string Unprotect(byte[] protectedToken) => Encoding.UTF8.GetString(protectedToken);
    }

    private sealed class FakeBehaviorCatalog : IBehaviorCatalog
    {
        public bool TryGet(string key, out IBotBehavior? behavior)
        {
            behavior = null;
            return key == "echo";
        }

        public IReadOnlyList<BehaviorDescriptor> List() => [];

        public Result Register(IBotBehavior behavior, string source) => Result.Ok();

        public IReadOnlyList<string> KeysFromSource(string source) => [];

        public Result ReplaceSource(string source, IReadOnlyList<IBotBehavior> behaviors) => Result.Ok();

        public Result RemoveSource(string source) => Result.Ok();
    }

    private sealed class FakeBotLifecycle : IBotLifecycle
    {
        public List<(long BotId, string Token)> Started { get; } = [];
        public List<long> Stopped { get; } = [];
        public List<long> Removed { get; } = [];

        /// <summary>Makes bringing the receiver up throw, as the real supervisor does when Telegram refuses the setWebhook.</summary>
        public Exception? StartThrows { get; set; }

        public Task Start(long botId, string token, CancellationToken cancellationToken = default)
        {
            if (StartThrows is { } exception)
            {
                throw exception;
            }

            Started.Add((botId, token));

            return Task.CompletedTask;
        }

        public Task Stop(long botId, CancellationToken cancellationToken = default)
        {
            Stopped.Add(botId);

            return Task.CompletedTask;
        }

        public Task Remove(long botId, CancellationToken cancellationToken = default)
        {
            Removed.Add(botId);

            return Task.CompletedTask;
        }
    }
}