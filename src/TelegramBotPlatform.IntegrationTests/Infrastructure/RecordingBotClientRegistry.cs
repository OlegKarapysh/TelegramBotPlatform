using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Requests.Abstractions;
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

    /// <summary>
    /// Makes Telegram refuse calls, for the failures the platform can only discover by making one — a
    /// <c>setWebhook</c> against an unreachable API, or a URL Telegram will not accept.
    /// <para>
    /// Consulted per call rather than fixed when a client is created, so a test can arm it around exactly
    /// the operation it is about and leave every other call working. Null (the default) is Telegram
    /// behaving.
    /// </para>
    /// </summary>
    public Func<IRequest, Exception?>? Failure { get; set; }

    /// <summary>Arms <see cref="Failure"/> for one request type — the usual shape of "Telegram refused this".</summary>
    public void FailEvery<TRequest>(string reason) where TRequest : IRequest =>
        Failure = request => request is TRequest ? new HttpRequestException(reason) : null;

    public void AcceptEverything() => Failure = null;

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

    // The failure hook is read through this registry on every call, not captured here, so arming it after
    // a client already exists still affects that client.
    public void Set(long botId, string token) =>
        _clients[botId] = new RecordingTelegramBotClient(botId, token, request => Failure?.Invoke(request));

    public void Remove(long botId) => _clients.TryRemove(botId, out _);
}