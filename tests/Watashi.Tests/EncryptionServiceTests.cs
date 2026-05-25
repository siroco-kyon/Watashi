using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Watashi.Server.Services;
using Xunit;

namespace Watashi.Tests;

public class EncryptionServiceTests
{
    private static IConfiguration BuildConfig(string? masterKey)
    {
        var dict = new Dictionary<string, string?> { ["Encryption:MasterKey"] = masterKey };
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Fact]
    public void Constructor_throws_when_master_key_missing()
    {
        // 環境変数経由で渡される可能性があるため、テスト中はクリアする。
        Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        Action act = () => new EncryptionService(BuildConfig(null));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*MasterKey*");
    }

    [Fact]
    public void Constructor_throws_when_master_key_not_base64()
    {
        Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        Action act = () => new EncryptionService(BuildConfig("not-base64-!"));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Base64*");
    }

    [Fact]
    public void Constructor_throws_when_master_key_wrong_length()
    {
        Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));   // 128bit
        Action act = () => new EncryptionService(BuildConfig(shortKey));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32*");
    }

    [Fact]
    public void Roundtrip_should_preserve_plaintext()
    {
        Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var svc = new EncryptionService(BuildConfig(key));
        var enc = svc.Encrypt("super-secret-password-123!");
        svc.Decrypt(enc).Should().Be("super-secret-password-123!");
    }

    [Fact]
    public void Decrypt_with_other_key_should_fail()
    {
        Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        var key1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var key2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var s1 = new EncryptionService(BuildConfig(key1));
        var s2 = new EncryptionService(BuildConfig(key2));
        var cipher = s1.Encrypt("secret");
        Action act = () => s2.Decrypt(cipher);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Env_var_overrides_configuration()
    {
        try
        {
            var envKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", envKey);
            var svc = new EncryptionService(BuildConfig(masterKey: null));
            // 例外なく構築できればよい
            svc.Encrypt("ok").Should().NotBeNullOrEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("WATASHI_MASTER_KEY", null);
        }
    }
}
