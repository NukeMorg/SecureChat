namespace SecureChat.Security;

/// <summary>
/// Маскирование PII в логах: email, телефон, токены.
/// Форматирование — culture-invariant, не зависит от локали и языка UI.
/// </summary>
public static class PiiMasker
{
    /// <summary>n***@domain — локальная часть скрывается.</summary>
    public static string Email(string email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        return email[0] + new string('*', Math.Min(at - 1, 3)) + email[at..];
    }

    /// <summary>Остаются только последние 2 цифры номера.</summary>
    public static string Phone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return "";
        string digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length < 4) return "***";
        return new string('*', Math.Max(3, digits.Length - 2)) + digits[^2..];
    }

    /// <summary>Первые 4 и последние 4 символа, середина скрыта.</summary>
    public static string Token(string token)
    {
        if (string.IsNullOrEmpty(token)) return "";
        if (token.Length <= 8) return "***";
        return token[..4] + "***" + token[^4..];
    }
}
