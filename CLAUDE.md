# CLAUDE.md

Guidance for Claude Code (and other AI agents) working in this repository.
Keep this file accurate — when you change a convention, build command, or layer boundary, update it here.

## What TelegramBotPlatform is

A **.NET 10 platform that hosts multiple Telegram bots**, added and managed at runtime through an operator-only
admin API — no restart or redeploy. Each hosted bot is assigned a **behavior** (`IBotBehavior`) that determines
what it does. Behaviors are **built-in** (compiled into the host and registered at startup) or **operator-supplied
extensions** (compiled plugin assemblies uploaded at runtime and loaded into a collectible `AssemblyLoadContext`).

This repo is the **reusable multi-bot hosting kernel only** — it contains no product-specific bot. The built-in
`echo` behavior ([WebApi/Behaviors/EchoBehavior.cs](src/TelegramBotPlatform.WebApi/Behaviors/EchoBehavior.cs)) and
the sample `reverse` plugin ([samples/ReverseBehavior](samples/ReverseBehavior)) exist to demonstrate the two
behavior paths; real bots are added the same way. See [README.md](README.md) for the product-facing overview and
admin API reference.

## Commands

All commands run from the repo root and require the **.NET 10 SDK**.

| Task | Command |
|------|---------|
| Restore | `dotnet restore TelegramBotPlatform.slnx` |
| Build (Debug) | `dotnet build TelegramBotPlatform.slnx` |
| Build (Release, as CI does) | `dotnet build TelegramBotPlatform.slnx -c Release` |
| Run all tests | `dotnet test --solution TelegramBotPlatform.slnx` |
| Run only the unit tests | `dotnet test --project src/TelegramBotPlatform.UnitTests/TelegramBotPlatform.UnitTests.csproj` |
| Run only the integration tests | `dotnet test --project src/TelegramBotPlatform.IntegrationTests/TelegramBotPlatform.IntegrationTests.csproj` |
| Format | `dotnet format TelegramBotPlatform.slnx` |
| Run the host | `dotnet run --project src/TelegramBotPlatform.WebApi` |
| Apply migrations | `dotnet run --project src/TelegramBotPlatform.WebApi -- migrate` |

> The build uses **`TreatWarningsAsErrors`** — a new warning fails the build. Fix it; don't suppress it unless
> it's a known false positive (see `NoWarn` in [Directory.Build.props](Directory.Build.props)).

Running locally needs `Platform:AdminApiKey` and `Persistence:ConnectionString` in user secrets and a Postgres
(`docker compose up -d`). In `Development` each bot long-polls; otherwise each registers its own webhook
(`/telegram-bot/webhook/{botId}`), which needs `Platform:WebhookBaseUrl`. Full walkthrough in the README.

## Architecture

The host (`WebApi`) composes the layers over an **in-memory MassTransit bus**. Every update is tagged with its
owning bot and routed to that bot's assigned behavior:

```
Telegram → PollingBotReceiver | WebhookBotReceiver     // one per bot; Infrastructure/Receivers
        → publish BotUpdate(botId, update)
        → BotUpdateRouter (IConsumer<BotUpdate>)        // resolves the bot's behavior via IBehaviorCatalog
        → IBotBehavior.HandleUpdateAsync(context)       // built-in echo, or a loaded extension
                                                         // context.Client == this bot's Telegram client
```

A MassTransit consume filter (`BotScopeFilter<T>`, applied to every `IBotScopedMessage`) sets the current bot in a
scoped `IBotContext` **before** the consumer is constructed, so a constructor-injected `ITelegramBotClient` resolves
to *that* bot's client. `BotSupervisor` owns each bot's receiver lifecycle (start/stop/rotate/remove) and is the
only thing `BotRegistrationService` touches to bring a bot up or down (via the `IBotLifecycle` seam, keeping
Application free of an Infrastructure reference). Starting a receiver is the one step that can fail *after* the
registry has been written — it is a live `setWebhook` — so `Register` and `Enable` undo their own registry write
when it does, and report a `Result` failure rather than letting the exception escape as a 500 with a
half-registered bot behind it. `RotateToken` deliberately does not roll back: the superseded token may already be
revoked, so the new one is kept and the refusal says the receiver still needs bringing up.

