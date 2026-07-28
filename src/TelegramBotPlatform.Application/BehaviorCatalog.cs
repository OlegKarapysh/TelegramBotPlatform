namespace TelegramBotPlatform.Application;

/// <summary>
/// The set of bot behaviors known to the running platform. Registration is uniform for built-in behaviors
/// (registered at startup) and operator-supplied extensions (registered after being loaded by
/// <c>IExtensionLoader</c>) — pure in-memory logic, deliberately free of any disk/reflection concern
/// so it stays unit-testable against a fake behavior.
/// <para>
/// State is an immutable snapshot published behind a volatile reference: readers — every incoming update
/// does a <see cref="TryGet"/> — never lock, and writers build the complete next dictionary under a lock
/// and swap it in with a single assignment. That is what makes a multi-key operation atomic: replacing a
/// package's three keys with four is one visible transition, never seven.
/// </para>
/// </summary>
public sealed class BehaviorCatalog : IBehaviorCatalog
{
    private readonly Lock _writeLock = new();

    // Deliberately NOT marked `volatile`. Every access goes through Current/Publish below, which state the
    // memory ordering explicitly at each use site — and a `volatile` field cannot be passed by ref to
    // Volatile.Read/Write anyway. Reading the field directly anywhere else would reintroduce exactly the
    // "sometimes synchronized, sometimes not" ambiguity this pair exists to remove.
    private FrozenDictionary<string, RegisteredBehavior> _snapshot =
        FrozenDictionary<string, RegisteredBehavior>.Empty;

    /// <summary>
    /// The currently published snapshot. Deliberately lock-free — this is the hot path (every incoming
    /// update does a <see cref="TryGet"/>), and a reader can only ever observe a fully-built dictionary.
    /// Read it exactly once per operation: two reads may return different snapshots.
    /// </summary>
    private FrozenDictionary<string, RegisteredBehavior> Current => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Publishes the next snapshot. Callers MUST hold <see cref="_writeLock"/> — the lock serialises
    /// writers with each other, while this write is what makes the new state visible to lock-free readers.
    /// </summary>
    private void Publish(FrozenDictionary<string, RegisteredBehavior> next)
    {
        Debug.Assert(_writeLock.IsHeldByCurrentThread, "The catalog snapshot must only be published under the write lock.");

        Volatile.Write(ref _snapshot, next);
    }

    public bool TryGet(string key, out IBotBehavior? behavior)
    {
        if (Current.TryGetValue(key, out var entry))
        {
            behavior = entry.Behavior;
            return true;
        }

        behavior = null;
        return false;
    }

    public IReadOnlyList<BehaviorDescriptor> List() =>
        Current.Values
            .Select(entry => new BehaviorDescriptor(entry.Behavior.Key, entry.Behavior.DisplayName, entry.Source))
            .OrderBy(descriptor => descriptor.Key, StringComparer.Ordinal)
            .ToArray();

    public IReadOnlyList<string> KeysFromSource(string source) =>
        Current.Values
            .Where(entry => string.Equals(entry.Source, source, StringComparison.Ordinal))
            .Select(entry => entry.Behavior.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public Result Register(IBotBehavior behavior, string source)
    {
        if (!IsCompatibleContractVersion(behavior.ContractVersion))
        {
            return IncompatibleContractVersion(behavior);
        }

        lock (_writeLock)
        {
            var current = Current;

            if (current.ContainsKey(behavior.Key))
            {
                return new Error($"A behavior with key \"{behavior.Key}\" is already registered.");
            }

            var next = current.ToDictionary(StringComparer.Ordinal);
            next[behavior.Key] = new RegisteredBehavior(behavior, source);
            Publish(next.ToFrozenDictionary(StringComparer.Ordinal));
        }

        return Result.Ok();
    }

    public Result ReplaceSource(string source, IReadOnlyList<IBotBehavior> behaviors)
    {
        // Everything is validated before anything is published, so a rejected set leaves the catalog
        // exactly as it was — no half-applied replacement is ever observable.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var behavior in behaviors)
        {
            if (!IsCompatibleContractVersion(behavior.ContractVersion))
            {
                return IncompatibleContractVersion(behavior);
            }

            if (!seen.Add(behavior.Key))
            {
                return new Error($"The extension declares the behavior key \"{behavior.Key}\" more than once.");
            }
        }

        lock (_writeLock)
        {
            var current = Current;

            foreach (var behavior in behaviors)
            {
                // A source may re-declare its OWN keys — that is exactly what shipping a new build does.
                if (current.TryGetValue(behavior.Key, out var existing)
                    && !string.Equals(existing.Source, source, StringComparison.Ordinal))
                {
                    return new Error($"A behavior with key \"{behavior.Key}\" is already registered.");
                }
            }

            var next = current
                .Where(entry => !string.Equals(entry.Value.Source, source, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            foreach (var behavior in behaviors)
            {
                next[behavior.Key] = new RegisteredBehavior(behavior, source);
            }

            Publish(next.ToFrozenDictionary(StringComparer.Ordinal));
        }

        return Result.Ok();
    }

    public Result RemoveSource(string source)
    {
        lock (_writeLock)
        {
            var next = Current
                .Where(entry => !string.Equals(entry.Value.Source, source, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

            Publish(next.ToFrozenDictionary(StringComparer.Ordinal));
        }

        return Result.Ok();
    }

    /// <summary>Only the major version must match — minor/patch bumps to the SDK contract stay compatible.</summary>
    private static bool IsCompatibleContractVersion(string contractVersion) =>
        GetMajorVersion(contractVersion) == GetMajorVersion(BehaviorContractVersion.Current);

    private static Error IncompatibleContractVersion(IBotBehavior behavior) =>
        new($"Behavior \"{behavior.Key}\" targets contract version {behavior.ContractVersion}, which is "
            + $"incompatible with the platform's current contract version {BehaviorContractVersion.Current}.");

    private static int GetMajorVersion(string version)
    {
        var separatorIndex = version.IndexOf('.');
        var majorPart = separatorIndex < 0 ? version : version[..separatorIndex];
        return int.TryParse(majorPart, out var major) ? major : -1;
    }

    private sealed record RegisteredBehavior(IBotBehavior Behavior, string Source);
}