# TelegramBotPlatform

A **.NET 10 platform that hosts many Telegram bots at once**, and lets an operator **add, remove, and
re-credential bots — and even add entirely new bot *behaviors* — while the platform is running**. No restart,
no redeploy, and no disruption to the bots already serving users.

Each hosted bot is assigned a **behavior** that determines what it does. Behaviors are either **built-in**
(compiled into the host) or **operator-supplied extensions** (compiled plugin assemblies uploaded at runtime).
The platform ships with a trivial built-in **`echo`** behavior and a sample **`reverse`** plugin so you can see
both paths end to end.

> This is the reusable multi-bot hosting kernel only — it deliberately contains no product-specific bot. Add
> your own behaviors (built-in or as plugins) to build a real bot on top of it.

## What it does

- **Host many bots from one deployment** — each identified by its own BotFather credential, each replying from
  its own client.
- **Runtime fleet management** over an authenticated admin API — register, list, disable/enable, rotate
  credential, and remove bots without touching config or restarting.
- **Runtime behavior extensions** — upload a compiled plugin (`IBotBehavior`) and assign it to bots without a
  redeploy. A faulty extension is contained, never crashing the platform or other bots.
- **Per-bot isolation** — every update is tagged with its owning bot and routed to that bot's behavior; a
  failure in one bot never stalls the others.
- **Durable & secure** — the bot registry survives restarts (bots auto-restore on startup); bot tokens are
  **encrypted at rest** (ASP.NET Core Data Protection) and never logged.

## Architecture

A modular monolith: an ASP.NET Core host (`WebApi`) composes the layers over an **in-memory MassTransit bus**.

```
Telegram → PollingBotReceiver | WebhookBotReceiver     // one receiver per bot (Infrastructure)
        → publish BotUpdate(botId, update)             // every message is tagged with its owning bot
        → BotUpdateRouter (IConsumer<BotUpdate>)       // resolves the bot's behavior via IBehaviorCatalog
        → IBotBehavior.HandleUpdateAsync(context)      // the built-in echo behavior, or a loaded extension
                                                        // context.Client is THIS bot's Telegram client
```

A MassTransit consume filter (`BotScopeFilter<T>`, applied to every `IBotScopedMessage`) sets the current bot in
a scoped `IBotContext` **before** the consumer is constructed, so a constructor-injected `ITelegramBotClient`
resolves to *that* bot's client automatically.

`BotSupervisor` starts/stops each bot's receiver — **long-polling** in `Development`, a per-bot **webhook**
(`/telegram-bot/webhook/{botId}`, secret token deterministically derived, no extra storage) otherwise.
`BotHealthTracker` flips a repeatedly-failing bot's status to `Failing` (kept running at normal cadence — the
operator decides whether to disable/rotate/remove) and clears it back to `Active` on the next success.

### Projects

| Project | Responsibility |
|---------|----------------|
| [`TelegramBotPlatform.Public`](src/TelegramBotPlatform.Public) | The plugin SDK & shared contracts (`IBotBehavior`, `IBotUpdateContext`, `IBotRegistry`, `BotUpdate`, …). **Plugin authors reference only this.** |
| [`TelegramBotPlatform.Application`](src/TelegramBotPlatform.Application) | Logic: `BehaviorCatalog`, `BotRegistrationService`, `BotUpdateRouter`, `BotHealthTracker`. |
| [`TelegramBotPlatform.Infrastructure`](src/TelegramBotPlatform.Infrastructure) | Receivers, `BotSupervisor`, per-bot clients, admin API, security (admin-key auth, token encryption, Telegram validation), plugin loader/store, MassTransit bot-scope filter, DI (`AddPlatformModule`). |
| [`TelegramBotPlatform.Persistence`](src/TelegramBotPlatform.Persistence) | The `platform` Postgres schema: `PlatformDbContext`, the encrypted bot registry, migrations. |
| [`TelegramBotPlatform.WebApi`](src/TelegramBotPlatform.WebApi) | The host: composes everything, wires MassTransit, registers the built-in `echo` behavior + reloads saved extensions, maps the admin API and per-bot webhook, applies migrations on `migrate`. |
| [`samples/ReverseBehavior`](samples/ReverseBehavior) | A sample behavior extension (`reverse`) for trying the runtime plugin-upload flow. |