`BotHealthTracker` marks a repeatedly-failing bot `Failing` while keeping it running at normal cadence (no
backoff, no auto-disable), and clears it on the next success. It is **scoped** (it reads/writes the scoped
`IBotRegistry`) and every update is consumed in its own scope, so its consecutive-failure counts live in the
singleton `BotFailureCounter` — counting on the tracker itself makes `Failing` unreachable in the running host
while every unit test that reuses one tracker still passes. `Failing` is *durable* but the counts are not, which
is why `BotFailureCounter.RecordSuccess` also answers true on the **first** success it sees for a bot: without
that, a bot left `Failing` by a previous process stays flagged for as long as it keeps working, because there is
no in-memory streak to break. The same counter is cleared (`Forget`) on disable/remove, so a bot brought back
does not inherit the streak it went down with.

### Layers (strict dependency direction)

- **`*.Public`** — interfaces & shared contracts only, no logic. This is the **plugin SDK**; extensions reference
  only this assembly. Its assembly name (`TelegramBotPlatform.Public`) is shared with plugins by
  `ExtensionAssemblyLoader` so `IBotBehavior` types unify — **don't rename it** without updating
  `PluginLoadContext.SharedAssemblyNames`.
- **`*.Application`** — logic (`BehaviorCatalog`, `BehaviorExtensionService`, `BotRegistrationService`,
  `BotUpdateRouter`, `BotHealthTracker` + `BotFailureCounter`).
- **`*.Infrastructure`** — external concerns + DI (`AddPlatformModule`): receivers, `BotSupervisor`, per-bot
  clients, admin API, security, the extension loader and the two `IExtensionStore` implementations, the
  bot-scope filter, `PlatformOptions`.
- **`*.Persistence`** — the `platform` Postgres schema: `PlatformDbContext` (also holds the Data Protection key
  ring), `PostgresBotRegistry`, migrations. Wired via `AddPlatformPersistence()`, called by `AddPlatformModule()`.
- **`WebApi`** — the host. Composes everything, registers built-in behaviors + reloads saved extensions, maps the
  admin API + per-bot webhook, applies migrations on `migrate`.

### Data & security

- The bot registry (`platform.Bots`) is **durable**; `BotRestoreHostedService` restarts every non-disabled bot on
  startup. Schema changes go through per-context EF migrations against `PlatformDbContext` (see the command table).
- **Behavior extension packages are durable too**, behind `IExtensionStore`: `FileSystemExtensionStore` over
  `Platform:PluginsDirectory` by default, `S3ExtensionStore` when `Platform:PluginsBucket` is set (ECS, where
  local disk is ephemeral). `BehaviorExtensionService` owns upload/replace/remove/restore; it restores every
  stored package before the host serves, and **aborts startup** if the store cannot be read within
  `Platform:ExtensionStoreStartupTimeout` rather than running with an incomplete catalog. That budget is one
  deadline shared by the listing and every package read, so the delay does not scale with package count. A
  single unloadable package is logged, skipped, and reported via `GET /admin/behaviors`.
