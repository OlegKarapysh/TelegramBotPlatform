namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// Durable storage for operator-uploaded behavior-extension packages. Independent of any single compute
/// instance's local disk, so an uploaded extension survives that instance being replaced. Implemented
/// over a local directory for development and over object storage for a real deployment; the platform
/// picks one by configuration and behaves identically against either.
/// <para>
/// Package names arrive already validated (see <c>ExtensionPackageName</c>) — an implementation joins the
/// name to its own root and never interprets it as a path or key fragment beyond that.
/// </para>
/// </summary>
public interface IExtensionStore
{
    /// <summary>
    /// Every stored package name. A reachable but empty store succeeds with an empty list — only a store
    /// that cannot be reached fails, and that failure is distinguishable from "not found" so the platform
    /// can tell a misconfiguration from an absence.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default);

    /// <summary>The package's bytes, or a "was not found" failure when it is not stored.</summary>
    Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the package. With <paramref name="overwrite"/> false the write fails if the name is already
    /// taken, leaving the stored bytes untouched; with it true the previous bytes are replaced, and a
    /// failed write leaves them intact.
    /// </summary>
    Task<Result> Write(string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Removes the package. Idempotent — removing something already absent succeeds.</summary>
    Task<Result> Delete(string packageName, CancellationToken cancellationToken = default);
}