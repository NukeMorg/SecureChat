using SecureChat.Client.Services;
using Xunit;

namespace SecureChat.Tests;

public class SecretProtectorTests
{
    [Theory]
    [InlineData("simple-secret")]
    [InlineData("Токен-сессии-12345")]
    [InlineData("VeryLongPrivateKeyPEMWithSpecialChars!@#$%^&*()_+{}[]:;<>,.?/|~`")]
    [InlineData("")]
    public void ProtectAndUnprotect_RoundTrip_RestoresOriginalString(string secret)
    {
        string protectedValue = SecretProtector.Protect(secret);
        Assert.True(SecretProtector.IsProtected(protectedValue));
        Assert.StartsWith("dpapi:", protectedValue);

        string? restored = SecretProtector.Unprotect(protectedValue);
        Assert.Equal(secret, restored);
    }

    [Fact]
    public void IsProtected_DetectsPrefix()
    {
        Assert.True(SecretProtector.IsProtected("dpapi:AAAA"));
        Assert.False(SecretProtector.IsProtected("plain-token"));
        Assert.False(SecretProtector.IsProtected(""));
    }

    [Fact]
    public void Unprotect_LegacyPlaintext_ReturnsAsIs()
    {
        string legacy = "my-old-plaintext-session-token";
        string? result = SecretProtector.Unprotect(legacy);
        Assert.Equal(legacy, result);
    }

    [Fact]
    public void Unprotect_InvalidBase64_ReturnsNull()
    {
        string corrupted = "dpapi:not-a-valid-base64-string!@#$";
        string? result = SecretProtector.Unprotect(corrupted);
        Assert.Null(result);
    }

    [Fact]
    public void Unprotect_CorruptedCiphertext_ReturnsNull()
    {
        // Валидный Base64, но случайные байты, не являющиеся валидным DPAPI-блоком
        string garbage = "dpapi:" + Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        string? result = SecretProtector.Unprotect(garbage);
        Assert.Null(result);
    }
}
