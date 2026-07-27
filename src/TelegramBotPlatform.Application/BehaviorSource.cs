namespace TelegramBotPlatform.Application;

/// <summary>
/// The provenance strings recorded against a registered behavior and surfaced via <c>GET /admin/behaviors</c>.
/// Assigned by the host at registration time — a behavior never declares its own source.
/// </summary>
public static class BehaviorSource
{
    /// <summary>A behavior compiled into the host and registered at startup.</summary>
    public const string BuiltIn = "built-in";

    /// <summary>A behavior loaded from an operator-uploaded extension assembly.</summary>
    public static string Extension(string fileName) => $"extension:{fileName}";
}
