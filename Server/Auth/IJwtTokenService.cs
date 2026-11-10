namespace SecureChat.Server.Auth;

/// <summary>Результат выпуска JWT: сам токен + id сессии (jti) + срок.</summary>
public sealed record IssuedToken(string Token, string Jti, DateTime ExpiresAtUtc);

/// <summary>Данные из валидного JWT: владелец, сессия (jti), имя и роль (информационные).</summary>
public sealed record TokenInfo(int UserId, string Jti, string UserName, string Role);

/// <summary>Фасад над выпуском и валидацией JWT (HS256).</summary>
public interface IJwtTokenService
{
    /// <summary>Выпускает подписанный JWT сессии с новым jti.</summary>
    IssuedToken Issue(int userId, string userName, string role);

    /// <summary>Валидация подписи, issuer, срока жизни; null — токен недействителен.</summary>
    TokenInfo? Validate(string token);
}