## Running locally

Requires the **.NET 10 SDK** and Docker (for local Postgres).

1. **Start Postgres.** Put a password in a `.env` file at the repo root (see [.env.example](.env.example)), then:
   ```bash
   docker compose up -d
   ```
2. **Set dev secrets** (never commit them) — an admin API key and the matching connection string:
   ```bash
   dotnet user-secrets set "Platform:AdminApiKey" "dev-admin-key" --project src/TelegramBotPlatform.WebApi
   dotnet user-secrets set "Persistence:ConnectionString" "Host=localhost;Port=5432;Database=telegrambotplatform;Username=telegrambotplatform;Password=<your .env password>" --project src/TelegramBotPlatform.WebApi
   ```
3. **Apply migrations:**
   ```bash
   dotnet run --project src/TelegramBotPlatform.WebApi -- migrate
   ```
4. **Run the host** (long-polls each bot in `Development`):
   ```bash
   dotnet run --project src/TelegramBotPlatform.WebApi
   ```
5. **Register a bot** with the built-in `echo` behavior (get a token from [@BotFather](https://t.me/BotFather)):
   ```bash
   curl -X POST http://localhost:8080/admin/bots \
     -H "Authorization: Bearer dev-admin-key" -H "Content-Type: application/json" \
     -d '{"label":"My Echo Bot","behaviorKey":"echo","token":"123456:your-botfather-token"}'
   ```
   Message the bot on Telegram — it echoes your text back. See
   [TelegramBotPlatform.WebApi.http](src/TelegramBotPlatform.WebApi/TelegramBotPlatform.WebApi.http) for every
   admin call.

## Admin API

All `/admin/*` requests require the configured admin key via `Authorization: Bearer <key>` or `X-Admin-Api-Key: <key>`.

| Method & path | Description |
|---------------|-------------|
| `POST /admin/bots` | Register a bot `{ label, behaviorKey, token }`. Validates the token with Telegram and the behavior against the catalog before persisting. |
| `GET /admin/bots` | List all bots with status. |
| `GET /admin/bots/{id}` | Get one bot. |
| `POST /admin/bots/{id}/disable` | Stop serving a bot (keeps its data). |
| `POST /admin/bots/{id}/enable` | Resume a disabled bot. |
| `PUT /admin/bots/{id}/token` | Rotate a bot's credential `{ token }` (must be the same Telegram bot). |
| `DELETE /admin/bots/{id}` | Remove a bot. |
| `GET /admin/behaviors` | List available behaviors **and** every stored extension package with its load status — `{ behaviors: [...], packages: [...] }`. |
| `POST /admin/behaviors` | Upload a **new** behavior-extension assembly (multipart field `package`). A name already in the store is a `409`. |
| `PUT /admin/behaviors/{packageName}` | Replace a stored extension with a new build (multipart field `package`); its behaviors hot-swap for bots already running. |
| `DELETE /admin/behaviors/{packageName}` | Remove a stored extension. Refused with `409` while a registered bot is still assigned to one of its behaviors. |

`GET /health` is an unauthenticated liveness probe.

Extension packages are stored **durably** — in the local plugins directory by default, or in an S3 bucket
when `Platform:PluginsBucket` is set — so an uploaded behavior survives a restart or a redeploy. A package
that fails to load does not block startup; it shows up in `packages` with a reason, and can be repaired
with `PUT` or retired with `DELETE` using nothing but the admin API.

## Writing a behavior extension

A behavior is a class implementing `IBotBehavior` from `TelegramBotPlatform.Public` — reference **only** that
assembly. See [samples/ReverseBehavior](samples/ReverseBehavior):

```csharp
public sealed class ReverseBotBehavior : IBotBehavior
{
    public string Key => "reverse";
    public string DisplayName => "Reverse";
    public string ContractVersion => BehaviorContractVersion.Current;

    public async Task HandleUpdateAsync(IBotUpdateContext context, CancellationToken cancellationToken)
    {
        var message = context.Update.Message;
        if (message?.Text is not { } text) return;
        await context.Client.SendMessage(message.Chat.Id, new string(text.Reverse().ToArray()), cancellationToken: cancellationToken);
    }
}
```

Build it, then upload and use it on a **running** platform (no redeploy):
```bash
dotnet build samples/ReverseBehavior -c Release
curl -X POST http://localhost:8080/admin/behaviors \
  -H "Authorization: Bearer dev-admin-key" \
  -F "package=@samples/ReverseBehavior/bin/Release/net10.0/ReverseBehavior.dll"
# now register a bot with "behaviorKey":"reverse"
```

Extensions are operator-supplied **trusted** code: the platform loads each into its own collectible
`AssemblyLoadContext` and *contains* a faulty one, but does not sandbox it. Vetting an extension is the
operator's job.

## Configuration

Binds from `appsettings.json` + environment files + user secrets (dev) + environment variables (prod,
double-underscore form, e.g. `Platform__AdminApiKey`).

| Section | Keys |
|---------|------|
| `Platform` | `AdminApiKey` (**required** — authenticates `/admin/*`), `PluginsDirectory` (default `plugins` — the extension store locally, the staging directory when a bucket is set), `PluginsBucket` (optional; setting it stores extensions in S3 instead), `PluginsPrefix` (default `behaviors/`), `MaxExtensionPackageBytes` (default 25 MB — the upload endpoints raise the server's request-body limit to match, so this alone is the ceiling), `ExtensionStoreStartupTimeout` (default `00:00:30` — a single budget shared by the startup listing and every package read, after which startup aborts), `WebhookBaseUrl` (**required outside `Development`**; each bot's webhook is `{WebhookBaseUrl}/{botId}`) |
| `Persistence` | `ConnectionString` (Postgres; keep the password in user secrets / env, never committed) |

## Build & test

| Task | Command |
|------|---------|
| Build (Debug) | `dotnet build TelegramBotPlatform.slnx` |
| Build (Release, as CI) | `dotnet build TelegramBotPlatform.slnx -c Release` |
| Run all tests | `dotnet test --solution TelegramBotPlatform.slnx` |
| Run only the unit tests | `dotnet test --project src/TelegramBotPlatform.UnitTests/TelegramBotPlatform.UnitTests.csproj` |
| Run only the integration tests | `dotnet test --project src/TelegramBotPlatform.IntegrationTests/TelegramBotPlatform.IntegrationTests.csproj` |
| Format | `dotnet format TelegramBotPlatform.slnx` |
| Add a migration | `dotnet ef migrations add <Name> --project src/TelegramBotPlatform.Persistence --startup-project src/TelegramBotPlatform.Persistence --context PlatformDbContext` |

The build uses **`TreatWarningsAsErrors`** and **Central Package Management** (versions in
[Directory.Packages.props](Directory.Packages.props)). Everything is xUnit v3 on Microsoft.Testing.Platform,
with hand-written fakes rather than a mocking library, and needs no Docker, cloud credentials or network:

- **Unit tests** are pure — one component at a time, against fakes (plus the EF Core in-memory provider for
  the registry).
- **Integration tests** boot the real host in-process (`WebApplicationFactory<Program>`) and drive it over
  HTTP: registering bots, delivering Telegram webhooks end to end, and uploading a real behavior extension
  onto a running platform. Only three things are substituted, all at the edges — Telegram's API, the token
  check, and Postgres.

## Deployment

A production [Dockerfile](src/TelegramBotPlatform.WebApi/Dockerfile) and a
[docker-compose.prod.yml](docker-compose.prod.yml) (Postgres + a one-off `migrate` runner + the app) are
included. `depends_on` ordering guarantees Postgres → migrate → app. Provide the secret env files it references
(`db.env`, `conn.env`, `app.env`) and set `Platform__WebhookBaseUrl` so each bot's webhook can be registered.
