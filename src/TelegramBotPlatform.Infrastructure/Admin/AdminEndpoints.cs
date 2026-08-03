namespace TelegramBotPlatform.Infrastructure.Admin;

/// <summary>Operator-only fleet management API — always off the end-user Telegram surface.</summary>
public static class AdminEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/admin").AddEndpointFilter<AdminApiKeyAuth>();

        group.MapPost("/bots", RegisterBot);
        group.MapGet("/bots", ListBots);
        group.MapGet("/bots/{id:long}", GetBot);
        group.MapPost("/bots/{id:long}/disable", DisableBot);
        group.MapPost("/bots/{id:long}/enable", EnableBot);
        group.MapPut("/bots/{id:long}/token", RotateToken);
        group.MapDelete("/bots/{id:long}", RemoveBot);
        group.MapGet("/behaviors", ListBehaviors);

        var uploadSizeLimit = ExtensionUploadSizeLimit.From(
            app.Services.GetRequiredService<IOptions<PlatformOptions>>().Value);

        // The multipart IFormFile parameter auto-attaches anti-forgery metadata, which would require the
        // UseAntiforgery middleware + a token. This is an operator-only, API-key-authenticated machine
        // endpoint (no cookies/browser), so anti-forgery adds nothing — opt out of it.
        group.MapPost("/behaviors", UploadBehaviorExtension).DisableAntiforgery().WithMetadata(uploadSizeLimit);
        group.MapPut("/behaviors/{packageName}", ReplaceBehaviorExtension).DisableAntiforgery().WithMetadata(uploadSizeLimit);
        group.MapDelete("/behaviors/{packageName}", RemoveBehaviorExtension);
    }

    /// <summary>
    /// Raises the transport-level body limit on the two upload endpoints to fit the configured package
    /// ceiling.
    /// <para>
    /// Without this the server's 30 MB default caps the request first, so raising
    /// <see cref="PlatformOptions.MaxExtensionPackageBytes"/> past it would silently not work — the upload
    /// would die on the wire with an opaque 413 before any of this file's friendlier handling ran. The
    /// headroom covers multipart framing (boundaries and part headers), which is counted against the
    /// request body but is not part of the package.
    /// </para>
    /// </summary>
    private sealed record ExtensionUploadSizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata
    {
        private const long MultipartFramingHeadroom = 1024 * 1024;

        public static ExtensionUploadSizeLimit From(PlatformOptions options) =>
            new(options.MaxExtensionPackageBytes + MultipartFramingHeadroom);
    }

    private static async Task<IResult> RegisterBot(RegisterBotRequest request, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var result = await service.Register(request.Label, request.BehaviorKey, request.Token, cancellationToken);
        return result.IsFailed
            ? MapFailure(result.Errors)
            : Results.Created($"/admin/bots/{result.Value.Id}", ToResponse(result.Value));
    }

    private static async Task<IResult> ListBots(BotRegistrationService service, CancellationToken cancellationToken)
    {
        var bots = await service.List(cancellationToken);
        return Results.Ok(bots.Select(ToResponse));
    }

    private static async Task<IResult> GetBot(long id, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var bot = await service.Get(id, cancellationToken);
        return bot is null ? Results.NotFound() : Results.Ok(ToResponse(bot));
    }

    private static async Task<IResult> DisableBot(long id, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var result = await service.Disable(id, cancellationToken);
        return result.IsFailed ? MapFailure(result.Errors) : Results.Ok();
    }

    private static async Task<IResult> EnableBot(long id, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var result = await service.Enable(id, cancellationToken);
        return result.IsFailed ? MapFailure(result.Errors) : Results.Ok();
    }

    private static async Task<IResult> RotateToken(long id, RotateTokenRequest request, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var result = await service.RotateToken(id, request.Token, cancellationToken);
        return result.IsFailed ? MapFailure(result.Errors) : Results.Ok();
    }

    private static async Task<IResult> RemoveBot(long id, BotRegistrationService service, CancellationToken cancellationToken)
    {
        var result = await service.Remove(id, cancellationToken);
        return result.IsFailed ? MapFailure(result.Errors) : Results.NoContent();
    }

    /// <summary>
    /// The assignable behaviors, plus every package held in the extension store — including any that
    /// failed to load, so an operator can see why a behavior is missing and repair it by name.
    /// </summary>
    private static IResult ListBehaviors(IBehaviorCatalog catalog, BehaviorExtensionService extensions) =>
        Results.Ok(new { behaviors = catalog.List(), packages = extensions.Packages });

    /// <summary>
    /// Uploads a compiled behavior-extension assembly, loads it, and registers every <see cref="IBotBehavior"/>
    /// it contains. Create-only: a name already in the store is a conflict, so an accidental re-upload can
    /// never silently supersede a working extension — use <c>PUT</c> for that. A bad extension is rejected
    /// without affecting the running platform or any bot.
    /// </summary>
    private static async Task<IResult> UploadBehaviorExtension(
        IFormFile package,
        BehaviorExtensionService behaviorExtensionService,
        IOptions<PlatformOptions> platformOptions,
        CancellationToken cancellationToken)
    {
        if (RejectIfTooLarge(package, platformOptions.Value) is { } tooLarge)
        {
            return tooLarge;
        }

        await using var stream = package.OpenReadStream();
        var result = await behaviorExtensionService.Upload(package.FileName, stream, cancellationToken);

        // Echo the name the service actually stored under, not a re-derivation of the client's — the two
        // differ whenever the supplied name carried a path or padding.
        return result.IsFailed
            ? MapFailure(result.Errors)
            : Results.Created(
                $"/admin/behaviors/{result.Value.PackageName}",
                new { loaded = result.Value.BehaviorKeys, assembly = result.Value.PackageName });
    }

    /// <summary>
    /// Replaces a stored extension with a new build, hot-swapping its behaviors for bots already running.
    /// A replacement that fails to load, collides, or would take away a behavior a bot is still assigned to
    /// changes nothing at all.
    /// </summary>
    private static async Task<IResult> ReplaceBehaviorExtension(
        string packageName,
        IFormFile package,
        BehaviorExtensionService extensions,
        IOptions<PlatformOptions> platformOptions,
        CancellationToken cancellationToken)
    {
        if (RejectIfTooLarge(package, platformOptions.Value) is { } tooLarge)
        {
            return tooLarge;
        }

        await using var stream = package.OpenReadStream();
        var result = await extensions.Replace(packageName, stream, cancellationToken);

        return result.IsFailed
            ? MapFailure(result.Errors)
            : Results.Ok(new { loaded = result.Value.BehaviorKeys, assembly = result.Value.PackageName });
    }

    /// <summary>Removes a stored extension. Refused while a registered bot is still assigned to one of its behaviors.</summary>
    private static async Task<IResult> RemoveBehaviorExtension(
        string packageName, BehaviorExtensionService extensions, CancellationToken cancellationToken)
    {
        var result = await extensions.Remove(packageName, cancellationToken);

        return result.IsFailed ? MapFailure(result.Errors) : Results.NoContent();
    }

    /// <summary>
    /// Rejects an oversized package with a message naming the limit, rather than letting it die on the wire.
    /// <para>
    /// This is the friendly half of the ceiling, not the enforcing one: form binding has already buffered
    /// the part (in memory to 64 KB, then to a temp file) by the time this runs, so it bounds what the
    /// platform loads into <em>managed memory</em>, not what it accepts off the socket. The transport bound
    /// is <see cref="ExtensionUploadSizeLimit"/>, which the server applies before reading the body at all.
    /// </para>
    /// </summary>
    private static IResult? RejectIfTooLarge(IFormFile package, PlatformOptions options)
    {
        if (package.Length <= options.MaxExtensionPackageBytes)
        {
            return null;
        }

        var limitInMegabytes = options.MaxExtensionPackageBytes / (1024d * 1024d);

        return Results.Json(
            new { error = $"Package exceeds the {limitInMegabytes.ToString("0.#", CultureInfo.InvariantCulture)} MB limit." },
            statusCode: StatusCodes.Status413PayloadTooLarge);
    }

    /// <summary>
    /// Maps a domain failure to the closest HTTP status; never echoes back a token.
    /// <para>
    /// The extension lifecycle reports failures as <see cref="ExtensionError"/> subtypes, so its mapping is
    /// by type and survives any rewording. The bot endpoints still return plain errors, which the message
    /// checks below cover — those are the fallback, not the primary mechanism.
    /// </para>
    /// </summary>
    private static IResult MapFailure(IReadOnlyList<IError> errors)
    {
        var error = errors.Count > 0 ? errors[0] : null;
        var message = error?.Message ?? "The request could not be completed.";

        switch (error)
        {
            // A store that cannot be reached is neither the caller's fault nor a missing resource — it is
            // the platform being temporarily unable to serve, so it must not be reported as a 400.
            case StoreUnavailableError:
                return Results.Json(new { error = message }, statusCode: StatusCodes.Status503ServiceUnavailable);

            case PackageNotFoundError:
                return Results.NotFound(new { error = message });

            case ExtensionConflictError:
                return Results.Conflict(new { error = message });

            // An in-use refusal carries the blocking bot ids so tooling need not parse the message.
            case BehaviorInUseError inUse:
                return Results.Conflict(new { error = message, bots = inUse.BotIds });
        }

        if (message.Contains("already registered", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { error = message });
        }

        if (message.Contains("was not found", StringComparison.OrdinalIgnoreCase))
        {
            return Results.NotFound(new { error = message });
        }

        return Results.BadRequest(new { error = message });
    }

    private static object ToResponse(BotRegistration bot) => new
    {
        botId = bot.Id,
        telegramBotId = bot.TelegramBotId,
        username = bot.Username,
        label = bot.Label,
        behaviorKey = bot.BehaviorKey,
        status = bot.Status.ToString(),
        createdAt = bot.CreatedAt,
        updatedAt = bot.UpdatedAt
    };

    private sealed record RegisterBotRequest(string Label, string BehaviorKey, string Token);

    private sealed record RotateTokenRequest(string Token);
}