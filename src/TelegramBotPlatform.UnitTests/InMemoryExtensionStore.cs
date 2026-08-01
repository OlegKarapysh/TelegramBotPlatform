using FluentResults;
using TelegramBotPlatform.Public.Behaviors;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// Dictionary-backed <see cref="IExtensionStore"/> for tests — keeps the suite pure (no filesystem, no
/// network) while still exercising every branch of the service, including the failure paths that only a
/// remote store can really produce. Set <see cref="FailList"/>, <see cref="FailRead"/>,
/// <see cref="FailWrite"/>, or <see cref="FailDelete"/> to make an operation report an unreachable store.
/// </summary>
public sealed class InMemoryExtensionStore : IExtensionStore
{
    private readonly Dictionary<string, byte[]> _packages = new(StringComparer.Ordinal);

    public bool FailList { get; set; }
    public bool FailRead { get; set; }
    public bool FailWrite { get; set; }
    public bool FailDelete { get; set; }

    /// <summary>Number of remaining <see cref="List"/> calls to fail before recovering — for retry tests.</summary>
    public int FailListTimes { get; set; }

    /// <summary>Number of remaining <see cref="Read"/> calls to fail before recovering — for retry tests.</summary>
    public int FailReadTimes { get; set; }

    public int ListCallCount { get; private set; }

    public int ReadCallCount { get; private set; }

    /// <summary>Seeds a package directly, as if it had been uploaded in an earlier run.</summary>
    public void Seed(string packageName, byte[]? content = null) =>
        _packages[packageName] = content ?? [1, 2, 3];

    public bool Contains(string packageName) => _packages.ContainsKey(packageName);

    public byte[]? Bytes(string packageName) => _packages.GetValueOrDefault(packageName);

    public int Count => _packages.Count;

    public Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default)
    {
        ListCallCount++;

        if (FailListTimes > 0)
        {
            FailListTimes--;
            return Task.FromResult(Result.Fail<IReadOnlyList<string>>(Unreachable()));
        }

        return Task.FromResult(FailList
            ? Result.Fail<IReadOnlyList<string>>(Unreachable())
            : Result.Ok<IReadOnlyList<string>>(_packages.Keys.Order(StringComparer.Ordinal).ToArray()));
    }

    public Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default)
    {
        ReadCallCount++;

        if (FailReadTimes > 0)
        {
            FailReadTimes--;
            return Task.FromResult(Result.Fail<byte[]>(Unreachable()));
        }

        if (FailRead)
        {
            return Task.FromResult(Result.Fail<byte[]>(Unreachable()));
        }

        return Task.FromResult(_packages.TryGetValue(packageName, out var content)
            ? Result.Ok(content)
            : Result.Fail<byte[]>(new PackageNotFoundError($"Behavior extension \"{packageName}\" was not found.")));
    }

    public async Task<Result> Write(string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default)
    {
        if (FailWrite)
        {
            return Result.Fail(Unreachable());
        }

        if (!overwrite && _packages.ContainsKey(packageName))
        {
            return Result.Fail(new ExtensionConflictError($"A behavior extension named \"{packageName}\" already exists."));
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _packages[packageName] = buffer.ToArray();

        return Result.Ok();
    }

    public Task<Result> Delete(string packageName, CancellationToken cancellationToken = default)
    {
        if (FailDelete)
        {
            return Task.FromResult(Result.Fail(Unreachable()));
        }

        _packages.Remove(packageName);
        return Task.FromResult(Result.Ok());
    }

    // The real stores report an outage with this type, and the service and admin API classify on the type
    // rather than on wording — so this fake exercises the same path without having to mirror any phrasing.
    private static StoreUnavailableError Unreachable() =>
        new("The behavior extension store could not be reached.");
}