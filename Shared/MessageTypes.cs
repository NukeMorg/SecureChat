using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecureChat.Common;

// Базовое сообщение, приходящее/уходящее от сервера. Содержит тип и динамическое поле Payload.
public class BaseMessage
{
    [JsonPropertyName("Type")]
    public string Type { get; set; }

    [JsonPropertyName("Payload")]
    public System.Text.Json.JsonElement Payload { get; set; }
}

// Данные для регистрации клиента.
public class RegisterPayload
{
    [JsonPropertyName("PublicKey")]
    public string PublicKey { get; set; }
}

// Ответ на регистрацию: статус и назначенный ID.
public class RegisterResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; }
    [JsonPropertyName("AssignedId")] public int AssignedId { get; set; }
}

// Запрос на вход (Hello) с указанием уже зарегистрированного ClientId.
public class HelloPayload
{
    [JsonPropertyName("ClientId")] public int ClientId { get; set; }
}

// Запрос публичного ключа другого пользователя.
public class GetPublicKeyPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

// Ответ с публичным ключом целевого пользователя.
public class PublicKeyResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; }
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("PublicKey")] public string PublicKey { get; set; }
}

// Запрос информации о пользователе (имя).
public class GetUserInfoPayload
{
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
}

// Ответ с информацией о пользователе.
public class UserInfoResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; }
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; }
}

// Сообщение, отправляемое другому пользователю.
// Содержит зашифрованные данные и метаинформацию (тип, файл и т.д.).
public class SendMessagePayload
{
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; }
    // Зашифрованный сессионный ключ для получателя.
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }
    // Зашифрованный сессионный ключ для отправителя (чтобы читать свою историю).
    [JsonPropertyName("EncryptedKeySelf")] public string? EncryptedKeySelf { get; set; } // === ИЗМЕНЕНИЕ ===
    [JsonPropertyName("EncryptedData")] public EncryptedData EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; }
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

// Структура зашифрованных данных (nonce, ciphertext, tag для AES-GCM).
public class EncryptedData
{
    [JsonPropertyName("Nonce")] public string Nonce { get; set; }
    [JsonPropertyName("Ciphertext")] public string Ciphertext { get; set; }
    [JsonPropertyName("Tag")] public string Tag { get; set; }
}

// Входящее сообщение (доставляется получателю).
public class IncomingMessagePayload
{
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; }
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedData")] public EncryptedData EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; }
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

// Подтверждение получения сообщения.
public class AckPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; }
    [JsonPropertyName("Status")] public string Status { get; set; }
}

// Статус доставки сообщения (например, "Sent", "Delivered").
public class DeliveryStatusPayload
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; }
    [JsonPropertyName("Status")] public string Status { get; set; }
}

// Фрагмент файла (в текущей версии не используется — файлы передаются одним сообщением).
public class FileChunkPayload
{
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("FileTransferId")] public string FileTransferId { get; set; }
    [JsonPropertyName("ChunkIndex")] public int ChunkIndex { get; set; }
    [JsonPropertyName("TotalChunks")] public int TotalChunks { get; set; }
    [JsonPropertyName("Offset")] public long Offset { get; set; }
    [JsonPropertyName("Length")] public int Length { get; set; }
    [JsonPropertyName("Data")] public string Data { get; set; }
}

// Запрос недостающих частей файла (не используется).
public class RequestChunkPayload
{
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("FileTransferId")] public string FileTransferId { get; set; }
    [JsonPropertyName("MissingChunks")] public List<int> MissingChunks { get; set; }
}

// Простой статус (часто для ошибок).
public class SimpleStatusPayload
{
    [JsonPropertyName("Status")] public string Status { get; set; }
}

// Информация о пользователе в списке контактов.
public class UserInfo
{
    [JsonPropertyName("Id")] public int Id { get; set; }
    [JsonPropertyName("Name")] public string Name { get; set; }
    [JsonPropertyName("IsOnline")] public bool IsOnline { get; set; } // <-- новое: индикатор онлайна
}

// Список пользователей (контакты).
public class UserListPayload
{
    [JsonPropertyName("Users")] public List<UserInfo> Users { get; set; }
}

// Запрос на изменение имени профиля.
public class UpdateProfilePayload
{
    [JsonPropertyName("UserName")] public string UserName { get; set; }
}

// Уведомление всем об изменении профиля пользователя.
public class UserProfileUpdatedPayload
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; }
}

// ── Типы для истории и контактов ──

// Запрос истории сообщений с указанным контактом.
public class GetHistoryRequestPayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
}

// Одно сообщение в истории.
public class HistoryMessage
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; }
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("ReceiverId")] public int ReceiverId { get; set; }
    [JsonPropertyName("EncryptedKey")] public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedKeySelf")] public string? EncryptedKeySelf { get; set; } // === ИЗМЕНЕНИЕ ===
    [JsonPropertyName("Nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("Ciphertext")] public string? Ciphertext { get; set; }
    [JsonPropertyName("Tag")] public string? Tag { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; }
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; }
    [JsonPropertyName("Status")] public string Status { get; set; }
}

// Ответ с историей сообщений для контакта.
public class HistoryResponsePayload
{
    [JsonPropertyName("ContactId")] public int ContactId { get; set; }
    [JsonPropertyName("Messages")] public List<HistoryMessage> Messages { get; set; }
}

// Список контактов (от сервера).
public class ContactListPayload
{
    [JsonPropertyName("Contacts")] public List<UserInfo> Contacts { get; set; }
}

// Запрос на добавление контакта по ID.
public class AddContactPayload
{
    public int ContactId { get; set; }
}

// Ответ на добавление контакта.
public class AddContactResponsePayload
{
    public string Status { get; set; }
    public int ContactId { get; set; }
}

// Уведомление об изменении онлайн-статуса пользователя.
public class OnlineStatusPayload
{
    [JsonPropertyName("UserId")]
    public int UserId { get; set; }
}