using TelegramBotPlatform.Application;

namespace TelegramBotPlatform.UnitTests;

public class ExtensionPackageNameTests
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

    // Backslash cases are the ones that matter here: Path.GetFileName treats '\' as a separator only on
    // Windows, so before these were normalised the same name was accepted on a developer's machine and
    // rejected on the Linux container — and the assertion below passed locally while failing in CI.
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
    public void Validate_NeverReturnsAnythingPathLike_OnAnyHostOs(string suppliedName)
    {
        // The OS-independence claim, asserted as a property rather than as expected values: whatever comes
        // back carries no separator of either flavour, so the result cannot depend on which of them the
        // host's Path.GetFileName happens to recognise (Windows honours both, Unix only '/').
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain('/', result.Value);
        Assert.DoesNotContain('\\', result.Value);
        Assert.EndsWith(".dll", result.Value, StringComparison.Ordinal);
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
        // GetFileName leaves "..foo.dll" intact — the explicit ".." check is what rejects it.
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
        // address the object the store actually holds, so the package must be refused, not renamed.
        var result = ExtensionPackageName.ValidateStored(storedName);

        Assert.True(result.IsFailed);
    }
}