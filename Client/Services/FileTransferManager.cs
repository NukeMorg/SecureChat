using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using SecureChat.Core;
using Serilog;

namespace SecureChat.Client.Services;

public class FileTransferManager
{
    private readonly CryptoService _crypto;
    private readonly NetworkService _network;
    private static readonly ILogger Log = Serilog.Log.ForContext<FileTransferManager>();

    public FileTransferManager(CryptoService crypto, NetworkService network)
    {
        _crypto = crypto;
        _network = network;
    }

    /// <summary>Возвращает MessageId, отправленный на сервер (нужен для связи с DeliveryStatus).</summary>
    public async Task<string> SendFileAsync(int receiverId, string filePath, byte[] sessionKey,
        string? encKeyBase64, string? encKeySelfBase64)
    {
        var sw = Stopwatch.StartNew();

        byte[] fileData = File.ReadAllBytes(filePath);
        var (ciphertext, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, fileData);

        string transferId = Guid.NewGuid().ToString();
        var msgId = Guid.NewGuid().ToString();

        await _network.SendMessageAsync(new SendMessagePayload
        {
            ReceiverId = receiverId,
            MessageId = msgId,
            EncryptedKey = encKeyBase64,
            EncryptedKeySelf = encKeySelfBase64,
            EncryptedData = new EncryptedData
            {
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(ciphertext),
                Tag = Convert.ToBase64String(tag)
            },
            ContentType = "File",
            FileName = Path.GetFileName(filePath),
            FileSize = fileData.Length,
            FileTransferId = transferId
        });

        sw.Stop();
        var level = sw.ElapsedMilliseconds > 1000 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information;
        Log.Write(level, "File sent {FileName} ({Size}B) → userId={ReceiverId} in {DurationMs}ms messageId={MessageId}",
            Path.GetFileName(filePath), fileData.Length, receiverId, sw.ElapsedMilliseconds, msgId);

        return msgId;
    }

    public void HandleIncomingFile(IncomingMessagePayload msg, byte[] sessionKey, Action<string> onComplete)
    {
        if (msg.ContentType != "File" || msg.EncryptedData == null) return;

        var sw = Stopwatch.StartNew();
        byte[] nonce = Convert.FromBase64String(msg.EncryptedData.Nonce);
        byte[] cipher = Convert.FromBase64String(msg.EncryptedData.Ciphertext);
        byte[] tag = Convert.FromBase64String(msg.EncryptedData.Tag);
        byte[] plain = _crypto.AesGcmDecrypt(sessionKey, nonce, cipher, tag);

        string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads\\";
        if (!Directory.Exists(downloads)) Directory.CreateDirectory(downloads);
        string path = Path.Combine(downloads, msg.FileName ?? "received_file");
        File.WriteAllBytes(path, plain);

        sw.Stop();
        var level = sw.ElapsedMilliseconds > 1000 ? Serilog.Events.LogEventLevel.Warning : Serilog.Events.LogEventLevel.Information;
        Log.Write(level, "File received {FileName} ({Size}B) from userId={SenderId} in {DurationMs}ms messageId={MessageId}",
            msg.FileName, plain.Length, msg.SenderId, sw.ElapsedMilliseconds, msg.MessageId);

        onComplete?.Invoke(path);
    }
}