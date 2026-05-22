using System.Security.Cryptography;
using System.Text;

namespace Watashi.Shared.Helpers;

public static class CryptoHelper
{
    public static byte[] Encrypt(string plaintext, byte[] masterKey)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plainBytes.Length];
        using var aes = new AesGcm(masterKey, 16);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var output = new byte[nonce.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, output, nonce.Length + tag.Length, cipher.Length);
        return output;
    }

    public static string Decrypt(byte[] data, byte[] masterKey)
    {
        var nonce = data[..12];
        var tag = data[12..28];
        var cipher = data[28..];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(masterKey, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }
}
