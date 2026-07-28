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

    [Theory]
    [InlineData("../../etc/passwd.dll", "passwd.dll")]
    [InlineData("plugins/Reverse.dll", "Reverse.dll")]
    [InlineData(@"C:\evil\Reverse.dll", "Reverse.dll")]
    [InlineData("  Reverse.dll  ", "Reverse.dll")]
    public void Validate_StripsPathSegments_LeavingAPlainFileName(string suppliedName, string expected)
    {
        var result = ExtensionPackageName.Validate(suppliedName);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
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
}