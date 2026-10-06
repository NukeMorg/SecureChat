using System;
using System.IO;
using System.Threading.Tasks;
using SecureChat.Common;

namespace SecureChat.Client.Services;

// Управление отправкой и получением файлов.
public class FileTransferManager
{
    private readonly CryptoService _crypto;
    private readonly NetworkService _network;

    public FileTransferManager(CryptoService crypto, NetworkService network)
    {
        _crypto = crypto;
        _network = network;
    }

    // Отправка файла: шифрует и отправляет как сообщение с ContentType "File".
    public async Task SendFileAsync(int receiverId, string filePath, byte[] sessionKey)
    {
        byte[] fileData = File.ReadAllBytes(filePath);
        var (ciphertext, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, fileData);

        string transferId = Guid.NewGuid().ToString();
        var msgId = Guid.NewGuid().ToString();
        await _network.SendAsync(new
        {
            Type = "SendMessage",
            Payload = new SendMessagePayload
            {
                ReceiverId = receiverId,
                MessageId = msgId,
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
            }
        });
    }

    // Обработка входящего файла: расшифровывает и сохраняет в папку Downloads.
    public void HandleIncomingFile(IncomingMessagePayload msg, byte[] sessionKey, Action<string> onComplete)
    {
        if (msg.ContentType != "File") return;
        byte[] nonce = Convert.FromBase64String(msg.EncryptedData.Nonce);
        byte[] cipher = Convert.FromBase64String(msg.EncryptedData.Ciphertext);
        byte[] tag = Convert.FromBase64String(msg.EncryptedData.Tag);
        byte[] plain = _crypto.AesGcmDecrypt(sessionKey, nonce, cipher, tag);

        string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads\\";
        string path = Path.Combine(downloads, msg.FileName ?? "received_file");
        File.WriteAllBytes(path, plain);
        onComplete?.Invoke(path);
    }
}