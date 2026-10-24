using System;
using System.IO;
using System.Threading.Tasks;
using SecureChat.Common;

namespace SecureChat.Client.Services;

// Отправка и приём файлов с симметричным шифрованием (AES-GCM)
public class FileTransferManager
{
    private readonly CryptoService _crypto;    // Шифрование/дешифрование
    private readonly NetworkService _network;  // Отправка по WebSocket

    public FileTransferManager(CryptoService crypto, NetworkService network)
    {
        _crypto = crypto;
        _network = network;
    }

    // Читает файл, шифрует сессионным ключом и отправляет одним сообщением
    public async Task<string> SendFileAsync(int receiverId, string filePath, byte[] sessionKey, string? encKeyBase64, string? encKeySelfBase64)
    {
        byte[] fileData = File.ReadAllBytes(filePath);
        var (ciphertext, nonce, tag) = _crypto.AesGcmEncrypt(sessionKey, fileData);

        string transferId = Guid.NewGuid().ToString();   // Идентификатор передачи
        var msgId = Guid.NewGuid().ToString();           // Идентификатор сообщения
        await _network.SendAsync(new
        {
            Type = "SendMessage",
            Payload = new SendMessagePayload
            {
                ReceiverId = receiverId,
                MessageId = msgId,
                EncryptedKey = encKeyBase64,          // Сессионный ключ для получателя (RSA)
                EncryptedKeySelf = encKeySelfBase64,  // Сессионный ключ для себя (RSA)
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
        return msgId;
    }

    // Расшифровывает входящий файл и сохраняет в папку Downloads
    public void HandleIncomingFile(IncomingMessagePayload msg, byte[] sessionKey, Action<string> onComplete)
    {
        if (msg.ContentType != "File" || msg.EncryptedData == null) return;
        byte[] nonce = Convert.FromBase64String(msg.EncryptedData.Nonce);
        byte[] cipher = Convert.FromBase64String(msg.EncryptedData.Ciphertext);
        byte[] tag = Convert.FromBase64String(msg.EncryptedData.Tag);
        byte[] plain = _crypto.AesGcmDecrypt(sessionKey, nonce, cipher, tag);  // Расшифровка

        // Каталог сохранения: %UserProfile%\Downloads
        string downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "\\Downloads\\";
        if (!Directory.Exists(downloads)) Directory.CreateDirectory(downloads);
        string path = Path.Combine(downloads, msg.FileName ?? "received_file");
        File.WriteAllBytes(path, plain);
        onComplete?.Invoke(path);   // Колбэк с путём к сохранённому файлу
    }
}