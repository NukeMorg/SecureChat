using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SecureChat.Common;

public class BaseMessage
{
    [JsonPropertyName("Type")]
    public string Type { get; set; }
    [JsonPropertyName("Payload")]
    public System.Text.Json.JsonElement Payload { get; set; }
}

public class RegisterPayload
{
    [JsonPropertyName("PublicKey")]
    public string PublicKey { get; set; }
}

public class RegisterResponsePayload
{
    [JsonPropertyName("Status")]
    public string Status { get; set; }
    [JsonPropertyName("AssignedId")]
    public int AssignedId { get; set; }
}

public class HelloPayload
{
    [JsonPropertyName("ClientId")]
    public int ClientId { get; set; }
}

public class GetPublicKeyPayload
{
    [JsonPropertyName("TargetId")]
    public int TargetId { get; set; }
}

public class PublicKeyResponsePayload
{
    [JsonPropertyName("Status")]
    public string Status { get; set; }
    [JsonPropertyName("TargetId")]
    public int TargetId { get; set; }
    [JsonPropertyName("PublicKey")]
    public string PublicKey { get; set; }
}

public class SendMessagePayload
{
    [JsonPropertyName("ReceiverId")]
    public int ReceiverId { get; set; }
    [JsonPropertyName("MessageId")]
    public string MessageId { get; set; }
    [JsonPropertyName("EncryptedKey")]
    public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedData")]
    public EncryptedData EncryptedData { get; set; }
    [JsonPropertyName("ContentType")]
    public string ContentType { get; set; }
    [JsonPropertyName("FileName")]
    public string? FileName { get; set; }
    [JsonPropertyName("FileSize")]
    public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")]
    public string? FileTransferId { get; set; }
}

public class EncryptedData
{
    [JsonPropertyName("Nonce")]
    public string Nonce { get; set; }
    [JsonPropertyName("Ciphertext")]
    public string Ciphertext { get; set; }
    [JsonPropertyName("Tag")]
    public string Tag { get; set; }
}

public class IncomingMessagePayload
{
    [JsonPropertyName("SenderId")]
    public int SenderId { get; set; }
    [JsonPropertyName("MessageId")]
    public string MessageId { get; set; }
    [JsonPropertyName("EncryptedKey")]
    public string? EncryptedKey { get; set; }
    [JsonPropertyName("EncryptedData")]
    public EncryptedData EncryptedData { get; set; }
    [JsonPropertyName("ContentType")]
    public string ContentType { get; set; }
    [JsonPropertyName("FileName")]
    public string? FileName { get; set; }
    [JsonPropertyName("FileSize")]
    public long FileSize { get; set; }
    [JsonPropertyName("FileTransferId")]
    public string? FileTransferId { get; set; }
}

public class AckPayload
{
    [JsonPropertyName("MessageId")]
    public string MessageId { get; set; }
    [JsonPropertyName("Status")]
    public string Status { get; set; }
}

public class DeliveryStatusPayload
{
    [JsonPropertyName("MessageId")]
    public string MessageId { get; set; }
    [JsonPropertyName("Status")]
    public string Status { get; set; }
}

public class FileChunkPayload
{
    [JsonPropertyName("ReceiverId")]
    public int ReceiverId { get; set; }
    [JsonPropertyName("FileTransferId")]
    public string FileTransferId { get; set; }
    [JsonPropertyName("ChunkIndex")]
    public int ChunkIndex { get; set; }
    [JsonPropertyName("TotalChunks")]
    public int TotalChunks { get; set; }
    [JsonPropertyName("Offset")]
    public long Offset { get; set; }
    [JsonPropertyName("Length")]
    public int Length { get; set; }
    [JsonPropertyName("Data")]
    public string Data { get; set; }
}

public class RequestChunkPayload
{
    [JsonPropertyName("SenderId")]
    public int SenderId { get; set; }
    [JsonPropertyName("FileTransferId")]
    public string FileTransferId { get; set; }
    [JsonPropertyName("MissingChunks")]
    public List<int> MissingChunks { get; set; }
}

public class SimpleStatusPayload
{
    [JsonPropertyName("Status")]
    public string Status { get; set; }
}

public class UserListPayload
{
    [JsonPropertyName("Users")]
    public List<int> Users { get; set; }
}