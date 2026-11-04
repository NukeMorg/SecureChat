namespace SecureChat.Server;

/// <summary>
/// Маскирование PII для логов. Полноценный PiiMasker появится в ветке Security.
/// </summary>
internal static class PiiMasking
{
    internal static string Email(string email)
    {
        if (string.IsNullOrEmpty(email)) return "";
        int at = email.IndexOf('@');
        if (at <= 0) return "***";
        return email[0] + new string('*', Math.Min(at - 1, 3)) + email.Substring(at);
    }
}
