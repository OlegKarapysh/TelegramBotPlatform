using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.UnitTests;

public sealed class ExtensionPackageNameTests
{
    [Theory]
    [InlineData("Reverse.dll")]
    [InlineData("My-Behavior_v2.dll")]
    [InlineData("Reminders.DLL")]
    public void Validate_Accepts_PlainAssemblyName(string suppliedName)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsSuccess);
        Assert.Equal(suppliedName, result.Value);
    }

    [Theory]
    [InlineData("../../etc/passwd.dll", "passwd.dll")]
    [InlineData("plugins/Reverse.dll", "Reverse.dll")]
    [InlineData(@"C:\evil\Reverse.dll", "Reverse.dll")]
    [InlineData(@"..\..\Reverse.dll", "Reverse.dll")]
    [InlineData(@"plugins\sub/Reverse.dll", "Reverse.dll")]
    [InlineData("  Reverse.dll  ", "Reverse.dll")]
    public void Validate_StripsPathSegments_LeavingAPlainFileName(string suppliedName, string expected)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("Reverse.dll")]
    [InlineData("../../etc/passwd.dll")]
    [InlineData("plugins/Reverse.dll")]
    [InlineData(@"C:\evil\Reverse.dll")]
    [InlineData(@"..\..\Reverse.dll")]
    [InlineData(@"\\server\share\Reverse.dll")]
    [InlineData(@"mixed/seps\Reverse.dll")]
    public void Validate_NeverReturnsAnythingPathLike(string suppliedName)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain('/', result.Value);
        Assert.DoesNotContain('\\', result.Value);
        Assert.EndsWith(".dll", result.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:Reverse.dll")]
    [InlineData("c:evil.dll")]
    [InlineData("D:My-Behavior_v2.dll")]
    public void Validate_Rejects_ADriveRelativeName(string suppliedName)
    {
        // Regression, and why the segment strip is no longer Path.GetFileName: on Windows that also
        // strips a drive-relative prefix, so "C:evil.dll" was accepted there as "evil.dll" and rejected on
        // Linux — the same upload passing locally and failing in CI.
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsFailed, $"\"{suppliedName}\" was accepted as \"{(result.IsSuccess ? result.Value : null)}\".");
    }

    [Theory]
    [InlineData("Reverse.dll")]
    [InlineData("plugins/Reverse.dll")]
    [InlineData(@"C:\evil\Reverse.dll")]
    [InlineData("C:Reverse.dll")]
    [InlineData("Reverse:stream.dll")]
    [InlineData("Rev erse.dll")]
    [InlineData("..foo.dll")]
    [InlineData("Reverse.exe")]
    [InlineData("")]
    public void Validate_ReachesTheSameVerdict_WhicheverOsItRunsOn(string suppliedName)
    {
        // The property behind the case above, stated for the whole surface: no input may be decided by a
        // rule only one platform applies. Re-deriving the verdict from an OS-blind strip is a second
        // opinion, and the two agreeing is what makes the OS-independence claim checked rather than stated.
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.Equal(OsBlindVerdict(suppliedName), result.IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_Rejects_EmptyName(string? suppliedName)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsFailed);
    }

    [Theory]
    [InlineData("Reverse.exe")]
    [InlineData("Reverse")]
    [InlineData("Reverse.dll.txt")]
    public void Validate_Rejects_NonAssemblyExtension(string suppliedName)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsFailed);
        Assert.Contains(".dll", result.Errors.First().Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Rev erse.dll")]
    [InlineData("Reverse$.dll")]
    [InlineData("Reverse:stream.dll")]
    [InlineData("Rev\u00e9rse.dll")]
    public void Validate_Rejects_DisallowedCharacters(string suppliedName)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsFailed);
    }

    [Fact]
    public void Validate_Rejects_TraversalThatSurvivesFileNameStripping()
    {
        // Stripping segments leaves "..foo.dll" intact; the explicit ".." check is what rejects it.
        var result = ExtensionPackageName.Validate("..foo.dll");

        Assert.True(result.IsFailed);
    }

    [Theory]
    [InlineData("Reverse.dll")]
    [InlineData("My-Behavior_v2.dll")]
    public void ValidateStored_Accepts_ANameAlreadyInCanonicalForm(string storedName)
    {
        var result = ExtensionPackageName.ValidateStored(storedName);

        Assert.True(result.IsSuccess);
        Assert.Equal(storedName, result.Value);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("nested/Reverse.dll")]
    [InlineData("  Reverse.dll  ")]
    [InlineData("Reverse.exe")]
    public void ValidateStored_Rejects_ANameItWouldHaveToRewrite(string storedName)
    {
        // Rewriting is right on the way in and wrong on the way out: the tidied name would no longer
        // address the object the store actually holds.
        var result = ExtensionPackageName.ValidateStored(storedName);

        Assert.True(result.IsFailed);
    }

    /// <summary>
    /// What <see cref="ExtensionPackageName.Validate"/> should answer, derived from a strip that hard-codes
    /// both separators and nothing else, so it cannot inherit the host's own path conventions.
    /// </summary>
    private static bool OsBlindVerdict(string suppliedName)
    {
        var trimmed = suppliedName.Trim();
        var fileName = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];

        return fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !fileName.Contains("..", StringComparison.Ordinal)
            && fileName.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}