- **Package names are validated in both directions**, by `ExtensionPackageName`. On the way in, `Validate`
  *normalises* (a client's `filename=` may carry a full path). It strips everything up to the last `/` or `\`
  **itself** rather than calling `Path.GetFileName`, which is OS-aware in more ways than the separator it
  recognises — on Windows it also strips a drive-relative prefix, so `C:evil.dll` came back as the valid
  `evil.dll` there and unchanged (then rejected on the `:`) on Linux. Keep the strip OS-blind; the same upload
  must not be accepted locally and refused in CI. On the way back out of a store, `ValidateStored` *refuses*
  anything it would have to rewrite: a tidied name would no longer address the object the store actually holds.
- **Bot tokens are encrypted at rest** via `ITokenProtector` (Data Protection, key ring in
  `platform.DataProtectionKeys`) — never stored in plaintext, never logged. Keeping the "never logged" half true
  takes two deliberate measures, because the token travels in the Telegram request **path**
  (`api.telegram.org/bot{token}/getMe`): `TelegramBotTokenValidator` logs only an exception's type/message,
  never the full exception; and `AddTelegramHttpClients()` calls `RemoveAllLoggers()` on both named clients,
  since `IHttpClientFactory`'s default handlers log the request URI at Information level. **Do not re-enable
  logging on those clients** — it publishes every bot's credential to the log sink on every call.
  `TelegramHttpClientLoggingTests` fails if either measure is dropped.
- The admin API is authenticated by a static `Platform:AdminApiKey` (constant-time compared) on every request, and
  is separate from the end-user Telegram surface. Webhook secret tokens are HMAC-derived from the admin key per bot.

## Conventions (match these — the codebase is consistent)

- **Result, not exceptions, for expected failures.** Public methods that can fail return `FluentResults.Result<T>`;
  check `.IsFailed` / `.Errors.First().Message`. Reserve `throw` for programmer errors / invariants.
  When a caller has to *act* on which failure it was, give the failure a type rather than a recognisable
  phrase — see `ExtensionErrors.cs` (`StoreUnavailableError`, `PackageNotFoundError`,
  `ExtensionConflictError`, `BehaviorInUseError`), which is how `AdminEndpoints.MapFailure` picks a status
  code and how startup decides an unreachable store is fatal. The bot endpoints still classify on message
  text; that is the older path, not the one to copy.
- **DI registration uses C# extension members** (not classic extension methods):
  ```csharp
  public static class ServiceInstaller
  {
      extension(IServiceCollection services)
      {
          public IServiceCollection AddXyz() { /* ... */ return services; }
      }
  }
  ```
- **Options pattern** for config: `services.AddOptions<TOptions>().BindConfiguration(TOptions.SectionName)
  .ValidateDataAnnotations().ValidateOnStart();`. Options are `record`s with a `const string SectionName`.
- **C# style:** file-scoped namespaces, `sealed` by default, primary constructors, `var`, expression-bodied members
  where they fit, Allman braces, private fields `_camelCase`. Common usings live in each project's `GlobalUsings.cs`.
  Nullable reference types are on solution-wide — keep code null-clean. See [.editorconfig](.editorconfig).
- **Central Package Management**: every NuGet version lives in [Directory.Packages.props](Directory.Packages.props);
  `.csproj` files reference packages **without** a `Version`.
- **No Async suffix** in async methods.

### Adding a built-in behavior
1. Implement `IBotBehavior` (in the host under `WebApi/Behaviors/`, or a new library the host references).
2. Register it in DI and add it to the catalog at startup in [Program.cs](src/TelegramBotPlatform.WebApi/Program.cs)
   (`behaviorCatalog.Register(theBehavior, "built-in")`), before the plugin-reload loop.
3. It's now assignable via `POST /admin/bots` with its `Key`.

### Adding a behavior extension (plugin)
Implement `IBotBehavior` in a class library that references only `TelegramBotPlatform.Public`, build it, and upload
the DLL via `POST /admin/behaviors`. See [samples/ReverseBehavior](samples/ReverseBehavior). A behavior that itself
needs the bus would add its consumers in the host's `AddPlatformMessaging`.

`POST` is create-only (a stored name returns 409). Ship a new build with `PUT /admin/behaviors/{packageName}` —
it hot-swaps the behaviors for running bots, and rolls back completely if the new build fails to load. Retire one
with `DELETE /admin/behaviors/{packageName}`, which is refused while a registered bot is still assigned to any of
its behaviors. `BehaviorCatalog`'s per-source operations are atomic (immutable snapshot behind a write lock), so a
multi-key package is never observed half-swapped.

## Tests

Two projects, both xUnit v3 on **Microsoft.Testing.Platform** (MTP) — test projects are executables
(`<OutputType>Exe</OutputType>`) referencing `xunit.v3.mtp-v2`; MTP mode is set in [global.json](global.json).
Because of MTP mode, pass the target explicitly: `dotnet test --solution TelegramBotPlatform.slnx` or
`--project <test>.csproj`. Never a mocking library — collaborators are small hand-written fakes.

### How a test is written

- **Arrange, act, assert — three blank-line-separated blocks, no `// Arrange` labels.** The names carry
  that; the blank lines carry the shape. **One act per test**: if a test needs a second act to make its
  point, it is two tests. And **assertions only in the last block** — a check in the arrange block is a
  setup guard, and belongs behind an `...Ok` helper instead (see below).
- **Setup that must not fail says so, without asserting**: `RegisterBotOk`, `GetBotOk`, `UploadBehaviorOk`,
  `DisableBotOk`, `EnableBotOk`, `RotateTokenOk`, `RemoveBotOk`, `RemoveBehaviorOk` on `AdminApi`, and
  `HostedBot.DeliverOk`. Each fails the test with the response body quoted if the call did not do what the
  arrange assumed. The bare `DisableBot`/`Deliver`/… variants are for when the *response itself* is what
  the test is about.
- **Comments earn their place or go.** Keep the class-level `<summary>` saying what the file is for, and
  keep a comment that records a real past bug ("Regression: …") or a why that is not on the screen. Delete
  anything that restates the assertion below it — the test name and the assertion are the description.
- Test classes are `sealed`, like everything else here.

### `TelegramBotPlatform.UnitTests` — one component at a time

Keep them **pure**: no network, Telegram, filesystem, or real database. The one sanctioned exception is
`PostgresBotRegistryTests`, which uses the **EF Core in-memory provider** via `InMemoryDbContextFactory`.

### `TelegramBotPlatform.IntegrationTests` — the composed host

Boots the **host's real entry point** through `WebApplicationFactory<Program>` (which is why
[Program.cs](src/TelegramBotPlatform.WebApi/Program.cs) ends with `public partial class Program;`) and drives it
over HTTP. Everything between the endpoints and the edges is production code — admin API, auth filter,
MassTransit bus, `BotScopeFilter`, supervisor, catalog, extension service, the collectible-`AssemblyLoadContext`
loader, `FileSystemExtensionStore`, Data Protection and the EF registry.

