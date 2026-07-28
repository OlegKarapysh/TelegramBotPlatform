namespace TelegramBotPlatform.Application;

/// <summary>
/// Reads the registered bots. A delegate rather than <c>IBotRegistry</c> itself because the registry is
/// scoped (it wraps a DbContext) while this service is a singleton — the composition root supplies a
/// callback that opens a scope per query, which keeps the captive-dependency problem out of here.
/// </summary>
public delegate Task<IReadOnlyList<BotRegistration>> ListRegisteredBots(CancellationToken cancellationToken);

/// <summary>
/// Owns the lifecycle of operator-supplied behavior extensions: restoring them from durable storage at
/// startup, and adding, replacing, and removing them on a running platform.
/// <para>
/// A singleton — it holds the loaded-package handles and serialises every mutation, so a package's bytes,
/// its registered behaviors, and its reported status never disagree. Depends only on interfaces, which is
/// what lets the whole lifecycle be tested against fakes with no disk, network, or database.
/// </para>
/// </summary>
public sealed class BehaviorExtensionService(
    IExtensionStore store,
    IExtensionLoader loader,
    IBehaviorCatalog catalog,
    ListRegisteredBots listRegisteredBots,
    long maxPackageBytes,
    ILogger<BehaviorExtensionService> logger)
{
    /// <summary>Marker phrase the admin API matches to answer 503 rather than 400.</summary>
    public const string StoreUnreachableMarker = "could not be reached";

    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly Dictionary<string, ILoadedExtension> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExtensionPackageStatus> _statuses = new(StringComparer.Ordinal);

    /// <summary>Every package in the store and what became of it — including the ones that failed to load.</summary>
    public IReadOnlyList<ExtensionPackageStatus> Packages
    {
        get
        {
            lock (_statuses)
            {
                return _statuses.Values.OrderBy(status => status.PackageName, StringComparer.Ordinal).ToArray();
            }
        }
    }

    /// <summary>
    /// Loads and registers every stored package. Called once, before the platform starts serving, so no
    /// update can reach a behavior that has not been restored yet.
    /// <para>
    /// Reading the store is retried against a bounded budget to ride out a transient failure; exhausting it
    /// fails, and the caller turns that into a startup abort rather than serving an incomplete catalog. A
    /// single package that will not load is a different matter — it is recorded and skipped.
    /// </para>
    /// </summary>
    public async Task<Result> RestoreAll(TimeSpan retryBudget, CancellationToken cancellationToken = default)
    {
        var listed = await ListWithRetry(retryBudget, cancellationToken);
        if (listed.IsFailed)
        {
            return Result.Fail(listed.Errors);
        }

        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            foreach (var packageName in listed.Value)
            {
                var read = await store.Read(packageName, cancellationToken);
                if (read.IsFailed)
                {
                    RecordFailure(packageName, read.Errors[0].Message);
                    continue;
                }

                var applied = Apply(packageName, read.Value);
                if (applied.IsFailed)
                {
                    RecordFailure(packageName, applied.Errors[0].Message);
                }
            }
        }
        finally
        {
            _mutationLock.Release();
        }

        return Result.Ok();
    }

    /// <summary>Stores a new package and registers its behaviors. Never overwrites an existing one.</summary>
    public async Task<Result<IReadOnlyList<string>>> Upload(
        string packageName, Stream content, CancellationToken cancellationToken = default)
    {
        var name = ExtensionPackageName.Validate(packageName);
        if (name.IsFailed)
        {
            return Result.Fail<IReadOnlyList<string>>(name.Errors);
        }

        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var listed = await store.List(cancellationToken);
            if (listed.IsFailed)
            {
                return Result.Fail<IReadOnlyList<string>>(listed.Errors);
            }

            if (listed.Value.Contains(name.Value, StringComparer.Ordinal))
            {
                return new Error($"A behavior extension named \"{name.Value}\" already exists.");
            }

            return await WriteLoadAndRegister(name.Value, content, overwrite: false, cancellationToken);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Replaces a stored package with a new build, hot-swapping its behaviors. Refused if the new build
    /// drops a behavior a registered bot still uses; on any failure the previous package stays stored,
    /// registered, and running.
    /// </summary>
    public async Task<Result<IReadOnlyList<string>>> Replace(
        string packageName, Stream content, CancellationToken cancellationToken = default)
    {
        var name = ExtensionPackageName.Validate(packageName);
        if (name.IsFailed)
        {
            return Result.Fail<IReadOnlyList<string>>(name.Errors);
        }

        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var listed = await store.List(cancellationToken);
            if (listed.IsFailed)
            {
                return Result.Fail<IReadOnlyList<string>>(listed.Errors);
            }

            if (!listed.Value.Contains(name.Value, StringComparer.Ordinal))
            {
                return new Error($"Behavior extension \"{name.Value}\" was not found.");
            }

            // Load the new build BEFORE touching anything, so a broken replacement costs nothing.
            var buffered = await Buffer(content, cancellationToken);
            if (buffered.IsFailed)
            {
                return Result.Fail<IReadOnlyList<string>>(buffered.Errors);
            }

            var loadedNew = loader.Load(name.Value, buffered.Value);
            if (loadedNew.IsFailed)
            {
                return Result.Fail<IReadOnlyList<string>>(loadedNew.Errors);
            }

            var source = BehaviorSource.Extension(name.Value);
            var newKeys = loadedNew.Value.Behaviors.Select(behavior => behavior.Key).ToArray();

            var disappearing = catalog.KeysFromSource(source).Except(newKeys, StringComparer.Ordinal).ToArray();
            var inUse = await FindBotsUsing(disappearing, cancellationToken);
            if (inUse.Count > 0)
            {
                loadedNew.Value.Dispose();
                return InUseError(disappearing, inUse);
            }

            var registered = catalog.ReplaceSource(source, loadedNew.Value.Behaviors);
            if (registered.IsFailed)
            {
                loadedNew.Value.Dispose();
                return Result.Fail<IReadOnlyList<string>>(registered.Errors);
            }

            var written = await store.Write(name.Value, new MemoryStream(buffered.Value), overwrite: true, cancellationToken);
            if (written.IsFailed)
            {
                // Put the catalog back the way it was — the stored bytes never changed, so restoring the
                // previous behaviors leaves no trace of the attempt.
                RollBackTo(source, name.Value);
                loadedNew.Value.Dispose();

                return Result.Fail<IReadOnlyList<string>>(written.Errors);
            }

            Swap(name.Value, loadedNew.Value, newKeys);

            logger.LogInformation(
                "Replaced behavior extension {Package}; behaviors now {Keys}.", name.Value, string.Join(", ", newKeys));

            return Result.Ok<IReadOnlyList<string>>(newKeys);
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>
    /// Removes a stored package and unregisters its behaviors. Refused while a registered bot — including
    /// a disabled one, which can be re-enabled — is still assigned to one of them.
    /// </summary>
    public async Task<Result> Remove(string packageName, CancellationToken cancellationToken = default)
    {
        var name = ExtensionPackageName.Validate(packageName);
        if (name.IsFailed)
        {
            return Result.Fail(name.Errors);
        }

        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var listed = await store.List(cancellationToken);
            if (listed.IsFailed)
            {
                return Result.Fail(listed.Errors);
            }

            if (!listed.Value.Contains(name.Value, StringComparer.Ordinal))
            {
                return new Error($"Behavior extension \"{name.Value}\" was not found.");
            }

            var source = BehaviorSource.Extension(name.Value);
            var keys = catalog.KeysFromSource(source);

            var inUse = await FindBotsUsing(keys, cancellationToken);
            if (inUse.Count > 0)
            {
                return InUseError(keys, inUse);
            }

            var deleted = await store.Delete(name.Value, cancellationToken);
            if (deleted.IsFailed)
            {
                return deleted;
            }

            catalog.RemoveSource(source);
            Forget(name.Value);

            logger.LogInformation("Removed behavior extension {Package}.", name.Value);

            return Result.Ok();
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    /// <summary>Bots assigned to any of <paramref name="keys"/>, as ids — empty when the keys are free.</summary>
    private async Task<IReadOnlyList<long>> FindBotsUsing(IReadOnlyList<string> keys, CancellationToken cancellationToken)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var registrations = await listRegisteredBots(cancellationToken);

        return registrations
            .Where(registration => keys.Contains(registration.BehaviorKey, StringComparer.Ordinal))
            .Select(registration => registration.Id)
            .Order()
            .ToArray();
    }

    private static Error InUseError(IReadOnlyList<string> keys, IReadOnlyList<long> botIds) =>
        new Error(
                $"Behavior(s) {string.Join(", ", keys.Select(key => $"\"{key}\""))} "
                + $"are still assigned to bot(s) {string.Join(", ", botIds)}.")
            .WithMetadata("bots", botIds);

    /// <summary>Writes, loads, and registers in one go, undoing the write if anything downstream rejects it.</summary>
    private async Task<Result<IReadOnlyList<string>>> WriteLoadAndRegister(
        string packageName, Stream content, bool overwrite, CancellationToken cancellationToken)
    {
        var buffered = await Buffer(content, cancellationToken);
        if (buffered.IsFailed)
        {
            return Result.Fail<IReadOnlyList<string>>(buffered.Errors);
        }

        var written = await store.Write(packageName, new MemoryStream(buffered.Value), overwrite, cancellationToken);
        if (written.IsFailed)
        {
            return Result.Fail<IReadOnlyList<string>>(written.Errors);
        }

        var applied = Apply(packageName, buffered.Value);
        if (applied.IsFailed)
        {
            // Any rejection past the write deletes the package, so a bad or colliding upload never lingers
            // to fail again on every subsequent startup.
            await store.Delete(packageName, cancellationToken);

            return Result.Fail<IReadOnlyList<string>>(applied.Errors);
        }

        logger.LogInformation(
            "Loaded behavior extension {Package}; behaviors {Keys}.", packageName, string.Join(", ", applied.Value));

        return applied;
    }

    /// <summary>Loads bytes and registers the result, replacing whatever that package previously contributed.</summary>
    private Result<IReadOnlyList<string>> Apply(string packageName, byte[] content)
    {
        var loaded = loader.Load(packageName, content);
        if (loaded.IsFailed)
        {
            return Result.Fail<IReadOnlyList<string>>(loaded.Errors);
        }

        var source = BehaviorSource.Extension(packageName);

        var registered = catalog.ReplaceSource(source, loaded.Value.Behaviors);
        if (registered.IsFailed)
        {
            loaded.Value.Dispose();

            return Result.Fail<IReadOnlyList<string>>(registered.Errors);
        }

        var keys = loaded.Value.Behaviors.Select(behavior => behavior.Key).ToArray();
        Swap(packageName, loaded.Value, keys);

        return Result.Ok<IReadOnlyList<string>>(keys);
    }

    /// <summary>Installs the new handle and disposes the one it supersedes, so nothing is left holding an assembly.</summary>
    private void Swap(string packageName, ILoadedExtension loaded, IReadOnlyList<string> keys)
    {
        if (_loaded.Remove(packageName, out var superseded))
        {
            superseded.Dispose();
        }

        _loaded[packageName] = loaded;

        lock (_statuses)
        {
            _statuses[packageName] = new ExtensionPackageStatus(packageName, Loaded: true, keys, Error: null);
        }
    }

    private void RollBackTo(string source, string packageName) =>
        catalog.ReplaceSource(
            source,
            _loaded.TryGetValue(packageName, out var previous) ? previous.Behaviors : []);

    private void Forget(string packageName)
    {
        if (_loaded.Remove(packageName, out var removed))
        {
            removed.Dispose();
        }

        lock (_statuses)
        {
            _statuses.Remove(packageName);
        }
    }

    private void RecordFailure(string packageName, string reason)
    {
        logger.LogError("Failed to restore behavior extension {Package}: {Error}", packageName, reason);

        lock (_statuses)
        {
            _statuses[packageName] = new ExtensionPackageStatus(packageName, Loaded: false, [], reason);
        }
    }

    /// <summary>
    /// Reads the store, retrying with exponential backoff until the budget runs out. Distinguishes "the
    /// store is unreachable" (worth retrying, then fatal) from "the store is empty" (a normal start).
    /// </summary>
    private async Task<Result<IReadOnlyList<string>>> ListWithRetry(TimeSpan budget, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        var delay = TimeSpan.FromSeconds(1);

        while (true)
        {
            var listed = await store.List(cancellationToken);
            if (listed.IsSuccess)
            {
                return listed;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return listed;
            }

            logger.LogWarning(
                "Could not read the behavior extension store ({Error}); retrying for up to {Remaining:0.#}s.",
                listed.Errors[0].Message,
                remaining.TotalSeconds);

            await Task.Delay(delay < remaining ? delay : remaining, cancellationToken);
            delay *= 2;
        }
    }

    /// <summary>
    /// Buffers the upload, refusing anything over the configured ceiling. The endpoint already rejects on
    /// the declared content length; this is the backstop that makes the limit a property of the operation
    /// rather than of one call site, and it stops reading as soon as the ceiling is passed — an oversized
    /// package never sits in memory in full.
    /// </summary>
    private async Task<Result<byte[]>> Buffer(Stream content, CancellationToken cancellationToken)
    {
        if (content.CanSeek && content.Length > maxPackageBytes)
        {
            return TooLarge();
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];

        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxPackageBytes)
            {
                return TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return Result.Ok(buffer.ToArray());
    }

    private Error TooLarge() =>
        new($"The package exceeds the {maxPackageBytes / (1024d * 1024d):0.#} MB limit.");
}