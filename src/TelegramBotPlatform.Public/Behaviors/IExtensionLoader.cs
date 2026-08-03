namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// A loaded extension package and the isolated context its assembly lives in. Disposing it releases both
/// — the platform drops every reference it holds so the runtime can collect the context, and any file
/// staged on the way in is deleted.
/// <para>
/// Disposal is a <em>request</em>: a collectible load context is only collected once nothing references
/// it, so an update still being handled by one of these behaviors keeps it alive until that call returns.
/// That is what makes replacing a live extension safe without draining in-flight work.
/// </para>
/// </summary>
public interface ILoadedExtension : IDisposable
{
    string PackageName { get; }

    /// <summary>Every usable behavior found in the package. Never empty — a package with none fails to load.</summary>
    IReadOnlyList<IBotBehavior> Behaviors { get; }
}

/// <summary>
/// Loads package bytes into an isolated, unloadable context and discovers the behaviors inside. A bad
/// package — one that fails to load, or contains no usable behavior — is reported as a failed result
/// without touching anything already running.
/// </summary>
public interface IExtensionLoader
{
    Result<ILoadedExtension> Load(string packageName, byte[] content);
}