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
| Run one project's tests | `dotnet test --project src/TelegramBotPlatform.UnitTests/TelegramBotPlatform.UnitTests.csproj` |
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
Application free of an Infrastructure reference). `BotHealthTracker` marks a repeatedly-failing bot `Failing` while
keeping it running at normal cadence (no backoff, no auto-disable), and clears it on the next success.

### Layers (strict dependency direction)

- **`*.Public`** — interfaces & shared contracts only, no logic. This is the **plugin SDK**; extensions reference
  only this assembly. Its assembly name (`TelegramBotPlatform.Public`) is shared with plugins by
  `ExtensionAssemblyLoader` so `IBotBehavior` types unify — **don't rename it** without updating
  `PluginLoadContext.SharedAssemblyNames`.
- **`*.Application`** — logic (`BehaviorCatalog`, `BotRegistrationService`, `BotUpdateRouter`, `BotHealthTracker`).
- **`*.Infrastructure`** — external concerns + DI (`AddPlatformModule`): receivers, `BotSupervisor`, per-bot
  clients, admin API, security, plugin loader/store, the bot-scope filter, `PlatformOptions`.
- **`*.Persistence`** — the `platform` Postgres schema: `PlatformDbContext` (also holds the Data Protection key
  ring), `PostgresBotRegistry`, migrations. Wired via `AddPlatformPersistence()`, called by `AddPlatformModule()`.
- **`WebApi`** — the host. Composes everything, registers built-in behaviors + reloads saved extensions, maps the
  admin API + per-bot webhook, applies migrations on `migrate`.

### Data & security

- The bot registry (`platform.Bots`) is **durable**; `BotRestoreHostedService` restarts every non-disabled bot on
  startup. Schema changes go through per-context EF migrations against `PlatformDbContext` (see the command table).
- **Bot tokens are encrypted at rest** via `ITokenProtector` (Data Protection, key ring in
  `platform.DataProtectionKeys`) — never stored in plaintext, never logged. `TelegramBotTokenValidator` logs only
  an exception's type/message, never the full exception, since the token is in the request URI.
- The admin API is authenticated by a static `Platform:AdminApiKey` (constant-time compared) on every request, and
  is separate from the end-user Telegram surface. Webhook secret tokens are HMAC-derived from the admin key per bot.

## Conventions (match these — the codebase is consistent)

- **Result, not exceptions, for expected failures.** Public methods that can fail return `FluentResults.Result<T>`;
  check `.IsFailed` / `.Errors.First().Message`. Reserve `throw` for programmer errors / invariants.
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

## Tests

Pure xUnit v3 on **Microsoft.Testing.Platform** (MTP) — test projects are executables (`<OutputType>Exe</OutputType>`)
referencing `xunit.v3.mtp-v2`; MTP mode is set in [global.json](global.json). Because of MTP mode, pass the target
explicitly: `dotnet test --solution TelegramBotPlatform.slnx` or `--project <test>.csproj`.

Keep tests **pure** — no network, Telegram, filesystem, or real database. Replace collaborators with small
hand-written fakes implementing the interfaces (no mocking library). The one sanctioned exception:
`PostgresBotRegistryTests` uses the **EF Core in-memory provider** via `InMemoryDbContextFactory`.

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
