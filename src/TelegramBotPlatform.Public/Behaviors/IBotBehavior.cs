namespace TelegramBotPlatform.Public.Behaviors;

/// <summary>
/// The plugin contract for bot behaviors. Built-in behaviors and operator-supplied behavior extensions
/// implement the same interface, so the platform treats them uniformly. A behavior determines what a
/// bot does: the commands and features it exposes to its users.
/// </summary>
public interface IBotBehavior
{
    /// <summary>Stable unique identifier stored on a <c>BotRegistration</c> (e.g. "echo"). Kebab-case.</summary>
    string Key { get; }

    /// <summary>Human-friendly name shown in <c>GET /admin/behaviors</c>.</summary>
    string DisplayName { get; }

    /// <summary>SDK contract version this behavior was built against — see <see cref="BehaviorContractVersion"/>.</summary>
    string ContractVersion { get; }

    /// <summary>
    /// Handle one update for one bot. Should not throw for expected failures; any throw is caught and
    /// contained by the platform but marks the bot's health.
    /// </summary>
    Task HandleUpdate(IBotUpdateContext context, CancellationToken cancellationToken);
}