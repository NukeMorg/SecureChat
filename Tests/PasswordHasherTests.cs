using System.Security.Cryptography;
using SecureChat.Security;
using Xunit;

namespace SecureChat.Tests;

/// <summary>Негативные security-тесты хэширования паролей (BCrypt cost 12).</summary>
public class PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesBcryptHashWithCost12()
    {
        string hash = PasswordHasher.Hash("Passw0rd");
        Assert.StartsWith("$2", hash);
        Assert.Contains("$12$", hash); // work factor 12
    }

    [Fact]
    public void Verify_CorrectAndWrongPassword()
    {
        string hash = PasswordHasher.Hash("Passw0rd");
        Assert.True(PasswordHasher.Verify("Passw0rd", hash));
        Assert.False(PasswordHasher.Verify("Passw0rdX", hash));
        Assert.False(PasswordHasher.Verify("passw0rd", hash)); // регистр важен
    }

    [Fact]
    public void Hash_UsesUniqueSalts()
    {
        string h1 = PasswordHasher.Hash("SamePassword1");
        string h2 = PasswordHasher.Hash("SamePassword1");
        Assert.NotEqual(h1, h2); // соль уникальна
    }

    [Fact]
    public void VerifyLegacy_ChecksOldPbkdf2Hash()
    {
        // легаси-хэш: PBKDF2 10 000, SHA-256, 32 байта (старая схема БД)
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string saltB64 = Convert.ToBase64String(salt);
        using var pbkdf2 = new Rfc2898DeriveBytes("OldPass1", salt, 10000, HashAlgorithmName.SHA256);
        string hashB64 = Convert.ToBase64String(pbkdf2.GetBytes(32));

        Assert.True(PasswordHasher.VerifyLegacy("OldPass1", hashB64, saltB64));
        Assert.False(PasswordHasher.VerifyLegacy("WrongPass", hashB64, saltB64));
    }

    [Fact]
    public void VerifyAny_LegacyHash_UpgradesToBcrypt()
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        string saltB64 = Convert.ToBase64String(salt);
        using var pbkdf2 = new Rfc2898DeriveBytes("Legacy1a", salt, 10000, HashAlgorithmName.SHA256);
        string hashB64 = Convert.ToBase64String(pbkdf2.GetBytes(32));

        bool ok = PasswordHasher.VerifyAny("Legacy1a", hashB64, saltB64, out string? newHash);

        Assert.True(ok);
        Assert.NotNull(newHash);              // клиент должен записать новый BCrypt-хэш
        Assert.StartsWith("$2", newHash);     // и он — BCrypt
        Assert.True(PasswordHasher.Verify("Legacy1a", newHash));
    }

    [Fact]
    public void VerifyAny_BcryptHash_NoUpgrade()
    {
        string hash = PasswordHasher.Hash("Modern1b");
        bool ok = PasswordHasher.VerifyAny("Modern1b", hash, "", out string? newHash);

        Assert.True(ok);
        Assert.Null(newHash); // обновление не требуется
    }
}
