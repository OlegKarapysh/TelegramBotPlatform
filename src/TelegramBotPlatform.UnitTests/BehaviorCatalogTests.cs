using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public.Behaviors;

namespace TelegramBotPlatform.UnitTests;

public sealed class BehaviorCatalogTests
{
    private const string Source = "extension:Pkg.dll";

    [Fact]
    public void Register_Succeeds_ForNewKey()
    {
        var catalog = new BehaviorCatalog();

        var result = catalog.Register(new FakeBehavior("echo"), "built-in");

        Assert.True(result.IsSuccess);
        Assert.True(catalog.TryGet("echo", out var behavior));
        Assert.Equal("echo", behavior!.Key);
    }

    [Fact]
    public void Register_Fails_WhenKeyAlreadyRegistered()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");

        var result = catalog.Register(new FakeBehavior("echo"), "extension:Echo.dll");

        Assert.True(result.IsFailed);
        Assert.Contains("already registered", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_Fails_WhenKeyBelongsToAnExtensionSource()
    {
        var catalog = new BehaviorCatalog();
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        var result = catalog.Register(new FakeBehavior("reverse"), "built-in");

        Assert.True(result.IsFailed);
    }

    [Fact]
    public void Register_Fails_WhenContractMajorVersionDiffers()
    {
        var catalog = new BehaviorCatalog();

        var result = catalog.Register(new FakeBehavior("echo", "2.0"), "extension:Echo.dll");

        Assert.True(result.IsFailed);
    }

    [Fact]
    public void Register_Succeeds_WhenOnlyMinorVersionDiffers()
    {
        var catalog = new BehaviorCatalog();

        var result = catalog.Register(new FakeBehavior("echo", "1.5"), "extension:Echo.dll");

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void TryGet_ReturnsFalse_ForUnknownKey()
    {
        var catalog = new BehaviorCatalog();

        var found = catalog.TryGet("missing", out var behavior);

        Assert.False(found);
        Assert.Null(behavior);
    }

    [Fact]
    public void List_ReturnsAllRegisteredBehaviors_WithTheirSource()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        catalog.Register(new FakeBehavior("reverse"), "extension:Reverse.dll");

        var descriptors = catalog.List();

        Assert.Equal(2, descriptors.Count);
        Assert.Contains(descriptors, descriptor => descriptor is { Key: "echo", Source: "built-in" });
        Assert.Contains(descriptors, descriptor => descriptor is { Key: "reverse", Source: "extension:Reverse.dll" });
    }

    [Fact]
    public void KeysFromSource_ReturnsOnlyThatSourcesKeys()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse"), new FakeBehavior("reverse-words")]);

        var extensionKeys = catalog.KeysFromSource("extension:Reverse.dll");

        Assert.Equal(["reverse", "reverse-words"], extensionKeys);
        Assert.Equal(["echo"], catalog.KeysFromSource("built-in"));
        Assert.Empty(catalog.KeysFromSource("extension:Unknown.dll"));
    }

