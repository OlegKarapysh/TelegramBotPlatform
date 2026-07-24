namespace TelegramBotPlatform.Public;

/// <summary>Encrypts/decrypts bot tokens at rest — backed by ASP.NET Core Data Protection.</summary>
public interface ITokenProtector
{
    byte[] Protect(string plaintextToken);

    string Unprotect(byte[] protectedToken);
}