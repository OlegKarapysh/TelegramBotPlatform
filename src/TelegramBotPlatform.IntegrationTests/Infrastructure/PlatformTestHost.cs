using System.Globalization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Telegram.Bot.Requests;
using TelegramBotPlatform.Persistence;
using TelegramBotPlatform.Public;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// One running instance of the platform, booted from the host's real entry point
/// (<c>src/TelegramBotPlatform.WebApi/Program.cs</c>) rather than a re-composition of it — so the
/// composition root, the startup ordering, the MassTransit bus, the endpoint routing and the DI graph
/// under test are the ones that ship.
/// <para>
/// Three things are substituted, all of them at the edges: Telegram (see
/// <see cref="RecordingTelegramBotClient"/> and <see cref="ScriptedTokenValidator"/>) and Postgres, which
/// becomes an in-memory database. Everything in between — admin API, auth filter, supervisor, catalog,
/// behavior-extension service, the collectible-<c>AssemblyLoadContext</c> loader, the filesystem extension
/// store, Data Protection and the EF registry — is production code.
/// </para>
/// </summary>
public sealed class PlatformTestHost : WebApplicationFactory<Program>
{
    private readonly PlatformTestSettings _settings;
    private readonly bool _ownsPluginsDirectory;

    private HttpClient? _admin;
    private HttpClient? _anonymous;

    private PlatformTestHost(PlatformTestSettings settings)
    {
        _settings = settings;
        _ownsPluginsDirectory = settings.PluginsDirectory is null;

        PluginsDirectory = settings.PluginsDirectory ?? CreateTemporaryPluginsDirectory();
        Database = settings.Database ?? new PlatformDatabase();
    }

    /// <summary>The clients the supervisor created — one per running bot, each recording its own calls.</summary>
    public RecordingBotClientRegistry Clients { get; } = new();

    /// <summary>Stands in for Telegram's <c>getMe</c> token check. Add to <c>Rejected</c> to make a token fail.</summary>
    public ScriptedTokenValidator Tokens { get; } = new();

    /// <summary>The extension store's root on disk. Tests inspect it to confirm what was really persisted.</summary>
    public string PluginsDirectory { get; }

    public PlatformDatabase Database { get; }

    public string AdminApiKey => _settings.AdminApiKey;

    /// <summary>An HTTP client that already carries a valid admin key.</summary>
    public HttpClient Admin => _admin ??= CreateAdminClient(AdminApiKey);

    /// <summary>An HTTP client with no credentials — the Telegram-facing and unauthenticated surface.</summary>
    public HttpClient Anonymous => _anonymous ??= CreateClient();

    /// <summary>
    /// Boots the platform. Startup work the host does before it serves — registering built-in behaviors and
    /// restoring stored extensions — happens here, so a host that refuses to start throws from this call.
    /// </summary>
    public static PlatformTestHost Start(PlatformTestSettings? settings = null)
    {
        var host = new PlatformTestHost(settings ?? new PlatformTestSettings());

        try
        {
            _ = host.Services;
        }
        catch
        {
            host.Dispose();
            throw;
        }

        return host;
    }

    public HttpClient CreateAdminClient(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Admin-Api-Key", apiKey);

        return client;
    }

    /// <summary>
    /// The local path Telegram was told to call for this bot, read back off the <c>setWebhook</c> the
    /// supervisor actually made. Posting to it proves the URL the platform published is one this app
    /// really serves — the two halves are derived independently, and a test that built the path itself
    /// would not notice them drifting apart.
    /// </summary>
    public string RegisteredWebhookPath(long botId) =>
        new Uri(Clients.Client(botId).LastRequest<SetWebhookRequest>().Url).AbsolutePath;

    /// <summary>The secret the supervisor registered for this bot — likewise read back, never re-derived.</summary>
    public string RegisteredWebhookSecret(long botId) =>
        Clients.Client(botId).LastRequest<SetWebhookRequest>().SecretToken!;

    /// <summary>Runs <paramref name="use"/> against a fresh DI scope, for asserting on state the API does not expose.</summary>
    public async Task InScope(Func<IServiceProvider, Task> use)
    {
        await using var scope = Services.CreateAsyncScope();
        await use(scope.ServiceProvider);
    }

