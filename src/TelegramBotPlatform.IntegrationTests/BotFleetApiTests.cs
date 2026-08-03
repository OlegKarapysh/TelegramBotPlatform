using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Requests;
using TelegramBotPlatform.Persistence;
using TelegramBotPlatform.Public;

namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// Managing the fleet through the admin API. The claim under test is that a lifecycle call is
/// <em>all-or-nothing across two systems</em> — the durable registry and the running receiver — which
/// neither the service nor the registry can demonstrate on its own.
/// </summary>
public class BotFleetApiTests
{
    private const string FirstBotToken = "111:first-bot-secret-token";
    private const string SecondBotToken = "222:second-bot-secret-token";

    [Fact]
    public async Task Register_PersistsTheBot_AndBringsItUpOnItsOwnWebhook()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.RegisterBot("Support bot", "echo", FirstBotToken);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var bot = await response.Read<BotResponse>();

        Assert.Equal($"/admin/bots/{bot.BotId}", response.Headers.Location?.ToString());
        Assert.Equal(111, bot.TelegramBotId);
        Assert.Equal("bot111", bot.Username);
        Assert.Equal("Support bot", bot.Label);
        Assert.Equal("echo", bot.BehaviorKey);
        Assert.Equal(nameof(BotStatus.Active), bot.Status);

        // The registry and the receiver agree: it is listed, and it is running under its own credentials.
        Assert.Equal(bot.BotId, Assert.Single(await platform.Admin.ListBots()).BotId);
        var client = platform.Clients.Client(bot.BotId);
        Assert.Equal(FirstBotToken, client.Token);

