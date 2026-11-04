using System.Text.RegularExpressions;

namespace SecureChat.Core;

/// <summary>
/// Серверная валидация входных данных (требования 1.1.1, 1.4.2).
/// Клиентская валидация — только UX; сервер проверяет всё сам.
/// Регулярные выражения — с RegexOptions.NonBacktracking (защита от ReDoS).
/// </summary>
public static partial class Validators
{
    /// <summary>Корпоративная почта: локальная часть + домен company.com.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._%+-]+@company\.com$", RegexOptions.NonBacktracking)]
    private static partial Regex EmailRegex();

    private const int MaxEmailLength = 254;
    private const int MinUserNameLength = 1;
    private const int MaxUserNameLength = 100;
    private const int MinPasswordLength = 8;
    private const int MaxMessageLength = 4000;

    public static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= MaxEmailLength
        && EmailRegex().IsMatch(email);

    /// <summary>Пароль: минимум 8 символов, хотя бы 1 буква и 1 цифра (требование 1.1.1).</summary>
    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrEmpty(password)
        && password.Length >= MinPasswordLength
        && password.Any(char.IsLetter)
        && password.Any(char.IsDigit);

    /// <summary>Имя пользователя: 1–100 символов, без запрещённых управляющих символов.</summary>
    public static bool IsValidUserName(string? userName) =>
        !string.IsNullOrWhiteSpace(userName)
        && userName.Length is >= MinUserNameLength and <= MaxUserNameLength
        && !HasControlCharacters(userName);

    /// <summary>
    /// Управляющие символы запрещены, кроме \n и \t (требование 1.4.2).
    /// </summary>
    public static bool HasControlCharacters(string text)
    {
        foreach (char c in text)
        {
            if (char.IsControl(c) && c != '\n' && c != '\t') return true;
        }
        return false;
    }

    /// <summary>Текст сообщения: 1–4000 символов, без запрещённых управляющих символов.</summary>
    public static bool IsValidMessageText(string? text) =>
        !string.IsNullOrEmpty(text)
        && text.Length <= MaxMessageLength
        && !HasControlCharacters(text);
}
