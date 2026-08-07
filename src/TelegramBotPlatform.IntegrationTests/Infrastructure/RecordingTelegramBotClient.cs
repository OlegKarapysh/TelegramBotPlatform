using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// One bot's Telegram client, recording every call instead of making it. Telegram is the platform's only
/// real external dependency, so this is the single seam these tests cut — everything on this side of it
/// (endpoints, bus, supervisor, catalog, loader, registry, Data Protection) is the production code.
/// <para>
/// Tests assert on what the platform <em>asked Telegram to do</em>: the webhook it registered and with
/// which secret, the replies a behavior sent, and — because each client is built from one token — which
/// bot's credentials were used to send them.
/// </para>
/// </summary>
/// <param name="failure">
/// Consulted per call, so a test can make Telegram refuse a specific request — the failure the platform
/// cannot check for in advance, because it only happens once the call is made. Returning null (the
/// default) is Telegram accepting everything, which is what every other test wants.
/// </param>
public sealed class RecordingTelegramBotClient(long botId, string token, Func<IRequest, Exception?>? failure = null)
    : ITelegramBotClient
{
    private readonly Lock _gate = new();
    private readonly List<IRequest> _requests = [];
    private readonly Func<IRequest, Exception?> _failure = failure ?? (_ => null);

    /// <summary>The plaintext token this client was built from — how tests check a rotation took effect.</summary>
    public string Token { get; } = token;

    public long BotId { get; } = botId;

    /// <summary>Every Telegram API call made through this client, oldest first.</summary>
    public IReadOnlyList<IRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    public IReadOnlyList<TRequest> RequestsOf<TRequest>() where TRequest : IRequest =>
        Requests.OfType<TRequest>().ToArray();

    /// <summary>The single call of this type, failing with a readable message when there is not exactly one.</summary>
    public TRequest SingleRequest<TRequest>() where TRequest : IRequest
    {
        var matches = RequestsOf<TRequest>();

        Assert.True(
            matches.Count == 1,
            $"Expected exactly one {typeof(TRequest).Name} on bot {BotId}, but saw {matches.Count}. "
            + $"All calls: [{string.Join(", ", Requests.Select(request => request.MethodName))}]");

        return matches[0];
    }

    /// <summary>The most recent call of this type — e.g. the webhook currently registered, after a rotation.</summary>
    public TRequest LastRequest<TRequest>() where TRequest : IRequest
    {
        var matches = RequestsOf<TRequest>();

        Assert.True(
            matches.Count > 0,
            $"Expected at least one {typeof(TRequest).Name} on bot {BotId}, but saw none. "
            + $"All calls: [{string.Join(", ", Requests.Select(request => request.MethodName))}]");

        return matches[^1];
    }

    /// <summary>Text of every message this bot sent, in order — the observable output of its behavior.</summary>
    public IReadOnlyList<string> SentMessages =>
        RequestsOf<SendMessageRequest>().Select(request => request.Text).ToArray();

    /// <summary>
    /// Waits until this bot has sent <paramref name="count"/> messages, then returns them.
    /// <para>
    /// The platform hands an update to its behavior off the bus, after the webhook request has already
    /// been answered — so a reply is the one effect a test cannot observe synchronously. This waits on
    /// that effect rather than on a duration: it returns the moment the reply lands.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<string>> WaitForSentMessages(int count, CancellationToken cancellationToken)
    {
        await Wait.Until(
            () => SentMessages.Count >= count,
            () => $"bot {BotId} to send {count} message(s); it sent [{string.Join(", ", SentMessages)}]",
            cancellationToken);

        return SentMessages;
    }

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        // Recorded before it is failed: "the platform did try" and "Telegram refused" are different
        // facts, and a test about the second still wants the first.
        return _failure(request) is { } exception
            ? Task.FromException<TResponse>(exception)
            : Task.FromResult(Response<TResponse>());
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public bool LocalBotServer => false;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

    // Declared with explicit accessors rather than as field-like events: nothing raises them, and a
    // field-like event that is never invoked is a warning, which this build treats as an error.
    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }

    /// <summary>
    /// A stand-in success response. Telegram's request types are plain DTOs with parameterless
    /// constructors, so an empty instance is enough for the platform's "the call succeeded" paths — no
    /// behavior under test reads anything back out of a reply.
    /// </summary>
    private static TResponse Response<TResponse>() =>
        typeof(TResponse) == typeof(bool)
            ? (TResponse)(object)true
            : Activator.CreateInstance<TResponse>();
}