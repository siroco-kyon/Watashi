using Watashi.Shared.Helpers;

namespace Watashi.Server.Services;

public class EncryptionService
{
    private readonly byte[] _masterKey;

    public EncryptionService(IConfiguration configuration)
    {
        var key = Environment.GetEnvironmentVariable("WATASHI_MASTER_KEY")
            ?? configuration["Encryption:MasterKey"]
            ?? throw new InvalidOperationException("Encryption:MasterKey (or WATASHI_MASTER_KEY env var) が設定されていません。");
        try
        {
            _masterKey = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Encryption:MasterKey は Base64 でエンコードされた値である必要があります。");
        }
        if (_masterKey.Length != 32)
            throw new InvalidOperationException($"Encryption:MasterKey は 32 バイト (256bit) である必要があります。現在: {_masterKey.Length} バイト");
    }

    public byte[] Encrypt(string plaintext) => CryptoHelper.Encrypt(plaintext, _masterKey);
    public string Decrypt(byte[] data) => CryptoHelper.Decrypt(data, _masterKey);
}
