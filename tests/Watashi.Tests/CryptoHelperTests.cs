using System.Security.Cryptography;
using FluentAssertions;
using Watashi.Shared.Helpers;
using Xunit;

namespace Watashi.Tests;

public class CryptoHelperTests
{
    [Fact]
    public void Roundtrip_should_recover_plaintext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var plain = "Hello, Watashi! こんにちは 🚀";
        var cipher = CryptoHelper.Encrypt(plain, key);
        cipher.Length.Should().BeGreaterThan(28);
        CryptoHelper.Decrypt(cipher, key).Should().Be(plain);
    }

    [Fact]
    public void Encrypt_should_use_random_nonce()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var a = CryptoHelper.Encrypt("same", key);
        var b = CryptoHelper.Encrypt("same", key);
        a.Should().NotEqual(b);
    }

    [Fact]
    public void Decrypt_should_throw_on_tampered_ciphertext()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var cipher = CryptoHelper.Encrypt("payload", key);
        cipher[cipher.Length - 1] ^= 0xFF; // tamper auth tag area
        var act = () => CryptoHelper.Decrypt(cipher, key);
        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_should_throw_with_wrong_key()
    {
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        var cipher = CryptoHelper.Encrypt("payload", k1);
        var act = () => CryptoHelper.Decrypt(cipher, k2);
        act.Should().Throw<CryptographicException>();
    }
}
