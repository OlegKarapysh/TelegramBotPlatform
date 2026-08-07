namespace TelegramBotPlatform.IntegrationTests.Infrastructure;

/// <summary>
/// A behavior a test drives directly: it counts what it handled and fails when told to.
/// <para>
/// Failure on demand is the input the platform's fault containment and health tracking exist for, and no
/// shippable extension would provide it — so this is registered into the running catalog rather than
/// uploaded as a package (see <see cref="PlatformTestHost.RegisterBehavior{T}"/>). Everything downstream
/// of it — the router's containment, the tracker, the status write, the admin API — is production code.
/// </para>
/// </summary>
public sealed class ControllableBehavior(string key = ControllableBehavior.DefaultKey) : IBotBehavior
{
    public const string DefaultKey = "test-controllable";

    private int _handled;

    public string Key { get; } = key;

    public string DisplayName => $"Controllable ({Key})";

    public string ContractVersion => BehaviorContractVersion.Current;

    /// <summary>While true, every update this behavior is given throws.</summary>
    public bool ShouldThrow { get; set; }

    /// <summary>How many updates reached this behavior, whether or not they went on to throw.</summary>
    public int Handled => Volatile.Read(ref _handled);

    public Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _handled);

        if (ShouldThrow)
        {
            throw new InvalidOperationException("This behavior was told to fail.");
        }

        // Replying through the context's client is how a test tells "handled" apart from "never arrived".
        return context.Client.SendMessage(
            context.Update.Message!.Chat.Id, $"handled: {context.Update.Message.Text}", cancellationToken: cancellationToken);
    }

    /// <summary>Waits until this behavior has been given <paramref name="count"/> updates.</summary>
    public Task WaitForHandled(int count, CancellationToken cancellationToken) =>
        Wait.Until(
            () => Handled >= count,
            () => $"behavior \"{Key}\" to handle {count} update(s); it handled {Handled}",
            cancellationToken);
}