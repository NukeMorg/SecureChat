using System.Security.Cryptography;
using SecureChat.Security;
using Xunit;

namespace SecureChat.Tests;

public class PasswordHasherRobustnessTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("password", null, "invalid-base64-salt")]
    [InlineData("password", "corrupted-hash", "")]
    public void VerifyLegacy_WithCorruptedData_ReturnsFalseWithoutException(string password, string? hash, string salt)
    {
        bool result = PasswordHasher.VerifyLegacy(password, hash ?? "", salt);
        Assert.False(result);
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("password", "invalid-legacy-hash", "invalid-salt")]
    public void VerifyAny_WithCorruptedData_ReturnsFalseWithoutException(string password, string hash, string salt)
    {
        bool result = PasswordHasher.VerifyAny(password, hash, salt, out string? newHash);
        Assert.False(result);
        Assert.Null(newHash);
    }
}
