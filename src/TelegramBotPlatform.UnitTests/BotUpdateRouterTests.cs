using FluentResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public;
using TelegramBotPlatform.Public.Behaviors;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

public sealed class BotUpdateRouterTests
{
    private static readonly Update _sampleUpdate = new();

    [Fact]
    public async Task Route_DispatchesToTheBotsAssignedBehavior()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, "recording"));
        var catalog = new BehaviorCatalog();
        var behavior = new RecordingBehavior();
        catalog.Register(behavior, "built-in");
        var router = CreateRouter(registry, catalog);

        await router.Route(botId: 1, _sampleUpdate, TestContext.Current.CancellationToken);

        Assert.Equal(1, behavior.CallCount);
        Assert.Equal(1, behavior.LastContext!.BotId);
        Assert.Same(_sampleUpdate, behavior.LastContext.Update);
    }

    [Fact]
    public async Task Route_DoesNothing_WhenBotIsUnknown()
    {
        var registry = new FakeBotRegistry();
        var catalog = new BehaviorCatalog();
        var behavior = new RecordingBehavior();
        catalog.Register(behavior, "built-in");
        var router = CreateRouter(registry, catalog);

        await router.Route(botId: 999, _sampleUpdate, TestContext.Current.CancellationToken);

        Assert.Equal(0, behavior.CallCount);
    }

    [Fact]
    public async Task Route_DoesNothing_WhenBehaviorKeyIsUnknown()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, "missing-behavior"));
        var catalog = new BehaviorCatalog();
        var router = CreateRouter(registry, catalog);

        var exception = await Record.ExceptionAsync(() => router.Route(botId: 1, _sampleUpdate, TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Route_ContainsAFaultInOneBotsBehavior_WithoutThrowing()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, "throwing"));
        var catalog = new BehaviorCatalog();
        catalog.Register(new ThrowingBehavior(), "built-in");
        var router = CreateRouter(registry, catalog);

        var exception = await Record.ExceptionAsync(() => router.Route(botId: 1, _sampleUpdate, TestContext.Current.CancellationToken));

        Assert.Null(exception);
    }

    [Fact]
    public async Task Route_DropsTheUpdate_WhenTheBotHasNoLiveClient()
    {
        var registry = new FakeBotRegistry();
        registry.Seed(Registration(1, "recording"));
        var catalog = new BehaviorCatalog();
        var behavior = new RecordingBehavior();
        catalog.Register(behavior, "built-in");
        var clientRegistry = new FakeBotClientRegistry { HasClient = false };
        var router = new BotUpdateRouter(
            registry,
            catalog,
            clientRegistry,
            new ServiceCollection().BuildServiceProvider(),
            new BotHealthTracker(registry, new BotFailureCounter(), NullLogger<BotHealthTracker>.Instance),
            NullLogger<BotUpdateRouter>.Instance);

        var exception = await Record.ExceptionAsync(() => router.Route(botId: 1, _sampleUpdate, TestContext.Current.CancellationToken));

        Assert.Null(exception);
        Assert.Equal(0, behavior.CallCount);
    }

    private static BotUpdateRouter CreateRouter(IBotRegistry registry, IBehaviorCatalog catalog) =>
        new(registry,
            catalog,
            new FakeBotClientRegistry(),
            new ServiceCollection().BuildServiceProvider(),
            new BotHealthTracker(registry, new BotFailureCounter(), NullLogger<BotHealthTracker>.Instance),
            NullLogger<BotUpdateRouter>.Instance);

    private static BotRegistration Registration(long id, string behaviorKey) =>
        new(id, TelegramBotId: 1000 + id, Username: "bot", Label: "Bot", behaviorKey, BotStatus.Active, DateTime.UtcNow, DateTime.UtcNow);

    private sealed class FakeBotRegistry : IBotRegistry
    {
        private readonly Dictionary<long, BotRegistration> _bots = new();

        public void Seed(BotRegistration registration) => _bots[registration.Id] = registration;

        public Task<Result<BotRegistration>> Add(long telegramBotId, string? username, string label, string behaviorKey, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.GetValueOrDefault(botId));

        public Task<byte[]?> GetEncryptedToken(long botId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateStatus(long botId, BotStatus status, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateToken(long botId, long telegramBotId, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> Remove(long botId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeBotClientRegistry : IBotClientRegistry
    {
        private readonly ITelegramBotClient _client = new TelegramBotClient("123456:fake-token-never-called");

        public bool HasClient { get; init; } = true;

        public ITelegramBotClient Get(long botId) =>
            HasClient ? _client : throw new InvalidOperationException($"No client for bot {botId}.");

        public bool TryGet(long botId, out ITelegramBotClient? client)
        {
            client = HasClient ? _client : null;
            return HasClient;
        }

        public void Set(long botId, string token) { }

        public void Remove(long botId) { }
    }

    private sealed class RecordingBehavior : IBotBehavior
    {
        public int CallCount { get; private set; }
        public IBotUpdateContext? LastContext { get; private set; }

        public string Key => "recording";
        public string DisplayName => "Recording";
        public string ContractVersion => BehaviorContractVersion.Current;

        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            LastContext = context;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingBehavior : IBotBehavior
    {
        public string Key => "throwing";
        public string DisplayName => "Throwing";
        public string ContractVersion => BehaviorContractVersion.Current;

        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}