Exactly three things are substituted, all at the edges, all in `PlatformTestHost`:

| Substituted | By | Why |
|---|---|---|
| Each bot's `ITelegramBotClient` | `RecordingBotClientRegistry` → `RecordingTelegramBotClient` | Telegram is the one true external system; tests assert on the calls the platform *made* — and, via `Clients.FailEvery<TRequest>`, on what it does when Telegram *refuses* one |
| `IBotTokenValidator` | `ScriptedTokenValidator` (`<id>:<secret>` ⇒ bot `<id>`) | It is a live `getMe` call; the seam exists for exactly this |
| Npgsql | EF in-memory provider, per `PlatformDatabase` | No Docker in CI; the real `PostgresBotRegistry` and key ring sit on top |

Rules for adding to this project:

- **Prove the composition, not a component.** If a fake collaborator could demonstrate it, it belongs in the
  unit tests. The payoff assertion is usually a bot: an update goes in, the right reply comes out of the right
  client. `BotFailureCounter` exists because an integration test caught what six passing unit tests could not.
- **Never sleep; wait on the condition.** Handing an update to a behavior is the only asynchronous seam (the
  webhook is answered before the bus delivers). Use `Wait.Until` / `WaitForSentMessages`, which return as soon
  as the effect lands and report what they were waiting for when they do not.
- **Assert a negative only after something has provably flowed.** "Nothing was dispatched" is checked by
  sending a *valid* update afterwards, waiting for its reply, and finding only that one — never by sleeping.
- **One host per test** (`await using var platform = PlatformTestHost.Start()`), with its own database and its
  own temp plugins directory, both cleaned up on dispose. A class fixture is only for a class whose tests
  cannot observe each other (see `AdminApiSecurityTests`). Share a `PlatformDatabase` between two hosts to
  simulate a restart — see `PlatformRestartTests`.
- **Extension tests use the real sample assembly** (`samples/ReverseBehavior`, via `SamplePlugin`), because the
  extension path's correctness depends on reflection, a collectible load context and type identity unifying
  across it — none of which a stand-in loader exercises.
- Note that a minimal API **binds parameters before endpoint filters run**, so an auth test must send a body
  the route can bind or it will get a 400/415 without the filter ever being consulted.

## Gotchas

- Touching anything that affects `dotnet restore` at the repo root (the `Directory.*.props` files) also affects the
  Docker build — the Dockerfile copies the full source before `restore` for this reason.
- Behavior extensions are **trusted, operator-only** code (upload is gated by the admin key). The platform contains
  a *faulty* one (bad load, version mismatch, colliding key) without crashing, but does **not** sandbox it. Don't add
  sandboxing complexity; vetting an extension's source is the operator's job.
- `PluginLoadContext.SharedAssemblyNames` must list the Public assembly name and `Telegram.Bot` so plugin types
  unify with the host's — keep it in sync if you rename `TelegramBotPlatform.Public`.
- The observability seams (`Activity.Current?.SetTag("bot.id", …)`) are kept but no tracer is wired by default; add
  OpenTelemetry in the host if you need per-bot traces/metrics exported.
