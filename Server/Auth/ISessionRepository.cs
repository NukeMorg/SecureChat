namespace SecureChat.Server.Auth;

/// <summary>
/// Репозиторий сессий (таблица Sessions, ключ — jti из JWT).
/// Валидация и ревокация принимают полный JWT: подпись проверяет IJwtTokenService,
/// состояние сессии и активность пользователя — хранилище.
/// </summary>
public interface ISessionRepository
{
    /// <summary>Создаёт таблицу сессий (вызывается после Database.Initialize).</summary>
    void Initialize();

    /// <summary>Сохраняет строку сессии по jti из выпущенного токена.</summary>
    void Create(int userId, IssuedToken issued);

    /// <summary>Полная валидация JWT: подпись/срок → существование строки, не истекла, юзер активен.</summary>
    int? Validate(string token);

    /// <summary>Logout: удаление строки сессии по jti токена.</summary>
    void Revoke(string token);

    /// <summary>Отзыв всех сессий пользователя (смена пароля, бан).</summary>
    void RevokeAllForUser(int userId);
}
