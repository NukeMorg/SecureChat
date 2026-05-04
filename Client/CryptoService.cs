using System.Security.Cryptography;
using System.Text;

namespace SecureChat.Client;

public class CryptoService
{
    public (string publicKey, string privateKey) GenerateRsaKeyPair()
    {
        using var rsa = RSA.Create(2048);
        string pub = rsa.ToXmlString(false);   // для передачи используем PEM? Для простоты используем XML, потом преобразуем.
        string priv = rsa.ToXmlString(true);
        // Конвертируем в PEM для совместимости? Оставим XML.
        return (pub, priv);
    }

    public byte[] RsaEncrypt(string publicKeyXml, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(publicKeyXml);
        return rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    public byte[] RsaDecrypt(string privateKeyXml, byte[] data)
    {
        using var rsa = RSA.Create();
        rsa.FromXmlString(privateKeyXml);
        return rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
    }

    public (byte[] ciphertext, byte[] nonce, byte[] tag) AesGcmEncrypt(byte[] key, byte[] plaintext)
    {
        byte[] nonce = new byte[12]; // 96-bit nonce for GCM
        RandomNumberGenerator.Fill(nonce);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[16]; // 128-bit authentication tag

        using var aes = new AesGcm(key);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return (ciphertext, nonce, tag);
    }

    public byte[] AesGcmDecrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] tag)
    {
        byte[] plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public byte[] GenerateSessionKey()
    {
        byte[] key = new byte[32]; // 256-bit
        RandomNumberGenerator.Fill(key);
        return key;
    }
}