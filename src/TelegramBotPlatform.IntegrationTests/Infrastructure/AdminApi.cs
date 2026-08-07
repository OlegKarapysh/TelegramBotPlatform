using System.Net.Http.Headers;
using System.Text;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>A bot as the admin API reports it. Mirrors the endpoint's response shape, nothing more.</summary>
public sealed record BotResponse(
    long BotId,
    long TelegramBotId,
    string? Username,
    string Label,
    string BehaviorKey,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>The result of uploading or replacing a behavior extension.</summary>
public sealed record ExtensionResponse(IReadOnlyList<string> Loaded, string Assembly);

/// <summary><c>GET /admin/behaviors</c>: what is assignable, and what is stored.</summary>
public sealed record BehaviorsResponse(
    IReadOnlyList<BehaviorDescriptor> Behaviors,
    IReadOnlyList<ExtensionPackageStatus> Packages);

/// <summary>A refusal. <c>Bots</c> is set only when the refusal was "a bot is still using this".</summary>
public sealed record ApiError(string Error, IReadOnlyList<long>? Bots);

/// <summary>
/// Thin, intention-revealing wrappers over the admin HTTP surface. They compose the request and read the
/// response — no assertions and no retries, so a test still states its own expectations, and a status code
/// that changes is a test failure rather than something a helper quietly swallows.
/// </summary>
public static class AdminApi
{
    extension(HttpClient client)
    {
        public Task<HttpResponseMessage> RegisterBot(string label, string behaviorKey, string token) =>
            client.PostAsJsonAsync("/admin/bots", new { label, behaviorKey, token });

        /// <summary>Registers a bot and returns it, failing the test if the platform refused.</summary>
        public async Task<BotResponse> RegisterBotOk(string label, string behaviorKey, string token)
        {
            var response = await client.RegisterBot(label, behaviorKey, token);
            await AssertStatus(response, HttpStatusCode.Created);

            return await response.Read<BotResponse>();
        }

        public Task<HttpResponseMessage> GetBot(long botId) => client.GetAsync($"/admin/bots/{botId}");

        public async Task<IReadOnlyList<BotResponse>> ListBots()
        {
            var response = await client.GetAsync("/admin/bots");
            await AssertStatus(response, HttpStatusCode.OK);

            return await response.Read<IReadOnlyList<BotResponse>>();
        }

        /// <summary>The bot's current server-side state — the status a health or lifecycle change must show up in.</summary>
        public async Task<BotResponse> GetBotOk(long botId)
        {
            var response = await client.GetBot(botId);
            await AssertStatus(response, HttpStatusCode.OK);

            return await response.Read<BotResponse>();
        }

        public Task<HttpResponseMessage> DisableBot(long botId) =>
            client.PostAsync($"/admin/bots/{botId}/disable", content: null);

        public Task<HttpResponseMessage> EnableBot(long botId) =>
            client.PostAsync($"/admin/bots/{botId}/enable", content: null);

        public Task<HttpResponseMessage> RotateToken(long botId, string token) =>
            client.PutAsJsonAsync($"/admin/bots/{botId}/token", new { token });

        public Task<HttpResponseMessage> RemoveBot(long botId) => client.DeleteAsync($"/admin/bots/{botId}");

        // The "Ok" pair of each call above, for the tests that need the change to have happened rather
        // than to inspect how it was reported — arranging a disabled bot, say. Keeping the two apart is
        // what lets a test's assertions all live in one block: setup that must not fail says so here.

        public Task DisableBotOk(long botId) => AssertStatus(client.DisableBot(botId), HttpStatusCode.OK);

        public Task EnableBotOk(long botId) => AssertStatus(client.EnableBot(botId), HttpStatusCode.OK);

        public Task RotateTokenOk(long botId, string token) =>
            AssertStatus(client.RotateToken(botId, token), HttpStatusCode.OK);

        public Task RemoveBotOk(long botId) => AssertStatus(client.RemoveBot(botId), HttpStatusCode.NoContent);

        public Task RemoveBehaviorOk(string packageName) =>
            AssertStatus(client.RemoveBehavior(packageName), HttpStatusCode.NoContent);

        public async Task<BehaviorsResponse> ListBehaviors()
        {
            var response = await client.GetAsync("/admin/behaviors");
            await AssertStatus(response, HttpStatusCode.OK);

            return await response.Read<BehaviorsResponse>();
        }

        public Task<HttpResponseMessage> UploadBehavior(string fileName, byte[] package) =>
            client.PostAsync("/admin/behaviors", Multipart(fileName, package));

        /// <summary>Uploads a package and returns what it contributed, failing the test if it was refused.</summary>
        public async Task<ExtensionResponse> UploadBehaviorOk(string fileName, byte[] package)
        {
            var response = await client.UploadBehavior(fileName, package);
            await AssertStatus(response, HttpStatusCode.Created);

            return await response.Read<ExtensionResponse>();
        }

        public Task<HttpResponseMessage> ReplaceBehavior(string packageName, byte[] package) =>
            client.PutAsync($"/admin/behaviors/{packageName}", Multipart(packageName, package));

        public Task<HttpResponseMessage> RemoveBehavior(string packageName) =>
            client.DeleteAsync($"/admin/behaviors/{packageName}");

        /// <summary>
        /// Delivers one text message the way Telegram does: a POST of the raw webhook payload, carrying the
        /// bot's secret-token header. The body is Telegram's own snake_case wire shape rather than a
        /// re-serialised object, so the endpoint's model binding is exercised against what really arrives.
        /// </summary>
        public Task<HttpResponseMessage> PostWebhook(string path, string? secret, string text, long chatId = 4242) =>
            client.PostWebhookRaw(path, secret, TelegramPayload.TextMessage(text, chatId));

        public Task<HttpResponseMessage> PostWebhookRaw(string path, string? secret, string updateJson)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(updateJson, Encoding.UTF8, "application/json")
            };

            if (secret is not null)
            {
                request.Headers.Add("X-Telegram-Bot-Api-Secret-Token", secret);
            }

            return client.SendAsync(request);
        }
    }

    extension(HttpResponseMessage response)
    {
        public async Task<T> Read<T>()
        {
            var body = await response.Content.ReadFromJsonAsync<T>();
            Assert.NotNull(body);

            return body;
        }

        /// <summary>The <c>error</c> a refusal carries. Never contains a bot token — that is asserted where it matters.</summary>
        public Task<ApiError> ReadError() => response.Read<ApiError>();
    }

    private static async Task AssertStatus(Task<HttpResponseMessage> request, HttpStatusCode expected) =>
        await AssertStatus(await request, expected);

    /// <summary>Asserts the status, quoting the body when it does not match so a failure explains itself.</summary>
    public static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode == expected)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail(
            $"Expected {(int)expected} {expected} from {response.RequestMessage?.Method} "
            + $"{response.RequestMessage?.RequestUri?.PathAndQuery}, got {(int)response.StatusCode} "
            + $"{response.StatusCode}. Body: {body}");
    }

    /// <summary>
    /// The multipart body the admin API expects. The file name is sent exactly as given — including a name
    /// carrying a path, which is what a real client's <c>filename=</c> may contain and what the platform is
    /// required to reduce to a plain name before it becomes a file path.
    /// </summary>
    private static MultipartFormDataContent Multipart(string fileName, byte[] package)
    {
        var file = new ByteArrayContent(package);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var content = new MultipartFormDataContent();
        content.Add(file, "package", fileName);

        return content;
    }
}