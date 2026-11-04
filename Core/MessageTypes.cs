using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecureChat.Core;

/// <summary>
/// Базовый конверт сообщения: тип + произвольный payload (JSON).
/// Используется только в legacy-режиме сырого WebSocket и удаляется после миграции на SignalR.
/// </summary>
public class BaseMessage
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("Payload")]
    public System.Text.Json.JsonElement Payload { get; set; }
}

/// <summary>
/// Запрос отправки кода подтверждения на Email.
/// </summary>
public class SendEmailCodePayload
{
    [JsonPropertyName("Email")] public string Email { get; set; } = "";
    [JsonPropertyName("Purpose")] public string Purpose { get; set; } = "Register"; // "Register" или "Login"
}

/// <summary>
/// Ответ на запрос отправки кода.
/// </summary>
public class SendEmailCodeResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "InvalidEmail", "RateLimit", "SendFailed"
    [JsonPropertyName("Message")] public string Message { get; set; } = "";
}

/// <summary>
/// Запрос авторизации по email/паролю.
/// </summary>
public class LoginPayload
{
    [JsonPropertyName("Email")] public string Email { get; set; } = "";
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
}

/// <summary>
/// Ответ на авторизацию.
/// </summary>
public class LoginResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "InvalidCredentials", "RateLimited", "Banned"
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("Token")] public string Token { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "Employee";
}

/// <summary>
/// Регистрация с обязательным подтверждением кодом из письма.
/// </summary>
public class RegisterAccountPayload
{
    [JsonPropertyName("Email")] public string Email { get; set; } = "";
    [JsonPropertyName("Code")] public string Code { get; set; } = "";       // код из письма
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("PublicKey")] public string PublicKey { get; set; } = "";
}

/// <summary>
/// Ответ на регистрацию.
/// </summary>
public class RegisterAccountResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "InvalidCode", "CodeExpired", "UserExists", "InvalidEmail", "WeakPassword", "InvalidUserName", "Error"
    [JsonPropertyName("AssignedId")] public int AssignedId { get; set; }
    [JsonPropertyName("Token")] public string Token { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "Employee";
}

/// <summary>
/// Приветствие после подключения (идентификация клиента).
/// </summary>
public class HelloPayload
{
    [JsonPropertyName("ClientId")] public int ClientId { get; set; }
    [JsonPropertyName("Token")] public string? Token { get; set; }
}

/// <summary>
/// Ответ на Hello.
/// </summary>
public class HelloResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "Employee";
}

/// <summary>
/// Запрос публичного ключа пользователя.
/// </summary>
public class GetPublicKeyPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

/// <summary>
/// Ответ с публичным ключом.
/// </summary>
public class PublicKeyResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("PublicKey")] public string PublicKey { get; set; } = "";
}

/// <summary>
/// Запрос информации о пользователе.
/// </summary>
public class GetUserInfoPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

/// <summary>
/// Ответ с информацией о пользователе.
/// </summary>
public class UserInfoResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