    [Fact]
    public void ReplaceSource_RegistersEveryBehavior_WhenTheSourceIsNew()
    {
        var catalog = new BehaviorCatalog();

        var result = catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("a"), new FakeBehavior("b")]);

        Assert.True(result.IsSuccess);
        Assert.True(catalog.TryGet("a", out _));
        Assert.True(catalog.TryGet("b", out _));
    }

    [Fact]
    public void ReplaceSource_SwapsTheWholeKeySet_InOneTransition()
    {
        var catalog = new BehaviorCatalog();
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("old1"), new FakeBehavior("old2")]);

        var result = catalog.ReplaceSource(
            "extension:Reverse.dll", [new FakeBehavior("new1"), new FakeBehavior("new2"), new FakeBehavior("new3")]);

        Assert.True(result.IsSuccess);
        Assert.False(catalog.TryGet("old1", out _));
        Assert.False(catalog.TryGet("old2", out _));
        Assert.Equal(["new1", "new2", "new3"], catalog.KeysFromSource("extension:Reverse.dll"));
    }

    [Fact]
    public void ReplaceSource_AllowsASourceToReDeclareItsOwnKeys()
    {
        // Shipping a new build of the same package must not look like a collision with itself.
        var catalog = new BehaviorCatalog();
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        var result = catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ReplaceSource_ChangesNothing_WhenAKeyBelongsToAnotherSource()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        var result = catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("fine"), new FakeBehavior("echo")]);

        Assert.True(result.IsFailed);
        Assert.False(catalog.TryGet("fine", out _));
        Assert.Equal(["reverse"], catalog.KeysFromSource("extension:Reverse.dll"));
        Assert.Equal("built-in", catalog.List().Single(descriptor => descriptor.Key == "echo").Source);
    }

    [Fact]
    public void ReplaceSource_ChangesNothing_WhenAContractVersionIsIncompatible()
    {
        var catalog = new BehaviorCatalog();
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        var result = catalog.ReplaceSource(
            "extension:Reverse.dll", [new FakeBehavior("ok"), new FakeBehavior("bad", "2.0")]);

        Assert.True(result.IsFailed);
        Assert.False(catalog.TryGet("ok", out _));
        Assert.True(catalog.TryGet("reverse", out _));
    }

    [Fact]
    public void ReplaceSource_Fails_WhenTheIncomingSetRepeatsAKey()
    {
        var catalog = new BehaviorCatalog();

        var result = catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("dup"), new FakeBehavior("dup")]);

        Assert.True(result.IsFailed);
        Assert.False(catalog.TryGet("dup", out _));
    }

    [Fact]
    public void RemoveSource_DropsExactlyThatSourcesKeys()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("a"), new FakeBehavior("b")]);

        var result = catalog.RemoveSource("extension:Reverse.dll");

        Assert.True(result.IsSuccess);
        Assert.False(catalog.TryGet("a", out _));
        Assert.False(catalog.TryGet("b", out _));
        Assert.True(catalog.TryGet("echo", out _));
    }

    [Fact]
    public void RemoveSource_Succeeds_ForAnUnknownSource()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");

        var result = catalog.RemoveSource("extension:Never.dll");

        Assert.True(result.IsSuccess);
        Assert.Single(catalog.List());
    }

    [Fact]
    public async Task ASourcesKeySet_IsNeverObservedHalfSwapped()
    {
        // Every other test here drives the catalog from one thread, where an in-place dictionary would
        // behave identically. Replacing a package's keys has to be one visible transition, or an update
        // arriving mid-swap routes on a key set that never existed.
        var catalog = new BehaviorCatalog();
        string[] before = ["a", "b", "c"];
        string[] after = ["d", "e", "f", "g"];
        catalog.ReplaceSource(Source, [.. before.Select(key => new FakeBehavior(key))]);
        var torn = new List<string>();

        using var deadline = Deadline();
        await Task.WhenAll(
            Race(deadline.Token, () =>
            {
                catalog.ReplaceSource(Source, [.. after.Select(key => new FakeBehavior(key))]);
                catalog.ReplaceSource(Source, [.. before.Select(key => new FakeBehavior(key))]);
            }),
            Race(deadline.Token, () =>
            {
                var observed = catalog.KeysFromSource(Source);

                if (!observed.SequenceEqual(before) && !observed.SequenceEqual(after))
                {
                    torn.Add(string.Join(",", observed));
                }
            }));

        Assert.True(torn.Count == 0, $"Observed {torn.Count} half-applied key set(s), e.g. [{torn.FirstOrDefault()}].");
    }

    [Fact]
    public async Task Reading_NeverFaults_WhileTheCatalogIsBeingWritten()
    {
        // TryGet is on the path of every update and List backs the admin API; neither takes the write
        // lock, which is only safe because what they read is immutable once published.
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        var generation = 0;

        using var deadline = Deadline();
        await Task.WhenAll(
            Race(deadline.Token, () =>
            {
                catalog.ReplaceSource(Source, [new FakeBehavior($"key-{generation++ % 8}")]);
                catalog.RemoveSource(Source);
            }),
            Race(deadline.Token, () =>
            {
                catalog.List();
                catalog.TryGet("echo", out _);
            }));

        Assert.True(catalog.TryGet("echo", out _));
    }

    /// <summary>Repeats <paramref name="iteration"/> on its own thread until the deadline passes.</summary>
    private static Task Race(CancellationToken deadline, Action iteration) =>
        Task.Run(
            () =>
            {
                while (!deadline.IsCancellationRequested)
                {
                    iteration();
                }
            },
            TestContext.Current.CancellationToken);

    /// <summary>Short: a torn read shows up within the first handful of the millions of iterations this allows.</summary>
    private static CancellationTokenSource Deadline()
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));

        return deadline;
    }

    private sealed class FakeBehavior(string key, string contractVersion = "1.0") : IBotBehavior
    {
        public string Key { get; } = key;
        public string DisplayName => $"Fake:{Key}";
        public string ContractVersion { get; } = contractVersion;
        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}