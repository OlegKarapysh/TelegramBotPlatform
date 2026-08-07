namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// Adding, replacing and retiring behavior extensions on a platform that is already serving.
/// <para>
/// These use the real sample assembly and the real loader, so the assembly really is loaded into a
/// collectible <c>AssemblyLoadContext</c> and its <c>IBotBehavior</c> really has to unify with the host's
/// interface type across that boundary. The payoff assertion throughout is not a status code but a bot:
/// after an upload a bot assigned to the new behavior answers, and after a refused change it still does.
/// </para>
/// </summary>
public sealed class BehaviorExtensionApiTests
{
    private const string BotToken = "111:extension-bot-token";

    [Fact]
    public async Task Upload_AddsANewBehavior_ToARunningPlatform()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.UploadBehavior(SamplePlugin.FileName, SamplePlugin.Bytes);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var uploaded = await response.Read<ExtensionResponse>();
        Assert.Equal(SamplePlugin.FileName, uploaded.Assembly);
        Assert.Equal([SamplePlugin.BehaviorKey], uploaded.Loaded);
        Assert.Equal($"/admin/behaviors/{SamplePlugin.FileName}", response.Headers.Location?.ToString());
        var behaviors = await platform.Admin.ListBehaviors();
        var reverse = Assert.Single(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);
        Assert.Equal($"extension:{SamplePlugin.FileName}", reverse.Source);
        Assert.True(Assert.Single(behaviors.Packages).Loaded);
        // Durable, not just loaded: the bytes are in the store, so a restart finds them.
        Assert.True(File.Exists(Path.Combine(platform.PluginsDirectory, SamplePlugin.FileName)));
    }

    [Fact]
    public async Task ABehaviorKey_IsNotAssignable_BeforeItsPackageIsUploaded()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnUploadedBehavior_CanRunABot_WithoutARestart()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);

        var replies = await bot.DeliverAndAwaitReply("desserts", TestContext.Current.CancellationToken);

        // Code that was not in the host when it started is now handling live traffic.
        Assert.Equal(["stressed"], replies);
    }

    [Fact]
    public async Task Upload_Conflicts_WhenTheNameIsAlreadyStored_LeavingTheRunningOneAlone()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);

        var response = await platform.Admin.UploadBehavior(SamplePlugin.FileName, SamplePlugin.Corrupt);

        // Create-only: an accidental re-upload must never quietly supersede a working extension.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SamplePlugin.Bytes, await File.ReadAllBytesAsync(
            Path.Combine(platform.PluginsDirectory, SamplePlugin.FileName), TestContext.Current.CancellationToken));
        Assert.Equal(["cba"], await bot.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Upload_StoresUnderAPlainName_WhenTheClientSendsAPath()
    {
        await using var platform = PlatformTestHost.Start();

        var uploaded = await platform.Admin.UploadBehaviorOk("packages/Reverse.dll", SamplePlugin.Bytes);

        // The store's root is scoped by an access policy, so a name that kept its path would put the
        // package somewhere that policy does not cover.
        Assert.Equal("Reverse.dll", uploaded.Assembly);
        Assert.True(File.Exists(Path.Combine(platform.PluginsDirectory, "Reverse.dll")));
        Assert.False(Directory.Exists(Path.Combine(platform.PluginsDirectory, "packages")));
        Assert.Equal("Reverse.dll", Assert.Single((await platform.Admin.ListBehaviors()).Packages).PackageName);
    }

    [Fact]
    public async Task Upload_IsRefused_ForANameThatIsNotAnAssembly()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.UploadBehavior("Reverse.txt", SamplePlugin.Bytes);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetFiles(platform.PluginsDirectory));
        Assert.Empty((await platform.Admin.ListBehaviors()).Packages);
    }

    [Fact]
    public async Task Upload_RejectsAPackageThatWillNotLoad_AndLeavesNothingStored()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.UploadBehavior("Broken.dll", SamplePlugin.Corrupt);

        // The write is undone, so a bad upload cannot linger and fail again on every future startup.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(platform.PluginsDirectory, "Broken.dll")));
        Assert.Empty((await platform.Admin.ListBehaviors()).Packages);
    }

    [Fact]
    public async Task Upload_RejectsAPackage_OverTheConfiguredSizeLimit()
    {
        await using var platform = PlatformTestHost.Start(
            new PlatformTestSettings { MaxExtensionPackageBytes = 1024 * 1024 });

        var response = await platform.Admin.UploadBehavior("Huge.dll", new byte[2 * 1024 * 1024]);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("1 MB limit", (await response.ReadError()).Error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(platform.PluginsDirectory));
    }

    [Fact]
    public async Task Replace_HotSwapsTheExtension_ForABotThatIsAlreadyRunning()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);
        await bot.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken);

        var response = await platform.Admin.ReplaceBehavior(SamplePlugin.FileName, SamplePlugin.Bytes);

        // The bot was never restarted or re-registered; its next update runs on the new build.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([SamplePlugin.BehaviorKey], (await response.Read<ExtensionResponse>()).Loaded);
        Assert.Equal(["cba", "fed"], await bot.DeliverAndAwaitReply("def", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replace_ChangesNothing_WhenTheNewBuildWillNotLoad()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);

        var response = await platform.Admin.ReplaceBehavior(SamplePlugin.FileName, SamplePlugin.Corrupt);

        // Rolled back completely: the stored bytes, the registered behavior and the running bot are as
        // they were, which is what makes shipping a new build a safe operation to attempt.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(SamplePlugin.Bytes, await File.ReadAllBytesAsync(
            Path.Combine(platform.PluginsDirectory, SamplePlugin.FileName), TestContext.Current.CancellationToken));
        Assert.True(Assert.Single((await platform.Admin.ListBehaviors()).Packages).Loaded);
        Assert.Equal(["cba"], await bot.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Replace_Returns404_ForAPackageThatWasNeverStored()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.ReplaceBehavior(SamplePlugin.FileName, SamplePlugin.Bytes);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Remove_IsRefused_WhileABotIsStillAssignedToTheBehavior()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);

        var response = await platform.Admin.RemoveBehavior(SamplePlugin.FileName);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        // The blocking bots come back as data, so tooling never parses the message to find them.
        var error = await response.ReadError();
        Assert.Equal([bot.Id], error.Bots);
        Assert.Contains(SamplePlugin.BehaviorKey, error.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(platform.PluginsDirectory, SamplePlugin.FileName)));
        Assert.Equal(["cba"], await bot.DeliverAndAwaitReply("abc", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_RetiresTheBehavior_OnceNoBotIsAssignedToIt()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        var bot = await platform.RegisterBot("Reverse bot", SamplePlugin.BehaviorKey, BotToken);
        await platform.Admin.RemoveBotOk(bot.Id);

        var response = await platform.Admin.RemoveBehavior(SamplePlugin.FileName);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var behaviors = await platform.Admin.ListBehaviors();
        Assert.DoesNotContain(behaviors.Behaviors, behavior => behavior.Key == SamplePlugin.BehaviorKey);
        Assert.Contains(behaviors.Behaviors, behavior => behavior.Key == "echo");
        Assert.Empty(behaviors.Packages);
        Assert.False(File.Exists(Path.Combine(platform.PluginsDirectory, SamplePlugin.FileName)));
    }

    [Fact]
    public async Task ARetiredBehavior_IsNoLongerAssignable()
    {
        await using var platform = PlatformTestHost.Start();
        await platform.Admin.UploadBehaviorOk(SamplePlugin.FileName, SamplePlugin.Bytes);
        await platform.Admin.RemoveBehaviorOk(SamplePlugin.FileName);

        var response = await platform.Admin.RegisterBot("Too late", SamplePlugin.BehaviorKey, BotToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Remove_Returns404_ForAPackageThatWasNeverStored()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.RemoveBehavior(SamplePlugin.FileName);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnUnreachableStore_IsReportedAsUnavailable_NotAsABadRequest()
    {
        var store = new ControllableExtensionStore();
        await using var platform = PlatformTestHost.Start(new PlatformTestSettings { ExtensionStore = store });
        store.IsReachable = false;

        var response = await platform.Admin.UploadBehavior(SamplePlugin.FileName, SamplePlugin.Bytes);

        // An outage is the platform's problem, not the caller's: 400 would send an operator looking for a
        // fault in their package.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}