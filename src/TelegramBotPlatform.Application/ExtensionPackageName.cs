namespace TelegramBotPlatform.Application;

/// <summary>
/// Reduces a client-supplied extension package name to a safe, plain file name. Stricter than
/// <see cref="Path.GetFileName(string)"/> alone: an object-store key is just a string, so a stray
/// separator would silently nest the package under a prefix the access policy does not cover, and odd
/// characters complicate the prefix scoping that policy relies on. Every real assembly file name passes.
/// </summary>
public static class ExtensionPackageName
{
    private const string DllExtension = ".dll";

    /// <summary>Validated name, or a failure explaining why it was rejected. Never throws.</summary>
    public static Result<string> Validate(string? suppliedName)
    {
        if (string.IsNullOrWhiteSpace(suppliedName))
        {
            return new Error("A behavior extension package name is required.");
        }

        // Strip any path segments first, so a traversal attempt is reduced rather than reasoned about.
        var fileName = Path.GetFileName(suppliedName.Trim());

        if (string.IsNullOrEmpty(fileName))
        {
            return new Error($"\"{suppliedName}\" is not a valid behavior extension package name.");
        }

        if (!fileName.EndsWith(DllExtension, StringComparison.OrdinalIgnoreCase))
        {
            return new Error($"\"{fileName}\" is not a behavior extension package — the name must end in \"{DllExtension}\".");
        }

        if (fileName.Contains("..", StringComparison.Ordinal) || !fileName.All(IsAllowed))
        {
            return new Error(
                $"\"{fileName}\" is not a valid behavior extension package name — use letters, digits, "
                + "'.', '_' and '-' only.");
        }

        return Result.Ok(fileName);
    }

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
}