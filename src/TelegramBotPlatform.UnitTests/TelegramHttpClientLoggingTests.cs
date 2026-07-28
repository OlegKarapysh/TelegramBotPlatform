using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TelegramBotPlatform.Infrastructure;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// A bot token travels in the Telegram request path, and <c>IHttpClientFactory</c>'s default handlers log
/// the full request URI at Information level — so the out-of-the-box configuration writes every bot's
/// credential to the log sink on every call. These tests hold the line on that, driving a real client
/// through a stub handler (no network) and asserting the token never reaches a logger.
/// </summary>
public class TelegramHttpClientLoggingTests
{
    private const string Token = "123456:SUPER-SECRET-BOT-TOKEN";

    [Theory]
    [InlineData("BotClientRegistry")]
    [InlineData("TelegramBotTokenValidator")]
    public async Task TelegramHttpClients_DoNotLogTheRequestUri_WhichCarriesTheBotToken(string clientName)
    {
        var sink = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddTelegramHttpClients();
        // Swap in a stub so the test never leaves the process; this does not alter the logging setup.
        services.AddHttpClient(clientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler());

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        await client.GetAsync(
            new Uri($"https://api.telegram.org/bot{Token}/getMe"), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(sink.Messages, message => message.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnconfiguredHttpClient_DoesLogTheToken_ProvingTheTestWouldCatchARegression()
    {
        // Guards the guard: if RemoveAllLoggers were dropped, the assertion above must actually fail.
        var sink = new CapturingLoggerProvider();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(sink);
        });
        services.AddHttpClient("unprotected").ConfigurePrimaryHttpMessageHandler(() => new StubHandler());

        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("unprotected");

        await client.GetAsync(
            new Uri($"https://api.telegram.org/bot{Token}/getMe"), TestContext.Current.CancellationToken);

        Assert.Contains(sink.Messages, message => message.Contains(Token, StringComparison.Ordinal));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return _messages.ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                Record(state?.ToString());
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                Record(formatter(state, exception));

            private void Record(string? message)
            {
                if (message is null)
                {
                    return;
                }

                lock (messages)
                {
                    messages.Add(message);
                }
            }
        }
    }
}