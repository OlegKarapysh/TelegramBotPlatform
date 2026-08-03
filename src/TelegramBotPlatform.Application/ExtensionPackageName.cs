namespace TelegramBotPlatform.Application;

/// <summary>
/// Reduces a client-supplied extension package name to a safe, plain file name. Stricter than
/// <see cref="Path.GetFileName(string)"/> alone: an object-store key is just a string, so a stray
/// separator would silently nest the package under a prefix the access policy does not cover, and odd
/// characters complicate the prefix scoping that policy relies on. Every real assembly file name passes.
/// <para>
/// The result does not depend on the host OS. That matters because the name usually arrives from a
/// multipart <c>filename=</c> header, which a Windows client may fill with a full backslash path — and
/// <see cref="Path.GetFileName(string)"/> treats '\' as a separator only on Windows, so without the
/// normalisation below the same upload would be accepted on a developer's machine and rejected on the
/// Linux container.
/// </para>
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
        // Backslashes are folded to '/' beforehand so both separators are stripped on every platform.
        var fileName = Path.GetFileName(suppliedName.Trim().Replace('\\', '/'));

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

    /// <summary>
    /// Checks a name that came back <em>out</em> of a store, which is a different question from validating
    /// one on the way in: here the name must already be exactly what <see cref="Validate"/> would produce.
    /// <para>
    /// Normalising would be actively wrong at this end — the platform would go on to read, replace, and
    /// delete under the tidied name while the store still holds the original, so a package that looked
    /// restored would silently be a different object from the one on disk. Refusing to trust it instead
    /// leaves it visible in <c>GET /admin/behaviors</c> with a reason, and repairable by name.
    /// </para>
    /// </summary>
    public static Result<string> ValidateStored(string? storedName)
    {
        var validated = Validate(storedName);

        if (validated.IsSuccess && !string.Equals(validated.Value, storedName, StringComparison.Ordinal))
        {
            return new Error(
                $"\"{storedName}\" is not a usable behavior extension package name — the store holds it "
                + "under a name the platform cannot address.");
        }

        return validated;
    }

    private static bool IsAllowed(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
}