namespace TelegramBotPlatform.Infrastructure.Security;

/// <summary>
/// Authenticates every <c>/admin/*</c> request against the configured static admin key, via either
/// <c>Authorization: Bearer &lt;key&gt;</c> or <c>X-Admin-Api-Key: &lt;key&gt;</c>.
/// </summary>
public sealed class AdminApiKeyAuth(IOptions<PlatformOptions> platformOptions) : IEndpointFilter
{
    private const string _bearerPrefix = "Bearer ";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var provided = ExtractKey(context.HttpContext.Request);

        if (provided is null || !FixedTimeEquals(provided, platformOptions.Value.AdminApiKey))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        if (request.Headers.TryGetValue("X-Admin-Api-Key", out var headerValue) && !string.IsNullOrEmpty(headerValue))
        {
            return headerValue.ToString();
        }

        var authorization = request.Headers.Authorization.ToString();
        return authorization.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? authorization[_bearerPrefix.Length..].Trim()
            : null;
    }

    private static bool FixedTimeEquals(string provided, string expected)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return providedBytes.Length == expectedBytes.Length
               && CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}