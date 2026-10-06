using System.Security.Cryptography;

namespace SecureChat.Client.Services;

// Криптографические операции: RSA, AES-GCM, генерация сессионного ключа.
public class CryptoService
{
    // Генерация пары RSA-2048 (публичный и приватный ключи в PEM).
    public (string publicKey, string privateKey) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        string pub = rsa.ExportRSAPublicKeyPem();
        string priv = rsa.ExportRSAPrivateKeyPem();
        return (pub, priv);
    }

    // Шифрование данных публичным RSA-ключом.
    public byte[] RsaEncrypt(string publicKeyPem, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(publicKeyPem);
        return rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    // Расшифровка приватным RSA-ключом.
    public byte[] RsaDecrypt(string privateKeyPem, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        return rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    // Шифрование AES-256-GCM. Возвращает ciphertext, nonce, tag.
    public (byte[] ciphertext, byte[] nonce, byte[] tag) AesGcmEncrypt(byte[] key, byte[] plaintext)
    {
        byte[] nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, nonce, tag);
    }

    // Расшифровка AES-256-GCM.
    public byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    // Генерация случайного 256-битного сессионного ключа.
    public byte[] GenerateSessionKey()
    {
        byte[] key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return key;
    }
}