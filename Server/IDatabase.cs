using System;
using System.Collections.Generic;
using SecureChat.Core;

namespace SecureChat.Server;

/// <summary>Результат проверки кода подтверждения почты.</summary>
public enum CodeVerifyResult { Valid, Invalid, Expired }

/// <summary>
/// Контракт хранилища: учётные записи, сессии, сообщения, каналы, аудит.
/// Реализация — <see cref="Database"/> (SQLite).
/// </summary>
public interface IDatabase
{
    string ConnectionString { get; }
    void Initialize();

    // ---------- Подтверждение почты ----------
    void SaveVerificationCode(string email, string code, TimeSpan ttl);
    CodeVerifyResult VerifyAndConsumeCode(string email, string code);

    // ---------- Защита от перебора и лимиты писем ----------
    bool IsLoginBlocked(string email, string ipAddress, TimeSpan window, int maxAttempts);
    void RecordFailedLogin(string email, string ipAddress);
    bool TryReserveEmailSend(string email, TimeSpan window, int maxAttempts);

    // ---------- Учётные записи ----------
    /// <summary>Создаёт пользователя; null — почта занята. Сессию создаёт ChatHub через JWT.</summary>
    int? RegisterUser(string email, string password, string userName, string publicKey);
    /// <summary>Проверка пароля (с миграцией хэша); токен сессии выпускает ChatHub.</summary>
    (int id, string name)? Authenticate(string email, string password);

    // ---------- Сессии (строки в Sessions; JWT-обёртка — IJwtTokenService) ----------
    /// <summary>Сохраняет строку сессии по jti из выпущенного JWT.</summary>
    void CreateSession(int userId, string jti, DateTime expiresAtUtc);
    /// <summary>Проверка строки сессии по jti: не истекла, не отозвана, юзер активен.</summary>
    int? ValidateSession(string jti);
    void RevokeSession(string jti);
    void RevokeAllUserSessions(int userId);

    // ---------- Роли, профиль, блокировка ----------
    string GetUserRole(int userId);
    void SetUserRole(int userId, string role);
    bool IsUserActive(int userId);
    void SetUserActive(int userId, bool isActive);
    string GetUserStatus(int userId);
    void SetUserStatus(int userId, string status);
    void SetAvatarPath(int userId, string? path);
    string? GetAvatarPath(int userId);
    bool VerifyPasswordForUser(int userId, string password, out string? upgradeHash);
    void UpdatePassword(int userId, string newPassword);

    // ---------- Аудит ----------
    void WriteAudit(int? actorId, string action, int? targetId, string result, string ip, string userAgent = "");
    long GetAuditLogCount();
    List<AuditLogEntry> GetAuditLog(int limit, int offset);

    // ---------- Публичные данные пользователей ----------
    bool UserExists(int id);
    string? GetPublicKey(int id);
    void UpdateUserName(int id, string userName);
    string? GetUserName(int id);

    // ---------- Сообщения ----------
    void SaveMessage(string messageId, int senderId, int receiverId,
        string? encryptedKey, string? nonce, string? ciphertext, string? tag,
        string contentType, string? fileName, long fileSize, string? fileTransferId,
        string status = "Sent", string? encryptedKeySelf = null, string? replyToMessageId = null);
    void UpdateMessageStatus(string messageId, string status);
    (int senderId, int receiverId, string? channelId, string timestamp)? GetMessageInfo(string messageId);
    void UpdateMessageContent(string messageId, string nonce, string ciphertext, string tag);
    void SoftDeleteMessage(string messageId);
    int? GetSenderByMessageId(string messageId);
    (List<HistoryMessage> messages, bool hasMore) GetHistoryPage(
        int user1, int user2, string? beforeTimestamp, int limit);
    List<HistoryMessage> GetPendingMessages(int receiverId);

    // ---------- Контакты ----------
    void AddContact(int userId, int contactId);
    List<int> GetContactIds(int userId);

    // ---------- Каналы ----------
    string? CreateChannel(string name, int ownerId);
    bool ChannelExists(string channelId);
    string? GetChannelName(string channelId);
    int? GetChannelOwnerId(string channelId);
    bool IsChannelMember(string channelId, int userId);
    void AddChannelMemberToChannel(string channelId, int userId);
    void RemoveChannelMemberFromChannel(string channelId, int userId);
    List<string> GetUserChannelIds(int userId);
    List<ChannelDto> GetUserChannels(int userId);
    List<ChannelMemberDto> GetChannelMembers(string channelId);
    void SaveChannelMessage(string messageId, string channelId, int senderId,
        string? nonce, string? ciphertext, string? tag,
        string contentType, string? fileName, long fileSize, string? fileTransferId,
        long chainIteration, string? replyToMessageId = null);
    (List<ChannelMessageDto> messages, bool hasMore) GetChannelHistoryPage(
        string channelId, string? beforeTimestamp, int limit);
    void SaveChannelKey(string channelId, int userId, long version, string encryptedKey);
    (long version, string encryptedKey)? GetChannelKey(string channelId, int userId);
    List<string> DeleteUserFromAllChannels(int userId);
}
