using FluentResults;
using TelegramBotPlatform.Public;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Stands in for the Telegram <c>getMe</c> call that validates a candidate token, without a network.
/// <para>
/// A token is accepted when it has Telegram's own <c>&lt;telegram-bot-id&gt;:&lt;secret&gt;</c> shape, and
/// the id it reports is the numeric prefix — so <c>"111:first"</c> and <c>"111:rotated"</c> are two tokens
/// for the <em>same</em> Telegram bot while <c>"222:other"</c> is a different one. That is the distinction
/// the registration and rotation rules turn on, and it keeps every test's intent readable from its tokens.
/// Anything else is rejected the way Telegram rejects a malformed token.
/// </para>
/// </summary>
public sealed class ScriptedTokenValidator : IBotTokenValidator
{
    /// <summary>Tokens to reject even though they are well-formed — a revoked or wrong-bot token.</summary>
    public HashSet<string> Rejected { get; } = new(StringComparer.Ordinal);

    public Task<Result<(long TelegramBotId, string? Username)>> Validate(
        string token, CancellationToken cancellationToken = default)
    {
        var separator = token.IndexOf(':', StringComparison.Ordinal);

        if (Rejected.Contains(token)
            || separator <= 0
            || separator == token.Length - 1
            || !long.TryParse(token[..separator], out var telegramBotId))
        {
            return Task.FromResult(Result.Fail<(long, string?)>("Telegram rejected the bot token."));
        }

        return Task.FromResult(Result.Ok<(long, string?)>((telegramBotId, $"bot{telegramBotId}")));
    }
}