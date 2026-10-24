using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecureChat.Common;

// Базовый конверт любого WS-сообщения: тип + произвольный payload
public class BaseMessage
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("Payload")]
    public System.Text.Json.JsonElement Payload { get; set; }
}

// Запрос входа (логин + пароль)
public class LoginPayload
{
    [JsonPropertyName("Login")] public string Login { get; set; } = "";
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
}

// Ответ на авторизацию
public class LoginResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "InvalidCredentials", "UserNotFound"
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("Token")] public string Token { get; set; } = "";
}

// Регистрация нового пользователя
public class RegisterAccountPayload
{
    [JsonPropertyName("Login")] public string Login { get; set; } = "";
    [JsonPropertyName("Password")] public string Password { get; set; } = "";
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("PublicKey")] public string PublicKey { get; set; } = ""; // RSA-ключ в PEM
}

// Ответ на регистрацию
public class RegisterAccountResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "UserExists", "Error"
    [JsonPropertyName("AssignedId")] public int AssignedId { get; set; }  // Выданный сервером ID
    [JsonPropertyName("Token")] public string Token { get; set; } = "";
}

// Приветствие (переавторизация по токену)
public class HelloPayload
{
    [JsonPropertyName("ClientId")] public int ClientId { get; set; }
    [JsonPropertyName("Token")] public string? Token { get; set; }
}

public class HelloResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

// Запрос публичного ключа собеседника
public class GetPublicKeyPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

public class PublicKeyResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("PublicKey")] public string PublicKey { get; set; } = "";
}

// Запрос информации о пользователе
public class GetUserInfoPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

public class UserInfoResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

// Шифртекст AES-256-GCM: nonce + данные + тег
public class EncryptedData
{
    [JsonPropertyName("Nonce")] public string Nonce { get; set; } = "";
    [JsonPropertyName("Ciphertext")] public string Ciphertext { get; set; } = "";
    [JsonPropertyName("Tag")] public string Tag { get; set; } = "";
}

// Отправка зашифрованного текста или файла
public class SendMessagePayload
{
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }        // Ключ сессии для получателя
    [JsonPropertyName("EncryptedKeySelf")] public string? EncryptedKeySelf { get; set; } // Ключ сессии для себя
    [JsonPropertyName("EncryptedData")] public EncryptedData? EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text"; // "Text" или "File"
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

// Входящее сообщение от сервера
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

// Подтверждение получения сообщения
public class AckPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

// Статус доставки ("Sent", "Delivered", "UserOffline")
public class DeliveryStatusPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}

// Контакт в списке контактов
public class UserInfo
{
    [JsonPropertyName("Id")] public int Id { get; set; }
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("IsOnline")] public bool IsOnline { get; set; }
}

public class ContactListPayload
{
    [JsonPropertyName("Contacts")] public List<UserInfo> Contacts { get; set; } = new();
}

// Добавление контакта по ID
public class AddContactPayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

public class AddContactResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

// Уведомление об онлайн-статусе пользователя
public class OnlineStatusPayload
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

// Смена собственного имени
public class UpdateProfilePayload
{
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

public class UserProfileUpdatedPayload
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
}

// Запрос истории переписки с контактом
public class GetHistoryRequestPayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

// Одно сообщение из истории
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

public class HistoryResponsePayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
    [JsonPropertyName("Messages")] public List<HistoryMessage> Messages { get; set; } = new();
}

// Универсальный ответ со статусом
public class SimpleStatusPayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = "";
}