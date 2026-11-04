using System;
using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Client.Services;

/// <summary>
/// Защита секретов (токен сессии, приватный ключ) через DPAPI (CurrentUser).
/// В config.json защищённые значения хранятся как "dpapi:" + Base64.
/// Открытые значения из старых конфигов читаются для обратной совместимости
/// и шифруются при следующем сохранении.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(plain);
        byte[] encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static bool IsProtected(string value) =>
        value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Расшифровка; открытые (легаси) значения возвращаются как есть.</summary>
    public static string? Unprotect(string value)
    {
        try
        {
            if (!IsProtected(value)) return value;
            byte[] bytes = Convert.FromBase64String(value[Prefix.Length..]);
            byte[] plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // другой профиль/машина Windows или повреждённое значение — секрет нечитаем
            return null;
        }
    }
}
