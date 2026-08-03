using System.Text;
using FluentResults;
using Microsoft.Extensions.Logging.Abstractions;
using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public.Behaviors;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// Drives the whole extension lifecycle against fakes — no disk, no network, no database — which is the
/// point of the store/loader seam. Every guarantee the spec makes about rejection cleanup, replacement
/// rollback, in-use protection, and startup restore is asserted here rather than left to manual checks.
/// </summary>
public class BehaviorExtensionServiceTests
{
    private static readonly TimeSpan NoRetries = TimeSpan.Zero;

    // --- Upload -------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_Stores_LoadsAndRegisters()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("Reverse.dll", result.Value.PackageName);
        Assert.Equal(["reverse"], result.Value.BehaviorKeys);
        Assert.True(store.Contains("Reverse.dll"));
        Assert.True(catalog.TryGet("reverse", out _));
        // Field-by-field: the record's generated equality compares the key list by reference.
        var status = Assert.Single(service.Packages);
        Assert.Equal("Reverse.dll", status.PackageName);
        Assert.True(status.Loaded);
        Assert.Equal(["reverse"], status.BehaviorKeys);
        Assert.Null(status.Error);
    }

    [Theory]
    [InlineData("../../Reverse.dll")]
    [InlineData(@"C:\uploads\Reverse.dll")]
    public async Task Upload_StripsPathSegments_FromTheSuppliedName(string suppliedName)
    {
        // A multipart filename= header carries whatever the client put there, including a full Windows
        // path. What gets stored — and reported back — must not depend on which OS the host runs on.
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var service = CreateService(store, loader);

        var result = await service.Upload(suppliedName, Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("Reverse.dll", result.Value.PackageName);
        Assert.True(store.Contains("Reverse.dll"));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Upload_Fails_ForInvalidName_WithoutTouchingTheStore()
    {
        var store = new InMemoryExtensionStore();
        var service = CreateService(store);

        var result = await service.Upload("Reverse.exe", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.ListCallCount);
    }

    [Fact]
    public async Task Upload_Conflicts_WhenNameIsAlreadyStored_LeavingTheStoredBytesUntouched()
    {
        var store = new InMemoryExtensionStore();
        store.Seed("Reverse.dll", [9, 9, 9]);
        var service = CreateService(store, new FakeExtensionLoader().Yields("Reverse.dll", "reverse"));

        var result = await service.Upload("Reverse.dll", Package("new bytes"), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("already exists", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([9, 9, 9], store.Bytes("Reverse.dll"));
    }

    [Fact]
    public async Task Upload_DeletesThePackage_WhenItFailsToLoad()
    {
        var store = new InMemoryExtensionStore();
        var service = CreateService(store, new FakeExtensionLoader().Fails("Broken.dll"));

        var result = await service.Upload("Broken.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.False(store.Contains("Broken.dll"));
        Assert.Empty(service.Packages);
    }

    [Fact]
    public async Task Upload_DeletesThePackage_WhenABehaviorKeyCollides_LeavingTheExistingBehaviorRegistered()
    {
        var store = new InMemoryExtensionStore();
        var catalog = new BehaviorCatalog();
        catalog.Register(new StubBehavior("echo"), BehaviorSource.BuiltIn);
        var service = CreateService(store, new FakeExtensionLoader().Yields("Clash.dll", "echo"), catalog);

        var result = await service.Upload("Clash.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.False(store.Contains("Clash.dll"));
        Assert.Single(catalog.List());
        Assert.Equal(BehaviorSource.BuiltIn, catalog.List()[0].Source);
    }

    [Fact]
    public async Task Upload_RegistersNothing_WhenOneOfSeveralKeysCollides()
    {
        var store = new InMemoryExtensionStore();
        var catalog = new BehaviorCatalog();
        catalog.Register(new StubBehavior("echo"), BehaviorSource.BuiltIn);
        var service = CreateService(store, new FakeExtensionLoader().Yields("Multi.dll", "fine", "echo"), catalog);

        var result = await service.Upload("Multi.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        // The atomic swap is what makes this true: the good key must not survive a rejected package.
        Assert.False(catalog.TryGet("fine", out _));
    }

    [Fact]
    public async Task Upload_ReportsStoreUnavailable_WhenTheWriteFails_LeavingTheCatalogUnchanged()
    {
        var store = new InMemoryExtensionStore { FailWrite = true };
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, new FakeExtensionLoader().Yields("Reverse.dll", "reverse"), catalog);

        var result = await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.IsType<StoreUnavailableError>(result.Errors.First());
        Assert.Empty(catalog.List());
    }

    [Fact]
    public async Task Upload_Rejects_WhenThePackageExceedsTheConfiguredLimit()
    {
        // The endpoint rejects on declared length once form binding has buffered the part; the service
        // still refuses oversize content so the guarantee does not depend on a single call site.
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Big.dll", "big");
        var service = CreateService(store, loader, maxPackageBytes: 8);

        var result = await service.Upload("Big.dll", Package(new string('x', 64)), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("limit", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, store.Count);
        Assert.Empty(loader.Handles);
    }

    // --- Replace ------------------------------------------------------------------------------------

    [Fact]
    public async Task Replace_SwapsBehaviors_AndDisposesTheSupersededHandle()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Yields("Reverse.dll", "reverse", "reverse-words");
        var result = await service.Replace("Reverse.dll", Package("v2"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("Reverse.dll", result.Value.PackageName);
        Assert.Equal(["reverse", "reverse-words"], result.Value.BehaviorKeys);
        Assert.True(catalog.TryGet("reverse-words", out _));
        Assert.Equal("v2", Encoding.UTF8.GetString(store.Bytes("Reverse.dll")!));
        Assert.Equal(1, loader.Handles[0].DisposeCount);
        Assert.Equal(0, loader.Handles[1].DisposeCount);
    }

    [Fact]
    public async Task Replace_LeavesThePackageStillStored_SoASubsequentReplaceFindsIt()
    {
        // Regression: the loader used to stage into the filesystem store's own root, so disposing the
        // superseded handle after a successful replace deleted the package it had just stored — the catalog
        // reported it loaded while the store was empty, and the next restart lost the behavior.
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var service = CreateService(store, loader);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Yields("Reverse.dll", "reverse");
        await service.Replace("Reverse.dll", Package("v2"), TestContext.Current.CancellationToken);

        Assert.True(store.Contains("Reverse.dll"));

        loader.Yields("Reverse.dll", "reverse");
        var third = await service.Replace("Reverse.dll", Package("v3"), TestContext.Current.CancellationToken);

        Assert.True(third.IsSuccess);
        Assert.Equal("v3", Encoding.UTF8.GetString(store.Bytes("Reverse.dll")!));
    }

    [Fact]
    public async Task Replace_ChangesNothing_WhenTheNewBuildFailsToLoad()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Fails("Reverse.dll");
        var result = await service.Replace("Reverse.dll", Package("broken"), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("v1", Encoding.UTF8.GetString(store.Bytes("Reverse.dll")!));
        Assert.True(catalog.TryGet("reverse", out _));
        Assert.Equal(0, loader.Handles[0].DisposeCount);
        Assert.True(service.Packages[0].Loaded);
    }

    [Fact]
    public async Task Replace_RollsBack_WhenTheStoreWriteFails()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Yields("Reverse.dll", "reverse-v2");
        store.FailWrite = true;
        var result = await service.Replace("Reverse.dll", Package("v2"), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal("v1", Encoding.UTF8.GetString(store.Bytes("Reverse.dll")!));
        // The previous behavior is back and the abandoned new build was released.
        Assert.True(catalog.TryGet("reverse", out _));
        Assert.False(catalog.TryGet("reverse-v2", out _));
        Assert.Equal(1, loader.Handles[1].DisposeCount);
    }

    [Fact]
    public async Task Replace_IsRefused_WhenTheNewBuildDropsABehaviorABotStillUses()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var registry = new FakeBotRegistry();
        registry.Add(botId: 12, behaviorKey: "reverse");
        var service = CreateService(store, loader, catalog, registry);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Yields("Reverse.dll", "something-else");
        var result = await service.Replace("Reverse.dll", Package("v2"), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("12", result.Errors.First().Message, StringComparison.Ordinal);
        Assert.Equal([12L], Assert.IsType<BehaviorInUseError>(result.Errors.First()).BotIds);
        Assert.True(catalog.TryGet("reverse", out _));
        Assert.False(catalog.TryGet("something-else", out _));
    }

    [Fact]
    public async Task Replace_Succeeds_WhenTheNewBuildKeepsTheInUseBehavior()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var registry = new FakeBotRegistry();
        registry.Add(botId: 12, behaviorKey: "reverse");
        var service = CreateService(store, loader, botRegistry: registry);
        await service.Upload("Reverse.dll", Package("v1"), TestContext.Current.CancellationToken);

        loader.Yields("Reverse.dll", "reverse", "extra");
        var result = await service.Replace("Reverse.dll", Package("v2"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Replace_Fails_WhenThePackageIsNotStored()
    {
        var service = CreateService();

        var result = await service.Replace("Missing.dll", Package(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("was not found", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replace_RepairsAPackageThatFailedToLoad()
    {
        var store = new InMemoryExtensionStore();
        store.Seed("Reverse.dll");
        var loader = new FakeExtensionLoader().Fails("Reverse.dll");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);
        await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);
        Assert.False(service.Packages[0].Loaded);

        loader.Yields("Reverse.dll", "reverse");
        var result = await service.Replace("Reverse.dll", Package("fixed"), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(service.Packages[0].Loaded);
        Assert.True(catalog.TryGet("reverse", out _));
    }

    // --- Remove -------------------------------------------------------------------------------------

    [Fact]
    public async Task Remove_UnregistersDeletesAndDisposes()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);
        await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        var result = await service.Remove("Reverse.dll", TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(store.Contains("Reverse.dll"));
        Assert.False(catalog.TryGet("reverse", out _));
        Assert.Empty(service.Packages);
        Assert.Equal(1, loader.Handles[0].DisposeCount);
    }

    [Fact]
    public async Task Remove_IsRefused_WhileABotIsStillAssigned()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var catalog = new BehaviorCatalog();
        var registry = new FakeBotRegistry();
        registry.Add(botId: 34, behaviorKey: "reverse");
        var service = CreateService(store, loader, catalog, registry);
        await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        var result = await service.Remove("Reverse.dll", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Equal([34L], Assert.IsType<BehaviorInUseError>(result.Errors.First()).BotIds);
        Assert.True(store.Contains("Reverse.dll"));
        Assert.True(catalog.TryGet("reverse", out _));
        Assert.Equal(0, loader.Handles[0].DisposeCount);
    }

    [Fact]
    public async Task Remove_IsRefused_WhenTheAssignedBotIsDisabled()
    {
        // A disabled bot can be re-enabled, so it must not land on a behavior that has been removed.
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var registry = new FakeBotRegistry();
        registry.Add(botId: 7, behaviorKey: "reverse", status: BotStatus.Disabled);
        var service = CreateService(store, loader, botRegistry: registry);
        await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        var result = await service.Remove("Reverse.dll", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task Remove_Fails_WhenThePackageIsNotStored()
    {
        var service = CreateService();

        var result = await service.Remove("Missing.dll", TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("was not found", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replace_RepeatedManyTimes_DisposesEverySupersededHandle()
    {
        var store = new InMemoryExtensionStore();
        var loader = new FakeExtensionLoader().Yields("Reverse.dll", "reverse");
        var service = CreateService(store, loader);
        await service.Upload("Reverse.dll", Package(), TestContext.Current.CancellationToken);

        for (var i = 0; i < 50; i++)
        {
            loader.Yields("Reverse.dll", "reverse");
            var result = await service.Replace("Reverse.dll", Package($"v{i}"), TestContext.Current.CancellationToken);
            Assert.True(result.IsSuccess);
        }

        // 51 handles produced; every one but the live one is released, with no double-dispose.
        Assert.Equal(51, loader.Handles.Count);
        Assert.Equal(50, loader.DisposedCount);
        Assert.All(loader.Handles, handle => Assert.InRange(handle.DisposeCount, 0, 1));
        Assert.Equal(0, loader.Handles[^1].DisposeCount);
    }

    // --- Restore ------------------------------------------------------------------------------------

    [Fact]
    public async Task RestoreAll_Succeeds_OnAnEmptyStore()
    {
        var service = CreateService();

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(service.Packages);
    }

    [Fact]
    public async Task RestoreAll_RegistersEveryStoredPackage()
    {
        var store = new InMemoryExtensionStore();
        store.Seed("A.dll");
        store.Seed("B.dll");
        var loader = new FakeExtensionLoader().Yields("A.dll", "a").Yields("B.dll", "b");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(catalog.TryGet("a", out _));
        Assert.True(catalog.TryGet("b", out _));
        Assert.All(service.Packages, package => Assert.True(package.Loaded));
    }

    [Fact]
    public async Task RestoreAll_RecordsABadPackage_AndKeepsGoing()
    {
        var store = new InMemoryExtensionStore();
        store.Seed("Good.dll");
        store.Seed("Broken.dll");
        var loader = new FakeExtensionLoader().Yields("Good.dll", "good").Fails("Broken.dll", "bad IL");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(catalog.TryGet("good", out _));

        var broken = service.Packages.Single(package => package.PackageName == "Broken.dll");
        Assert.False(broken.Loaded);
        Assert.Empty(broken.BehaviorKeys);
        Assert.Contains("bad IL", broken.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAll_Fails_WhenTheStoreStaysUnreachable()
    {
        var store = new InMemoryExtensionStore { FailList = true };
        var service = CreateService(store);

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.IsType<StoreUnavailableError>(result.Errors.First());
    }

    [Fact]
    public async Task RestoreAll_Succeeds_WhenTheStoreRecoversInsideTheRetryBudget()
    {
        var store = new InMemoryExtensionStore { FailListTimes = 2 };
        store.Seed("A.dll");
        var loader = new FakeExtensionLoader().Yields("A.dll", "a");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.RestoreAll(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, store.ListCallCount);
        Assert.True(catalog.TryGet("a", out _));
    }

    [Fact]
    public async Task RestoreAll_RetriesAPackageRead_AndSucceedsWhenTheStoreRecovers()
    {
        // The same outage that makes the listing flaky makes the reads flaky. Retrying only the listing
        // would leave a package marked broken for the process's whole life over a blip it rode out once.
        var store = new InMemoryExtensionStore { FailReadTimes = 2 };
        store.Seed("A.dll");
        var loader = new FakeExtensionLoader().Yields("A.dll", "a");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.RestoreAll(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, store.ReadCallCount);
        Assert.True(catalog.TryGet("a", out _));
        Assert.True(Assert.Single(service.Packages).Loaded);
    }

    [Fact]
    public async Task RestoreAll_RecordsAPackageItCannotRead()
    {
        var store = new InMemoryExtensionStore();
        store.Seed("A.dll");
        store.FailRead = true;
        var service = CreateService(store);

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.False(Assert.Single(service.Packages).Loaded);
    }

    [Fact]
    public async Task RestoreAll_RecordsAStoredNameItRefusesToTrust_WithoutLoadingIt()
    {
        // The store's contents are not necessarily only what this platform put there — a stray object in
        // the bucket, or a prefix misconfigured to "", can surface a name that is about to become a path.
        var store = new InMemoryExtensionStore();
        store.Seed("../escape.dll");
        store.Seed("Good.dll");
        var loader = new FakeExtensionLoader().Yields("Good.dll", "good");
        var catalog = new BehaviorCatalog();
        var service = CreateService(store, loader, catalog);

        var result = await service.RestoreAll(NoRetries, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(catalog.TryGet("good", out _));

        var rejected = service.Packages.Single(package => package.PackageName == "../escape.dll");
        Assert.False(rejected.Loaded);
        Assert.DoesNotContain(loader.Handles, handle => handle.PackageName.Contains("escape", StringComparison.Ordinal));
    }

    // --- Helpers ------------------------------------------------------------------------------------

    private static BehaviorExtensionService CreateService(
        InMemoryExtensionStore? store = null,
        FakeExtensionLoader? loader = null,
        BehaviorCatalog? catalog = null,
        FakeBotRegistry? botRegistry = null,
        long maxPackageBytes = long.MaxValue) =>
        new(store ?? new InMemoryExtensionStore(),
            loader ?? new FakeExtensionLoader(),
            catalog ?? new BehaviorCatalog(),
            (botRegistry ?? new FakeBotRegistry()).List,
            maxPackageBytes,
            NullLogger<BehaviorExtensionService>.Instance);

    private static MemoryStream Package(string content = "package") =>
        new(Encoding.UTF8.GetBytes(content));

    private sealed class StubBehavior(string key) : IBotBehavior
    {
        public string Key { get; } = key;
        public string DisplayName => $"Stub:{Key}";
        public string ContractVersion => BehaviorContractVersion.Current;
        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Only <see cref="List"/> matters here — the service reads registrations to protect in-use behaviors.</summary>
    private sealed class FakeBotRegistry : IBotRegistry
    {
        private readonly List<BotRegistration> _bots = [];

        public void Add(long botId, string behaviorKey, BotStatus status = BotStatus.Active) =>
            _bots.Add(new BotRegistration(botId, botId, null, $"Bot {botId}", behaviorKey, status, DateTime.UtcNow, DateTime.UtcNow));

        public Task<IReadOnlyList<BotRegistration>> List(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BotRegistration>>(_bots);

        public Task<Result<BotRegistration>> Add(long telegramBotId, string? username, string label, string behaviorKey, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BotRegistration?> Get(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bots.FirstOrDefault(bot => bot.Id == botId));

        public Task<byte[]?> GetEncryptedToken(long botId, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>(null);

        public Task<Result> UpdateStatus(long botId, BotStatus status, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> UpdateToken(long botId, long telegramBotId, byte[] encryptedToken, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result> Remove(long botId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}