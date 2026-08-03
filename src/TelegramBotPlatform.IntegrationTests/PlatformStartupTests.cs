namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// What the host has to have done <em>before</em> it serves its first request, and what it must refuse to
/// start at all for. These properties are startup ordering, so nothing below the host level can check them.
/// </summary>
public class PlatformStartupTests
{
    [Fact]
    public async Task Health_IsServed_WithoutAnAdminKey()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Anonymous.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task BuiltInBehaviors_AreRegistered_BeforeTheHostServes()
    {
        await using var platform = PlatformTestHost.Start();

        var behaviors = await platform.Admin.ListBehaviors();

        var echo = Assert.Single(behaviors.Behaviors, behavior => behavior.Key == "echo");
        Assert.Equal("built-in", echo.Source);
        Assert.Empty(behaviors.Packages);
    }

    [Fact]
    public async Task StoredExtensions_AreRestored_BeforeTheHostServes()
    {
        using var pluginsDirectory = TemporaryDirectory.Create();
        await pluginsDirectory.Write(SamplePlugin.FileName, SamplePlugin.Bytes, TestContext.Current.CancellationToken);

        await using var platform = PlatformTestHost.Start(
            new PlatformTestSettings { PluginsDirectory = pluginsDirectory.Path });

        // Assignable on the very first request, with no upload in this process — the package was picked up
        // from durable storage during startup.
        var behaviors = await platform.Admin.ListBehaviors();

        var reverse = Assert.Single(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);
        Assert.Equal($"extension:{SamplePlugin.FileName}", reverse.Source);

        var package = Assert.Single(behaviors.Packages);
        Assert.True(package.Loaded);
        Assert.Equal([SamplePlugin.BehaviorKey], package.BehaviorKeys);
        Assert.Null(package.Error);
    }

    [Fact]
    public async Task AStoredPackageThatWillNotLoad_IsReported_AndTheRestOfThePlatformStillServes()
    {
        using var pluginsDirectory = TemporaryDirectory.Create();
        await pluginsDirectory.Write("Broken.dll", SamplePlugin.Corrupt, TestContext.Current.CancellationToken);
        await pluginsDirectory.Write(SamplePlugin.FileName, SamplePlugin.Bytes, TestContext.Current.CancellationToken);

        await using var platform = PlatformTestHost.Start(
            new PlatformTestSettings { PluginsDirectory = pluginsDirectory.Path });

        var behaviors = await platform.Admin.ListBehaviors();

        // One bad package is contained: it is named, explained and still addressable for repair, while the
        // good package next to it loaded and the host came up.
        var broken = Assert.Single(behaviors.Packages, package => package.PackageName == "Broken.dll");
        Assert.False(broken.Loaded);
        Assert.Empty(broken.BehaviorKeys);
        Assert.NotNull(broken.Error);

        Assert.Contains(behaviors.Packages, package => package is { PackageName: SamplePlugin.FileName, Loaded: true });
        Assert.Contains(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);
        Assert.Contains(behaviors.Behaviors, behavior => behavior.Key == "echo");
    }

    [Fact]
    public void AnUnreachableExtensionStore_AbortsStartup_RatherThanServingAnIncompleteCatalog()
    {
        var store = new ControllableExtensionStore { IsReachable = false };

        // Fail closed: the process never binds a port, so a rollout gated on health rolls back instead of
        // going green with behaviors silently missing.
        var failure = Assert.Throws<InvalidOperationException>(
            () => PlatformTestHost.Start(new PlatformTestSettings { ExtensionStore = store }).Dispose());

        Assert.Contains("Refusing to serve", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReachableButEmptyStore_IsANormalStart()
    {
        var store = new ControllableExtensionStore();

        await using var platform = PlatformTestHost.Start(new PlatformTestSettings { ExtensionStore = store });

        var behaviors = await platform.Admin.ListBehaviors();

        Assert.Empty(behaviors.Packages);
        Assert.Contains(behaviors.Behaviors, behavior => behavior.Key == "echo");
    }
}