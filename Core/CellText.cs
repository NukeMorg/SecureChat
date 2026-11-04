namespace SecureChat.Core;

/// <summary>
/// Защита от formula injection в Excel (требование 1.5.1): текст, начинающийся
/// с =, +, -, @, \t, \r, выписывается с префиксом ' — Excel трактует его как строку.
/// </summary>
public static class CellText
{
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + text
            : text;
    }
}

/// <summary>Санитизация имени файла: без пути, управляющих символов и зарезервированных знаков.</summary>
public static class PathSanitizer
{
    public static string SanitizeFileName(string? name, string fallback = "export")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length > 100) cleaned = cleaned[..100];
        return cleaned.Length == 0 ? fallback : cleaned;
    }
}
