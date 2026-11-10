using System.Security.Cryptography;

namespace SecureChat.Security;

/// <summary>
/// Хэширование паролей: BCrypt (cost 12). Легаси-хэши PBKDF2 (10 000 итераций)
/// проверяются старым алгоритмом и прозрачно обновляются при успешном входе.
/// </summary>
public static class PasswordHasher
{
    private const int BcryptWorkFactor = 12;

    public static string Hash(string password) =>
        BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);

    public static bool Verify(string password, string storedHash) =>
        BCrypt.Net.BCrypt.Verify(password, storedHash);

    /// <summary>Легаси-хэши начинаются с "$2" (BCrypt); всё остальное считаем PBKDF2.</summary>
    public static bool IsLegacyHash(string storedHash) =>
        string.IsNullOrEmpty(storedHash) || !storedHash.StartsWith("$2", StringComparison.Ordinal);

    /// <summary>Проверка пароля против легаси-PBKDF2 (16-байтовая соль, SHA-256).</summary>
    public static bool VerifyLegacy(string password, string storedHash, string storedSalt)
    {
        try
        {
            byte[] saltBytes = Convert.FromBase64String(storedSalt);
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000, HashAlgorithmName.SHA256);
            byte[] hashBytes = pbkdf2.GetBytes(32);
            return Convert.ToBase64String(hashBytes) == storedHash;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Единая точка проверки: BCrypt напрямую, легаси — с последующим обновлением хэша
    /// (out newHash != null означает, что хэш в БД нужно заменить на BCrypt).
    /// </summary>
    public static bool VerifyAny(string password, string storedHash, string storedSalt, out string? newHash)
    {
        newHash = null;
        if (IsLegacyHash(storedHash))
        {
            if (!VerifyLegacy(password, storedHash, storedSalt)) return false;
            newHash = Hash(password);
            return true;
        }
        return Verify(password, storedHash);
    }
}