        var webhook = client.SingleRequest<SetWebhookRequest>();
        Assert.Equal($"https://platform.test/telegram-bot/webhook/{bot.BotId}", webhook.Url);
        Assert.False(string.IsNullOrWhiteSpace(webhook.SecretToken));
    }

    [Fact]
    public async Task TheBotToken_IsNeverEchoedBack_ByAnyReadOfTheBot()
    {
        await using var platform = PlatformTestHost.Start();

        var created = await platform.Admin.RegisterBot("Support bot", "echo", FirstBotToken);
        var bot = await created.Read<BotResponse>();

        var bodies = new[]
        {
            await created.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await (await platform.Admin.GetBot(bot.BotId)).Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await (await platform.Admin.GetAsync("/admin/bots", TestContext.Current.CancellationToken))
                .Content.ReadAsStringAsync(TestContext.Current.CancellationToken)
        };

        Assert.All(bodies, body => Assert.DoesNotContain("first-bot-secret-token", body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheBotToken_IsEncryptedAtRest_AndStillDecryptable()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        await platform.InScope(async services =>
        {
            var stored = await services.GetRequiredService<PlatformDbContext>().Bots
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == bot.Id, TestContext.Current.CancellationToken);

            // The row holds ciphertext — the plaintext is nowhere in the bytes, under any framing.
            Assert.DoesNotContain(
                Encoding.UTF8.GetString(stored.EncryptedToken), "first-bot-secret-token", StringComparison.Ordinal);
            Assert.NotEqual(Encoding.UTF8.GetBytes(FirstBotToken), stored.EncryptedToken);

            // And the platform can still get it back, through the real Data Protection key ring.
            Assert.Equal(
                FirstBotToken, services.GetRequiredService<ITokenProtector>().Unprotect(stored.EncryptedToken));
        });
    }

    [Fact]
    public async Task Register_IsRefused_ForAnUnknownBehavior_WithoutStartingAnything()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.RegisterBot("Support bot", "no-such-behavior", FirstBotToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown behavior", (await response.ReadError()).Error, StringComparison.Ordinal);
        Assert.Empty(await platform.Admin.ListBots());
        Assert.Empty(platform.Clients.LiveBotIds);
    }

    [Fact]
    public async Task Register_IsRefused_WhenTelegramRejectsTheToken_WithoutStartingAnything()
    {
        await using var platform = PlatformTestHost.Start();
        platform.Tokens.Rejected.Add(FirstBotToken);

        var response = await platform.Admin.RegisterBot("Support bot", "echo", FirstBotToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await platform.Admin.ListBots());
        Assert.Empty(platform.Clients.LiveBotIds);
    }

    [Fact]
    public async Task Register_Conflicts_WhenTheSameTelegramBotIsAlreadyRegistered_LeavingTheFirstRunning()
    {
        await using var platform = PlatformTestHost.Start();
        var first = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        // A second token for the same Telegram bot: a duplicate, not a new bot.
        var response = await platform.Admin.RegisterBot("Support bot again", "echo", "111:another-token");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(first.Id, Assert.Single(await platform.Admin.ListBots()).BotId);
        Assert.Equal(FirstBotToken, first.Client.Token);
    }

    [Fact]
    public async Task List_ReturnsEveryRegisteredBot()
    {
        await using var platform = PlatformTestHost.Start();
        var first = await platform.RegisterBot("First", "echo", FirstBotToken);
        var second = await platform.RegisterBot("Second", "echo", SecondBotToken);

        var bots = await platform.Admin.ListBots();

        Assert.Equal([first.Id, second.Id], bots.Select(bot => bot.BotId).Order().ToArray());
        Assert.Equal([111, 222], bots.Select(bot => bot.TelegramBotId).Order().ToArray());
    }

    [Fact]
    public async Task Get_Returns404_ForABotThatWasNeverRegistered()
    {
        await using var platform = PlatformTestHost.Start();

        var response = await platform.Admin.GetBot(404);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Disable_MarksTheBotDisabled_AndTakesDownItsWebhook()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        var response = await platform.Admin.DisableBot(bot.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(BotStatus.Disabled), (await bot.Current()).Status);
        Assert.Single(bot.Client.RequestsOf<DeleteWebhookRequest>());
    }

    [Fact]
    public async Task Enable_BringsTheBotBack_UsingTheTokenItHadStored()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);
        await AdminApi.AssertStatus(await platform.Admin.DisableBot(bot.Id), HttpStatusCode.OK);

        var response = await platform.Admin.EnableBot(bot.Id);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(nameof(BotStatus.Active), (await bot.Current()).Status);

        // Enabling rebuilds the client from scratch, so this is the original token coming back out of the
        // encrypted column through the key ring — nothing was kept in memory across the disable.
        Assert.Equal(FirstBotToken, bot.Client.Token);
        Assert.Equal(
            $"https://platform.test/telegram-bot/webhook/{bot.Id}", bot.Client.SingleRequest<SetWebhookRequest>().Url);

        // And it is genuinely serving again, not merely marked Active.
        Assert.Equal(["echo: back up"], await bot.DeliverAndAwaitReply("back up", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RotateToken_SwapsTheCredentials_AndReRegistersTheWebhook()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        var response = await platform.Admin.RotateToken(bot.Id, "111:rotated-secret-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("111:rotated-secret-token", bot.Client.Token);
        Assert.Equal($"https://platform.test/telegram-bot/webhook/{bot.Id}", bot.Client.LastRequest<SetWebhookRequest>().Url);
    }

    [Fact]
    public async Task RotateToken_KeepsWorking_ForUpdatesThatArriveAfterwards()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);
        await AdminApi.AssertStatus(
            await platform.Admin.RotateToken(bot.Id, "111:rotated-secret-token"), HttpStatusCode.OK);

        // A rotation replaces the client, so the reply has to go out through the *new* one.
        var replies = await bot.DeliverAndAwaitReply("hello", TestContext.Current.CancellationToken);

        Assert.Equal(["echo: hello"], replies);
        Assert.Equal("111:rotated-secret-token", bot.Client.Token);
    }

    [Fact]
    public async Task RotateToken_IsRefused_WhenTheTokenBelongsToADifferentTelegramBot()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        // Pointing a registration at someone else's bot would silently re-target it; that must not happen.
        var response = await platform.Admin.RotateToken(bot.Id, SecondBotToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(FirstBotToken, bot.Client.Token);
        Assert.Single(bot.Client.RequestsOf<SetWebhookRequest>());
    }

    [Fact]
    public async Task Remove_DeletesTheRegistration_AndForgetsTheClient()
    {
        await using var platform = PlatformTestHost.Start();
        var bot = await platform.RegisterBot("Support bot", "echo", FirstBotToken);

        var response = await platform.Admin.RemoveBot(bot.Id);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await platform.Admin.GetBot(bot.Id)).StatusCode);
        Assert.Empty(await platform.Admin.ListBots());
        Assert.DoesNotContain(bot.Id, platform.Clients.LiveBotIds);
    }

    public static TheoryData<string> LifecycleCalls => ["disable", "enable", "rotate", "remove"];

    [Theory]
    [MemberData(nameof(LifecycleCalls))]
    public async Task EveryLifecycleCall_Returns404_ForABotThatWasNeverRegistered(string call)
    {
        await using var platform = PlatformTestHost.Start();

        var response = call switch
        {
            "disable" => await platform.Admin.DisableBot(404),
            "enable" => await platform.Admin.EnableBot(404),
            "rotate" => await platform.Admin.RotateToken(404, FirstBotToken),
            _ => await platform.Admin.RemoveBot(404)
        };

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ActingOnOneBot_LeavesEveryOtherBotRunning()
    {
        await using var platform = PlatformTestHost.Start();
        var first = await platform.RegisterBot("First", "echo", FirstBotToken);
        var second = await platform.RegisterBot("Second", "echo", SecondBotToken);

        await AdminApi.AssertStatus(await platform.Admin.DisableBot(first.Id), HttpStatusCode.OK);
        await AdminApi.AssertStatus(await platform.Admin.RemoveBot(first.Id), HttpStatusCode.NoContent);

        // The fleet is per-bot: taking one down must not touch another's registration, client or webhook.
        Assert.Equal(nameof(BotStatus.Active), (await second.Current()).Status);
        Assert.Equal(SecondBotToken, second.Client.Token);
        Assert.Empty(second.Client.RequestsOf<DeleteWebhookRequest>());
        Assert.Equal(["echo: still here"], await second.DeliverAndAwaitReply("still here", TestContext.Current.CancellationToken));
    }
}