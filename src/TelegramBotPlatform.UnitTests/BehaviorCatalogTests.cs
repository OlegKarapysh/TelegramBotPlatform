using TelegramBotPlatform.Application;
using TelegramBotPlatform.Public.Behaviors;

namespace TelegramBotPlatform.UnitTests;

public class BehaviorCatalogTests
{
    private sealed class FakeBehavior(string key, string contractVersion = "1.0") : IBotBehavior
    {
        public string Key { get; } = key;
        public string DisplayName => $"Fake:{Key}";
        public string ContractVersion { get; } = contractVersion;
        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

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

        Assert.False(catalog.TryGet("missing", out var behavior));
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
        Assert.Contains(descriptors, d => d.Key == "echo" && d.Source == "built-in");
        Assert.Contains(descriptors, d => d.Key == "reverse" && d.Source == "extension:Reverse.dll");
    }

    [Fact]
    public void KeysFromSource_ReturnsOnlyThatSourcesKeys()
    {
        var catalog = new BehaviorCatalog();
        catalog.Register(new FakeBehavior("echo"), "built-in");
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse"), new FakeBehavior("reverse-words")]);

        Assert.Equal(["reverse", "reverse-words"], catalog.KeysFromSource("extension:Reverse.dll"));
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
        Assert.Equal("built-in", catalog.List().Single(d => d.Key == "echo").Source);
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

        Assert.True(catalog.RemoveSource("extension:Never.dll").IsSuccess);
        Assert.Single(catalog.List());
    }

    [Fact]
    public void Register_Fails_WhenKeyBelongsToAnExtensionSource()
    {
        var catalog = new BehaviorCatalog();
        catalog.ReplaceSource("extension:Reverse.dll", [new FakeBehavior("reverse")]);

        var result = catalog.Register(new FakeBehavior("reverse"), "built-in");

        Assert.True(result.IsFailed);
    }
}