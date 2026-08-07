using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using TelegramBotPlatform.Infrastructure;
using TelegramBotPlatform.Infrastructure.Security;

namespace TelegramBotPlatform.UnitTests;

public sealed class AdminApiKeyAuthTests
{
    private const string ValidKey = "s3cr3t-admin-key";

    /// <summary>What the endpoint would return; seeing it back means the filter let the request through.</summary>
    private static readonly EndpointFilterDelegate _reachedTheHandler = _ => ValueTask.FromResult<object?>("ok");

    [Fact]
    public async Task InvokeAsync_Allows_WhenBearerKeyMatches()
    {
        var context = Request(header: "Authorization", value: $"Bearer {ValidKey}");

        var result = await CreateFilter().InvokeAsync(context, _reachedTheHandler);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task InvokeAsync_Allows_WhenHeaderKeyMatches()
    {
        var context = Request(header: "X-Admin-Api-Key", value: ValidKey);

        var result = await CreateFilter().InvokeAsync(context, _reachedTheHandler);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task InvokeAsync_RejectsUnauthorized_WhenKeyIsMissing()
    {
        var context = EndpointFilterInvocationContext.Create(new DefaultHttpContext());

        var result = await CreateFilter().InvokeAsync(context, _reachedTheHandler);

        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    [Fact]
    public async Task InvokeAsync_RejectsUnauthorized_WhenKeyIsWrong()
    {
        var context = Request(header: "X-Admin-Api-Key", value: "wrong-key");

        var result = await CreateFilter().InvokeAsync(context, _reachedTheHandler);

        Assert.IsType<UnauthorizedHttpResult>(result);
    }

    private static EndpointFilterInvocationContext Request(string header, string value)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[header] = value;

        return EndpointFilterInvocationContext.Create(httpContext);
    }

    private static AdminApiKeyAuth CreateFilter() =>
        new(Options.Create(new PlatformOptions { AdminApiKey = ValidKey }));
}