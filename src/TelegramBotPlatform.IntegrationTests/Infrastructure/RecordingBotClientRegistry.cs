using System.Collections.Concurrent;
using Telegram.Bot;
using TelegramBotPlatform.Public;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Drop-in replacement for the production <c>BotClientRegistry</c> that hands out
/// <see cref="RecordingTelegramBotClient"/>s. Mirrors the real registry's contract exactly — including
/// <see cref="Get"/> throwing for an unknown bot — so <c>BotSupervisor</c>, <c>BotUpdateRouter</c> and the
/// scoped <c>ITelegramBotClient</c> registration all run their real code against it.
/// </summary>
public sealed class RecordingBotClientRegistry : IBotClientRegistry
{
    private readonly ConcurrentDictionary<long, RecordingTelegramBotClient> _clients = new();

    /// <summary>Bot ids with a live client, i.e. the bots the supervisor currently has running.</summary>
    public IReadOnlyList<long> LiveBotIds => _clients.Keys.Order().ToArray();

    /// <summary>The recording client for <paramref name="botId"/>, failing the test if the bot never started.</summary>
    public RecordingTelegramBotClient Client(long botId)
    {
        Assert.True(
            _clients.TryGetValue(botId, out var client),
            $"No client was created for bot {botId}; live bots are [{string.Join(", ", LiveBotIds)}].");

        return client!;
    }

    public bool TryGetClient(long botId, out RecordingTelegramBotClient? client) =>
        _clients.TryGetValue(botId, out client);

    public ITelegramBotClient Get(long botId) =>
        _clients.TryGetValue(botId, out var client)
            ? client
            : throw new InvalidOperationException($"No Telegram client is registered for bot {botId}.");

    public bool TryGet(long botId, out ITelegramBotClient? client)
    {
        var found = _clients.TryGetValue(botId, out var recording);
        client = recording;

        return found;
    }

    public void Set(long botId, string token) => _clients[botId] = new RecordingTelegramBotClient(botId, token);

    public void Remove(long botId) => _clients.TryRemove(botId, out _);
}