namespace TelegramBotPlatform.IntegrationTests;

/// <summary>
/// The admin API is the whole platform's control plane: anyone who reaches it can add a bot, take one
/// down, or upload code the host will load and run. These tests check the key is required on <em>every</em>
/// route — a filter attached to the group is easy to lose when a route is added — and that a rejected
/// request never reaches its handler.
/// </summary>
public class AdminApiSecurityTests(AdminApiSecurityTests.Platform platform)
    : IClassFixture<AdminApiSecurityTests.Platform>
{
    /// <summary>
    /// Booted once for the class. Safe to share here precisely because of what these tests assert: every
    /// request either is rejected before it changes anything, or only reads.
    /// </summary>
    public sealed class Platform : IAsyncLifetime
    {
        public PlatformTestHost Host { get; private set; } = null!;

        public ValueTask InitializeAsync()
        {
            Host = PlatformTestHost.Start();

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }

    /// <summary>Every route under <c>/admin</c>, so a newly added one that forgets the filter fails here.</summary>
    public static TheoryData<string, string> AdminRoutes =>
        new()
        {
            { "GET", "/admin/bots" },
            { "POST", "/admin/bots" },
            { "GET", "/admin/bots/1" },
            { "POST", "/admin/bots/1/disable" },
            { "POST", "/admin/bots/1/enable" },
            { "PUT", "/admin/bots/1/token" },
            { "DELETE", "/admin/bots/1" },
            { "GET", "/admin/behaviors" },
            { "POST", "/admin/behaviors" },
            { "PUT", "/admin/behaviors/Reverse.dll" },
            { "DELETE", "/admin/behaviors/Reverse.dll" }
        };

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task EveryAdminRoute_RejectsARequest_WithNoKey(string method, string route)
    {
        var response = await Send(platform.Host.Anonymous, method, route, apiKey: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(AdminRoutes))]
    public async Task EveryAdminRoute_RejectsARequest_WithTheWrongKey(string method, string route)
    {
        var response = await Send(platform.Host.Anonymous, method, route, apiKey: "not-the-admin-key");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AKeyThatIsOnlyAPrefixOfTheRealOne_IsRejected()
    {
        var response = await Send(platform.Host.Anonymous, "GET", "/admin/bots", platform.Host.AdminApiKey[..8]);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AKeyWithTrailingPadding_IsRejected()
    {
        var response = await Send(platform.Host.Anonymous, "GET", "/admin/bots", platform.Host.AdminApiKey + "x");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheKeyIsAccepted_AsABearerToken()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/bots");
        request.Headers.Add("Authorization", $"Bearer {platform.Host.AdminApiKey}");

        var response = await platform.Host.Anonymous.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheKeyIsAccepted_AsADedicatedHeader()
    {
        var response = await Send(platform.Host.Anonymous, "GET", "/admin/bots", platform.Host.AdminApiKey);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_NeverReachesTheHandler()
    {
        // Not just the status code: the filter has to run before the endpoint, or an attacker's 401 would
        // still have registered a bot.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/bots")
        {
            Content = JsonContent.Create(new { label = "Smuggled", behaviorKey = "echo", token = "999:token" })
        };

        var response = await platform.Host.Anonymous.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(await platform.Host.Admin.ListBots());
        Assert.Empty(platform.Host.Clients.LiveBotIds);
    }

    [Fact]
    public async Task TheTelegramFacingSurface_IsNotBehindTheAdminKey()
    {
        // The webhook is authenticated by its own per-bot secret, not the admin key — it has to stay
        // reachable without one, or Telegram could never deliver an update.
        var response = await platform.Host.Anonymous.PostWebhook("/telegram-bot/webhook/1", secret: null, "hello");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string route, string? apiKey)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);

        if (apiKey is not null)
        {
            request.Headers.Add("X-Admin-Api-Key", apiKey);
        }

        request.Content = Body(method, route);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A body each route can actually bind.
    /// <para>
    /// This matters more than it looks: a minimal API binds parameters <em>before</em> its endpoint
    /// filters run, so a request the endpoint cannot bind is answered 400/415 without the auth filter ever
    /// being consulted. Sending the wrong shape would make these tests pass on a route that had lost its
    /// filter entirely.
    /// </para>
    /// </summary>
    private static HttpContent? Body(string method, string route)
    {
        if (method is not ("POST" or "PUT"))
        {
            return null;
        }

        if (!route.StartsWith("/admin/behaviors", StringComparison.Ordinal))
        {
            return JsonContent.Create(new { label = "x", behaviorKey = "echo", token = "1:x" });
        }

        var package = new ByteArrayContent([0x4d, 0x5a]);
        var multipart = new MultipartFormDataContent();
        multipart.Add(package, "package", "Reverse.dll");

        return multipart;
    }
}