using FluentResults;

namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// An extension store whose reachability a test controls.
/// <para>
/// The platform draws a hard line between a store that is <em>empty</em> and one that <em>cannot be
/// reached</em>: the first is a normal start, the second aborts startup and answers 503 rather than 400.
/// A healthy local directory cannot produce the second, so this store stands in for a bucket whose
/// permissions are wrong or whose region is down. Reachable, it behaves like any other store.
/// </para>
/// </summary>
public sealed class ControllableExtensionStore : IExtensionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);

    public bool IsReachable { get; set; } = true;

    public Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default)
    {
        if (!IsReachable)
        {
            return Task.FromResult(Result.Fail<IReadOnlyList<string>>(Unreachable()));
        }

        lock (_gate)
        {
            return Task.FromResult(
                Result.Ok<IReadOnlyList<string>>(_packages.Keys.Order(StringComparer.Ordinal).ToArray()));
        }
    }

    public Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default)
    {
        if (!IsReachable)
        {
            return Task.FromResult(Result.Fail<byte[]>(Unreachable()));
        }

        lock (_gate)
        {
            return Task.FromResult(_packages.TryGetValue(packageName, out var content)
                ? Result.Ok(content)
                : Result.Fail<byte[]>(new PackageNotFoundError($"Behavior extension \"{packageName}\" was not found.")));
        }
    }

    public async Task<Result> Write(
        string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (!IsReachable)
        {
            return Unreachable();
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        lock (_gate)
        {
            if (!overwrite && _packages.ContainsKey(packageName))
            {
                return new ExtensionConflictError($"A behavior extension named \"{packageName}\" already exists.");
            }

            _packages[packageName] = buffer.ToArray();
        }

        return Result.Ok();
    }

    public Task<Result> Delete(string packageName, CancellationToken cancellationToken = default)
    {
        if (!IsReachable)
        {
            return Task.FromResult(Result.Fail(Unreachable()));
        }

        lock (_gate)
        {
            _packages.Remove(packageName);
        }

        return Task.FromResult(Result.Ok());
    }

    private static StoreUnavailableError Unreachable() =>
        new("The behavior extension store could not be reached.");
}