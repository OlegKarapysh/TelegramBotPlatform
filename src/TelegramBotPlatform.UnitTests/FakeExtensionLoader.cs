using FluentResults;
using TelegramBotPlatform.Public.Behaviors;

namespace TelegramBotPlatform.UnitTests;

/// <summary>
/// Scripted <see cref="IExtensionLoader"/> for tests — no reflection, no assembly load context, no disk.
/// Each handle records its own disposal, which is how the "superseded packages are released" requirement
/// is asserted deterministically instead of depending on GC timing.
/// </summary>
public sealed class FakeExtensionLoader : IExtensionLoader
{
    private readonly Dictionary<string, Func<Result<ILoadedExtension>>> _scripts = new(StringComparer.Ordinal);

    /// <summary>Every handle this loader has produced, in order, so tests can assert disposal.</summary>
    public List<FakeLoadedExtension> Handles { get; } = [];

    public int DisposedCount => Handles.Count(handle => handle.DisposeCount > 0);

    /// <summary>Next load of <paramref name="packageName"/> yields behaviors with these keys.</summary>
    public FakeExtensionLoader Yields(string packageName, params string[] behaviorKeys)
    {
        _scripts[packageName] = () =>
        {
            var handle = new FakeLoadedExtension(packageName, behaviorKeys);
            Handles.Add(handle);
            return Result.Ok<ILoadedExtension>(handle);
        };

        return this;
    }

    /// <summary>Next load of <paramref name="packageName"/> fails, as a corrupt or incompatible package would.</summary>
    public FakeExtensionLoader Fails(string packageName, string reason = "not a valid assembly")
    {
        _scripts[packageName] = () => Result.Fail<ILoadedExtension>(
            $"Failed to load behavior extension \"{packageName}\": {reason}");

        return this;
    }

    public Result<ILoadedExtension> Load(string packageName, byte[] content) =>
        _scripts.TryGetValue(packageName, out var script)
            ? script()
            : Result.Fail<ILoadedExtension>($"Failed to load behavior extension \"{packageName}\": no script configured.");

    public sealed class FakeLoadedExtension(string packageName, IReadOnlyList<string> behaviorKeys) : ILoadedExtension
    {
        public string PackageName { get; } = packageName;

        public IReadOnlyList<IBotBehavior> Behaviors { get; } =
            behaviorKeys.Select(IBotBehavior (key) => new FakeBehavior(key)).ToArray();

        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeBehavior(string key) : IBotBehavior
    {
        public string Key { get; } = key;
        public string DisplayName => $"Fake:{Key}";
        public string ContractVersion => BehaviorContractVersion.Current;
        public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}