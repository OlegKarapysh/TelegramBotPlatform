using TelegramBotPlatform.Persistence;
using TelegramBotPlatform.Persistence.Repositories;
using TelegramBotPlatform.Public.Bots;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// The sanctioned exception to "no database in the unit tests": the EF in-memory provider, so the real
/// repository's own query and change-tracking code is what runs.
/// </summary>
public sealed class PostgresBotRegistryTests : IAsyncDisposable
{
    private static readonly byte[] _token = "encrypted-token"u8.ToArray();

    private readonly PlatformDbContext _dbContext = InMemoryDbContextFactory.Create();

    [Fact]
    public async Task Get_ReturnsNull_WhenNotRegistered()
    {
        var registry = CreateRegistry();

        var registration = await registry.Get(botId: 1, TestContext.Current.CancellationToken);

        Assert.Null(registration);
    }

    [Fact]
    public async Task Add_ThenGet_ReturnsTheRegistration()
    {
        var registry = CreateRegistry();

        var addResult = await registry.Add(
            telegramBotId: 111, "echo_bot", "Echo", "echo", _token, TestContext.Current.CancellationToken);

        Assert.True(addResult.IsSuccess);
        var registration = addResult.Value;
        Assert.Equal(111, registration.TelegramBotId);
        Assert.Equal("echo_bot", registration.Username);
        Assert.Equal("Echo", registration.Label);
        Assert.Equal("echo", registration.BehaviorKey);
        Assert.Equal(BotStatus.Active, registration.Status);
        Assert.Equal(registration, await registry.Get(registration.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Add_Fails_WhenTelegramBotIdAlreadyRegistered()
    {
        var registry = CreateRegistry();
        await Register(registry);

        var result = await registry.Add(111, "echo_bot", "Echo Again", "echo", _token, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("already registered", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetEncryptedToken_ReturnsStoredCiphertext()
    {
        var registry = CreateRegistry();
        var registration = await Register(registry);

        var storedToken = await registry.GetEncryptedToken(registration.Id, TestContext.Current.CancellationToken);

        Assert.Equal(_token, storedToken);
    }

    [Fact]
    public async Task List_ReturnsAllRegisteredBots()
    {
        var registry = CreateRegistry();
        await Register(registry, telegramBotId: 111);
        await Register(registry, telegramBotId: 222);

        var bots = await registry.List(TestContext.Current.CancellationToken);

        Assert.Equal(2, bots.Count);
    }

    [Fact]
    public async Task UpdateStatus_ChangesStatus()
    {
        var registry = CreateRegistry();
        var registration = await Register(registry);

        var result = await registry.UpdateStatus(registration.Id, BotStatus.Disabled, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var fetched = await registry.Get(registration.Id, TestContext.Current.CancellationToken);
        Assert.Equal(BotStatus.Disabled, fetched!.Status);
    }

    [Fact]
    public async Task UpdateStatus_Fails_WhenBotDoesNotExist()
    {
        var registry = CreateRegistry();

        var result = await registry.UpdateStatus(botId: 999, BotStatus.Disabled, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
    }

    [Fact]
    public async Task UpdateToken_ReplacesToken_WhenTelegramBotIdMatches()
    {
        var registry = CreateRegistry();
        var registration = await Register(registry);
        var newToken = "new-encrypted-token"u8.ToArray();

        var result = await registry.UpdateToken(registration.Id, 111, newToken, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(newToken, await registry.GetEncryptedToken(registration.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateToken_Fails_WhenTelegramBotIdDiffers()
    {
        var registry = CreateRegistry();
        var registration = await Register(registry);

        var result = await registry.UpdateToken(
            registration.Id, 999, "irrelevant"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
        Assert.Contains("different Telegram bot", result.Errors.First().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Remove_DeletesTheRegistration()
    {
        var registry = CreateRegistry();
        var registration = await Register(registry);

        var result = await registry.Remove(registration.Id, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(await registry.Get(registration.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Remove_Fails_WhenBotDoesNotExist()
    {
        var registry = CreateRegistry();

        var result = await registry.Remove(botId: 999, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailed);
    }

    public ValueTask DisposeAsync() => _dbContext.DisposeAsync();

    private PostgresBotRegistry CreateRegistry() => new(_dbContext);

    private static async Task<BotRegistration> Register(PostgresBotRegistry registry, long telegramBotId = 111) =>
        (await registry.Add(
            telegramBotId, "echo_bot", "Echo", "echo", _token, TestContext.Current.CancellationToken)).Value;
}