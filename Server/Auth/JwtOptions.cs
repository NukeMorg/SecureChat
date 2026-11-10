namespace SecureChat.Server.Auth;

/// <summary>Настройки JWT (секция "Jwt"). Secret задаётся через user-secrets/переменные окружения.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "SecureChat.Server";
    public string Audience { get; set; } = "SecureChat.Client";
    /// <summary>Срок жизни сессии в днях (ранее — SessionLifetimeDays в Database).</summary>
    public int LifetimeDays { get; set; } = 7;
    /// <summary>Секрет подписи HS256 (минимум 32 символа). Пусто — встроенный секрет разработки.</summary>
    public string Secret { get; set; } = "";
}
