namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// Durable storage for operator-uploaded behavior-extension packages. Independent of any single compute
/// instance's local disk, so an uploaded extension survives that instance being replaced. Implemented
/// over a local directory for development and over object storage for a real deployment; the platform
/// picks one by configuration and behaves identically against either.
/// <para>
/// Package names arrive already validated (see <c>ExtensionPackageName</c>) — an implementation joins the
/// name to its own root and never interprets it as a path or key fragment beyond that. Callers re-validate
/// names an implementation hands back, since a store's contents are not necessarily only what this
/// platform put there.
/// </para>
/// <para>
/// Failures are reported as <see cref="ExtensionError"/> subtypes so callers classify them by type, never
/// by message text.
/// </para>
/// </summary>
public interface IExtensionStore
{
    /// <summary>
    /// Every stored package name. A reachable but empty store succeeds with an empty list; only a store
    /// that cannot be reached fails, with a <see cref="StoreUnavailableError"/>, so the platform can tell a
    /// misconfiguration from an absence.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> List(CancellationToken cancellationToken = default);

    /// <summary>
    /// The package's bytes, or a <see cref="PackageNotFoundError"/> when it is not stored.
    /// <para>
    /// An implementation is allowed to report an absent package as a <see cref="StoreUnavailableError"/>
    /// when it genuinely cannot tell the two apart — S3 answers <c>403</c> rather than <c>404</c> for a
    /// missing key unless the caller holds an unconditional <c>s3:ListBucket</c> grant, and the task role
    /// deliberately does not. Callers must therefore treat "unavailable" as the weaker claim and never
    /// infer existence from it.
    /// </para>
    /// </summary>
    Task<Result<byte[]>> Read(string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the package. With <paramref name="overwrite"/> false the write fails with a
    /// <see cref="ExtensionConflictError"/> if the name is already taken, leaving the stored bytes untouched;
    /// with it true the previous bytes are replaced, and a failed write leaves them intact.
    /// </summary>
    Task<Result> Write(string packageName, Stream content, bool overwrite, CancellationToken cancellationToken = default);

    /// <summary>Removes the package. Idempotent — removing something already absent succeeds.</summary>
    Task<Result> Delete(string packageName, CancellationToken cancellationToken = default);
}