/// <summary>
/// Зашифрованные данные (AES-GCM: nonce + ciphertext + tag).
/// </summary>
public class EncryptedData
{
    [JsonPropertyName("Nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("Ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("Tag")] public string Tag { get; set; } = "";
}

/// <summary>
/// Исходящее сообщение (текст или файл).
/// </summary>
public class SendMessagePayload
{
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }         // ключ для получателя
    [JsonPropertyName("EncryptedKeySelf")] public string? EncryptedKeySelf { get; set; } // ключ для себя
    [JsonPropertyName("EncryptedData")] public EncryptedData? EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";  // "Text" | "File"
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

/// <summary>
/// Входящее сообщение от другого пользователя.
/// </summary>
public class IncomingMessagePayload
{
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedData")] public EncryptedData? EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

/// <summary>
/// Подтверждение доставки/прочтения сообщения.
/// </summary>
public class AckPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

/// <summary>
/// Уведомление о смене статуса доставки.
/// </summary>
public class DeliveryStatusPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

/// <summary>
/// Краткая информация о пользователе для списка контактов.
/// </summary>
public class UserInfo
{
    [JsonPropertyName("Id")] public int Id { get; set; }
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("IsOnline")] public bool IsOnline { get; set; }
}

/// <summary>
/// Список контактов.
/// </summary>
public class ContactListPayload
{
    [JsonPropertyName("Contacts")] public List<UserInfo> Contacts { get; set; } = new();
}

/// <summary>
/// Запрос на добавление контакта.
/// </summary>
public class AddContactPayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

/// <summary>
/// Ответ на добавление контакта.
/// </summary>
public class AddContactResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

/// <summary>
/// Уведомление об изменении онлайн-статуса пользователя.
/// </summary>
public class OnlineStatusPayload
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

/// <summary>
/// Запрос на обновление профиля.
/// </summary>
public class UpdateProfilePayload
{
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

/// <summary>
/// Уведомление об обновлении профиля пользователя.
/// </summary>
public class UserProfileUpdatedPayload
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

/// <summary>
/// Запрос истории переписки с контактом.
/// </summary>
public class GetHistoryRequestPayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

/// <summary>
/// Одно сообщение из истории (расшифровывается на клиенте).
/// </summary>
public class HistoryMessage
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedKeySelf")] public string? EncryptedKeySelf { get; set; }
    [JsonPropertyName("Nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("Ciphertext")] public string? Ciphertext { get; set; }
    [JsonPropertyName("Tag")] public string? Tag { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

/// <summary>
/// Ответ с историей сообщений.
/// </summary>
public class HistoryResponsePayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
    [JsonPropertyName("Messages")] public List<HistoryMessage> Messages { get; set; } = new();
}

/// <summary>
/// Универсальный ответ со статусом.
/// </summary>
public class SimpleStatusPayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

/// <summary>
/// Запись журнала аудита (требование 1.7.3). Action/Result — English-константы.
/// </summary>
public class AuditLogEntry
{
    [JsonPropertyName("Id")] public long Id { get; set; }
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("ActorId")] public int? ActorId { get; set; }
    [JsonPropertyName("Action")] public string Action { get; set; } = "";
    [JsonPropertyName("TargetId")] public int? TargetId { get; set; }
    [JsonPropertyName("Result")] public string Result { get; set; } = "";
    [JsonPropertyName("IpAddress")] public string IpAddress { get; set; } = "";
}

/// <summary>
/// Запрос блокировки пользователя (только Admin).
/// </summary>
public class BanUserPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("Reason")] public string Reason { get; set; } = "";
}

/// <summary>
/// Запрос разблокировки пользователя (только Admin).
/// </summary>
public class UnbanUserPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

/// <summary>
/// Запрос смены роли (только Admin): "Admin" | "HR" | "Employee".
/// </summary>
public class ChangeRolePayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("Role")] public string Role { get; set; } = "Employee";
}

/// <summary>
/// Уведомление о блокировке аккаунта (сервер → заблокированный клиент).
/// </summary>
public class BannedPayload
{
    [JsonPropertyName("Reason")] public string Reason { get; set; } = "";
}

/// <summary>
/// Смена пароля: текущий + новый. После успеха все сессии отзываются.
/// </summary>
public class ChangePasswordPayload
{
    [JsonPropertyName("CurrentPassword")] public string CurrentPassword { get; set; } = "";
    [JsonPropertyName("NewPassword")] public string NewPassword { get; set; } = "";
}

/// <summary>
/// Обновление статуса пользователя (≤ 100 символов, требование 1.1.4).
/// </summary>
public class UpdateStatusPayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

/// <summary>
/// Загрузка аватара: PNG (после клиентского re-encode), ≤ 2 МБ.
/// </summary>
public class UploadAvatarPayload
{
    [JsonPropertyName("DataBase64")] public string DataBase64 { get; set; } = "";
}

/// <summary>
/// Запрос профиля пользователя.
/// </summary>
public class GetUserProfilePayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

/// <summary>
/// Профиль пользователя: публичные данные (требование 1.1.4).
/// </summary>
public class UserProfileDto
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "Employee";
    [JsonPropertyName("IsOnline")] public bool IsOnline { get; set; }
    [JsonPropertyName("AvatarBase64")] public string? AvatarBase64 { get; set; }
}

/// <summary>
/// Запрос журнала аудита (только Admin): постранично, свежие сверху.
/// </summary>
public class GetAuditLogPayload
{
    [JsonPropertyName("Limit")] public int Limit { get; set; } = 200;
    [JsonPropertyName("Offset")] public int Offset { get; set; } = 0;
}

/// <summary>
/// Ответ с журналом аудита.
/// </summary>
public class AuditLogResponsePayload
{
    [JsonPropertyName("TotalCount")] public long TotalCount { get; set; }
    [JsonPropertyName("Entries")] public List<AuditLogEntry> Entries { get; set; } = new();
}
