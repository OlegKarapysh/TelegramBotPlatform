namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// Webhook bodies in Telegram's own wire format. Written as literal JSON rather than serialised from
/// <c>Telegram.Bot</c>'s types on purpose: it is the payload shape the platform has to keep working
/// against, and a hand-written body cannot drift into agreement with the endpoint the way a round-trip
/// through the same serialiser silently would.
/// </summary>
internal static class TelegramPayload
{
    // A fixed instant (2026-01-01T00:00:00Z) — nothing under test reads it, and a moving value would be
    // one more thing that differs between two runs of the same test.
    private const long SentAt = 1767225600;

    public static string TextMessage(string text, long chatId = 4242, int updateId = 1) =>
        $$"""
          {
            "update_id": {{updateId}},
            "message": {
              "message_id": {{updateId}},
              "date": {{SentAt}},
              "chat": { "id": {{chatId}}, "type": "private" },
              "from": { "id": {{chatId}}, "is_bot": false, "first_name": "Tester" },
              "text": {{JsonSerializer.Serialize(text)}}
            }
          }
          """;

    /// <summary>
    /// An update carrying no message at all — Telegram sends plenty of these (poll answers, chat-member
    /// changes). A behavior is expected to ignore it rather than fail on it.
    /// </summary>
    public static string NonMessageUpdate(int updateId = 1) =>
        $$"""
          {
            "update_id": {{updateId}},
            "poll_answer": {
              "poll_id": "1",
              "option_ids": [0],
              "user": { "id": 4242, "is_bot": false, "first_name": "Tester" }
            }
          }
          """;
}