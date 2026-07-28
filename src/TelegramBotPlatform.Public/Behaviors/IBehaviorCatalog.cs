namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>Where a behavior came from — shown via <c>GET /admin/behaviors</c>.</summary>
public sealed record BehaviorDescriptor(string Key, string DisplayName, string Source);

/// <summary>
/// The set of bot behaviors known to the running platform: built-ins registered at startup, plus any
/// operator-supplied behavior extensions loaded later. Registration logic (contract-version check,
/// key-collision rejection) is uniform regardless of source, so a bad extension can never corrupt or
/// replace an existing behavior.
/// <para>
/// The per-source operations are <em>atomic</em>: a package contributing several keys is added, swapped,
/// or removed as one visible transition, so an update in flight never observes a half-applied change.
/// </para>
/// </summary>
public interface IBehaviorCatalog
{
    bool TryGet(string key, out IBotBehavior? behavior);

    IReadOnlyList<BehaviorDescriptor> List();

    /// <summary>
    /// Registers a behavior discovered from <paramref name="source"/> (e.g. "built-in" or
    /// "extension:Reminders.dll"). Fails if its <see cref="IBotBehavior.ContractVersion"/> major version
    /// differs from <see cref="BehaviorContractVersion.Current"/>, or if its <see cref="IBotBehavior.Key"/>
    /// collides with an already-registered behavior.
    /// </summary>
    Result Register(IBotBehavior behavior, string source);

    /// <summary>The behavior keys currently registered from <paramref name="source"/>.</summary>
    IReadOnlyList<string> KeysFromSource(string source);

    /// <summary>
    /// Atomically swaps every behavior registered from <paramref name="source"/> for
    /// <paramref name="behaviors"/>. This is also how a source is registered for the first time.
    /// <para>
    /// The whole incoming set is validated before anything is published: every contract version must be
    /// compatible, no key may already belong to a <em>different</em> source, and no key may repeat within
    /// the set. On failure nothing changes — there is no partially-applied outcome. A source may freely
    /// re-declare the keys it already owns, which is what makes replacing a package with a new build work.
    /// </para>
    /// </summary>
    Result ReplaceSource(string source, IReadOnlyList<IBotBehavior> behaviors);

    /// <summary>Atomically unregisters every behavior from <paramref name="source"/>.</summary>
    Result RemoveSource(string source);
}