    /// <summary>Registers a bot through the admin API and returns it with its webhook already resolved.</summary>
    public async Task<HostedBot> RegisterBot(string label, string behaviorKey, string token) =>
        new(this, await Admin.RegisterBotOk(label, behaviorKey, token));

    /// <summary>A bot this host already knows about — how a restarted host picks up a bot it restored.</summary>
    public async Task<HostedBot> Bot(long botId) => new(this, await Admin.GetBotOk(botId));

    /// <summary>
    /// Adds a behavior to the running catalog — the same call the host makes for its own built-ins.
    /// <para>
    /// Used for behaviors that exist only to be driven by a test, such as one that fails on demand. No
    /// operator would ship that as an extension, but it is exactly the input the platform's fault
    /// containment and health tracking are built for, and there is no other way to produce it.
    /// </para>
    /// </summary>
    public T RegisterBehavior<T>(T behavior) where T : IBotBehavior
    {
        var registered = Services.GetRequiredService<IBehaviorCatalog>().Register(behavior, "test");
        Assert.True(registered.IsSuccess, $"Could not register the test behavior \"{behavior.Key}\".");

        return behavior;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Anything but Development: outside it the supervisor drives bots by webhook, which is the mode a
        // deployment runs and the only one that can be exercised deterministically (Development would
        // instead spawn an open-ended long-polling loop per bot).
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Platform:AdminApiKey"] = _settings.AdminApiKey,
                ["Platform:WebhookBaseUrl"] = _settings.WebhookBaseUrl,
                ["Platform:PluginsDirectory"] = PluginsDirectory,
                ["Platform:MaxExtensionPackageBytes"] =
                    _settings.MaxExtensionPackageBytes.ToString(CultureInfo.InvariantCulture),
                ["Platform:ExtensionStoreStartupTimeout"] =
                    _settings.ExtensionStoreStartupTimeout.ToString("c", CultureInfo.InvariantCulture),
                // Unused — the context below is re-registered on the in-memory provider. Present only
                // because PersistenceOptions is validated on start, and a host that skipped that check
                // would not be the host that ships.
                ["Persistence:ConnectionString"] = "Host=integration-tests;Database=unused",
                ["Logging:LogLevel:Default"] = "Warning"
            }));

        builder.ConfigureTestServices(services =>
        {
            UseInMemoryDatabase(services);

            // The two seams that would otherwise reach api.telegram.org, replaced with the recording
            // doubles the tests assert against.
            services.RemoveAll<IBotTokenValidator>();
            services.AddSingleton<IBotTokenValidator>(Tokens);
            services.RemoveAll<IBotClientRegistry>();
            services.AddSingleton<IBotClientRegistry>(Clients);

            if (_settings.ExtensionStore is { } store)
            {
                services.RemoveAll<IExtensionStore>();
                services.AddSingleton(store);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && _ownsPluginsDirectory)
        {
            DeletePluginsDirectory();
        }
    }

    /// <summary>
    /// Swaps Npgsql for the in-memory provider, keeping the real <c>PlatformDbContext</c>,
    /// <c>PostgresBotRegistry</c> and Data Protection key ring on top of it.
    /// </summary>
    private void UseInMemoryDatabase(IServiceCollection services)
    {
        services.RemoveAll<DbContextOptions>();
        services.RemoveAll<DbContextOptions<PlatformDbContext>>();
        services.RemoveAll<IDbContextOptionsConfiguration<PlatformDbContext>>();
        services.RemoveAll<PlatformDbContext>();

        services.AddDbContext<PlatformDbContext>(options => options
            .UseInMemoryDatabase(Database.Name, Database.Root)
            // The in-memory provider has no transactions; Data Protection's EF key store opens one when it
            // writes a new key. Ignoring the warning keeps that a no-op instead of an exception.
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
    }

    private static string CreateTemporaryPluginsDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tbp-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return path;
    }

    private void DeletePluginsDirectory()
    {
        try
        {
            if (Directory.Exists(PluginsDirectory))
            {
                Directory.Delete(PluginsDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A staged plugin copy can briefly keep a handle open on Windows. Leftover temp files are the
            // operating system's problem, never a reason to fail a test that has already made its point.
        }
    }
}