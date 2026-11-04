using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecureChat.Core;

/// <summary>
/// DTO-типы каналов (Фаза C). Сервер видит только шифртекст и метаданные:
/// GroupKey передаётся в зашифрованном виде через 1-1 RSA + AES-GCM.
/// </summary>

/// <summary>Создание канала (HR/Admin). Имя: 1–100 символов, уникально.</summary>
public class CreateChannelPayload
{
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
}

/// <summary>Ответ на создание канала.</summary>
public class CreateChannelResponsePayload
{
    [JsonPropertyName("Status")] public string Status { get; set; } = ""; // "Ok", "Forbidden", "InvalidName", "NameExists"
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
}

/// <summary>Канал в списке.</summary>
public class ChannelDto
{
    [JsonPropertyName("Id")] public string Id { get; set; } = "";
    [JsonPropertyName("Name")] public string Name { get; set; } = "";
    [JsonPropertyName("OwnerId")] public int OwnerId { get; set; }
    [JsonPropertyName("OwnerName")] public string OwnerName { get; set; } = "";
    [JsonPropertyName("MembersCount")] public int MembersCount { get; set; }
}

/// <summary>Список каналов пользователя.</summary>
public class ChannelListPayload
{
    [JsonPropertyName("Channels")] public List<ChannelDto> Channels { get; set; } = new();
}

/// <summary>Участник канала.</summary>
public class ChannelMemberDto
{
    [JsonPropertyName("UserId")] public int UserId { get; set; }
    [JsonPropertyName("UserName")] public string UserName { get; set; } = "";
    [JsonPropertyName("Role")] public string Role { get; set; } = "Member"; // "Owner" | "Member"
    [JsonPropertyName("IsOnline")] public bool IsOnline { get; set; }
}

/// <summary>Список участников канала.</summary>
public class ChannelMembersPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("Members")] public List<ChannelMemberDto> Members { get; set; } = new();
}

/// <summary>Добавление участника (HR/Admin). После успеха пригласивший выполняет ротацию GroupKey.</summary>
public class AddChannelMemberPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

/// <summary>Удаление участника (HR/Admin). После успеха выполняется ротация GroupKey.</summary>
public class RemoveChannelMemberPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

/// <summary>Вход/выход текущего соединения в SignalR-группу канала (проверяется членство).</summary>
public class JoinChannelPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
}

/// <summary>
/// Доставка GroupKey участнику через 1-1 E2E: ключ зашифрован RSA-публичным ключом получателя (TargetId).
/// Сервер сохраняет EncryptedKey для восстановления после перезапуска клиента и
/// ретранслирует получателю. Открытый ключ серверу недоступен.
/// </summary>
public class GroupKeyDeliveryPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("TargetId")] public int TargetId { get; set; }
    [JsonPropertyName("Version")] public long Version { get; set; }
    [JsonPropertyName("EncryptedKey")] public string EncryptedKey { get; set; } = "";
}

/// <summary>
/// Уведомление о ротации ключа канала: получатели сбрасывают ratchet-состояние
/// и ожидают GroupKeyDelivery новой версии через 1-1.
/// </summary>
public class ChannelKeyRotatedPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("Version")] public long Version { get; set; }
}

/// <summary>Событие: участник добавлен в канал.</summary>
public class ChannelMemberAddedPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

/// <summary>Событие: участник удалён из канала (в том числе при бане).</summary>
public class ChannelMemberRemovedPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("UserId")] public int UserId { get; set; }
}

/// <summary>Исходящее сообщение канала. Шифртекст — AES-256-GCM на MessageKey из ratchet.</summary>
public class SendChannelMessagePayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("ChainIteration")] public long ChainIteration { get; set; }
    [JsonPropertyName("EncryptedData")] public EncryptedData? EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")] public string? FileTransferId { get; set; }
}

/// <summary>Входящее сообщение канала.</summary>
public class IncomingChannelMessagePayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("ChainIteration")] public long ChainIteration { get; set; }
    [JsonPropertyName("EncryptedData")] public EncryptedData? EncryptedData { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; } = "";
}

/// <summary>Сообщение канала в истории (расшифровывается на клиенте).</summary>
public class ChannelMessageDto
{
    [JsonPropertyName("MessageId")] public string MessageId { get; set; } = "";
    [JsonPropertyName("SenderId")] public int SenderId { get; set; }
    [JsonPropertyName("ChainIteration")] public long ChainIteration { get; set; }
    [JsonPropertyName("Nonce")] public string? Nonce { get; set; }
    [JsonPropertyName("Ciphertext")] public string? Ciphertext { get; set; }
    [JsonPropertyName("Tag")] public string? Tag { get; set; }
    [JsonPropertyName("ContentType")] public string ContentType { get; set; } = "Text";
    [JsonPropertyName("FileName")] public string? FileName { get; set; }
    [JsonPropertyName("FileSize")] public long FileSize { get; set; }
    [JsonPropertyName("Timestamp")] public string Timestamp { get; set; } = "";
    [JsonPropertyName("IsDeleted")] public bool IsDeleted { get; set; }
}

/// <summary>Запрос истории канала: курсорная пагинация, до 50 сообщений старее BeforeTimestamp.</summary>
public class GetChannelHistoryPayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("BeforeTimestamp")] public string? BeforeTimestamp { get; set; }
    [JsonPropertyName("Limit")] public int Limit { get; set; } = 50;
}

/// <summary>Страница истории канала (отсортирована по возрастанию времени).</summary>
public class ChannelHistoryResponsePayload
{
    [JsonPropertyName("ChannelId")] public string ChannelId { get; set; } = "";
    [JsonPropertyName("Messages")] public List<ChannelMessageDto> Messages { get; set; } = new();
    [JsonPropertyName("HasMore")] public bool HasMore { get; set; }
}
