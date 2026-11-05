using System.Security.Cryptography;
using UnGodot.Core.Crypto;
using Xunit;

namespace UnGodot.Tests;

public class CryptoTests
{
    [Fact]
    public void AesScheduleMatchesThePlatformImplementation()
    {
        var random = new Random(1234);
        for (int iteration = 0; iteration < 200; iteration++)
        {
            var key = new byte[32];
            var block = new byte[16];
            random.NextBytes(key);
            random.NextBytes(block);

            var schedule = new Aes256Schedule(key);
            var actual = new byte[16];
            schedule.EncryptBlock(block, actual);

            using var aes = Aes.Create();
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            byte[] expected = aes.EncryptEcb(block, PaddingMode.None);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void AesScheduleHandlesKnownVector()
    {
        // FIPS-197 Appendix C.3: AES-256 with key 000102...1f.
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] plain = Convert.FromHexString("00112233445566778899aabbccddeeff");
        byte[] expected = Convert.FromHexString("8ea2b7ca516745bfeafc49904b496089");

        var schedule = new Aes256Schedule(key);
        var actual = new byte[16];
        schedule.EncryptBlock(plain, actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CfbRoundTripsAndMatchesThePlatformCfb()
    {
        var random = new Random(42);
        var key = new byte[32];
        var iv = new byte[16];
        random.NextBytes(key);
        random.NextBytes(iv);

        for (int size = 0; size <= 97; size++)
        {
            var plain = new byte[size];
            random.NextBytes(plain);

            byte[] cipher = GodotCrypto.EncryptCfb(plain, key, iv);
            Assert.Equal(plain.Length, cipher.Length);
            Assert.Equal(plain, GodotCrypto.DecryptCfb(cipher, key, iv));

            if (size % 16 == 0)
            {
                using var aes = Aes.Create();
                aes.Key = key;
                aes.Mode = CipherMode.CFB;
                aes.FeedbackSize = 128;
                aes.Padding = PaddingMode.None;
                aes.IV = iv;
                using var encryptor = aes.CreateEncryptor();
                byte[] platform = encryptor.TransformFinalBlock(plain, 0, plain.Length);
                Assert.Equal(cipher, platform);
            }
        }
    }

    [Fact]
    public void PasswordDerivationMatchesGodot()
    {
        byte[] key = GodotCrypto.KeyFromPassword("password");
        Assert.Equal("5f4dcc3b5aa765d61d8327deb882cf99", System.Text.Encoding.ASCII.GetString(key));
    }

    [Fact]
    public void HexKeysRoundTrip()
    {
        var key = new byte[32];
        new Random(7).NextBytes(key);
        string hex = GodotCrypto.ToHex(key);
        Assert.Equal(64, hex.Length);
        Assert.True(GodotCrypto.TryParseHexKey(hex, out byte[] parsed));
        Assert.Equal(key, parsed);
        Assert.True(GodotCrypto.TryParseHexKey("0x" + hex.ToUpperInvariant(), out parsed));
        Assert.Equal(key, parsed);
        Assert.False(GodotCrypto.TryParseHexKey("deadbeef", out _));
    }

    [Fact]
    public void EncryptedBlobRoundTripsAndRejectsWrongKeys()
    {
        byte[] key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        byte[] iv = Enumerable.Repeat((byte)0x11, 16).ToArray();

        foreach (int size in new[] { 0, 1, 15, 16, 17, 1024, 4099 })
        {
            byte[] plain = new byte[size];
            new Random(size).NextBytes(plain);

            byte[] blob = EncryptedBlob.Seal(plain, key, iv);
            using var source = new Core.IO.MemorySource(blob);
            EncryptedBlob parsed = EncryptedBlob.Read(source, 0, out string? error)
                ?? throw new InvalidOperationException(error);

            Assert.Equal(size, parsed.PlaintextLength);
            Assert.Equal(plain, parsed.TryDecrypt(key));
            if (size > 0)
            {
                // An empty plaintext has no ciphertext to get wrong.
                Assert.Null(parsed.TryDecrypt(Enumerable.Repeat((byte)0x00, 32).ToArray()));
            }
        }
    }
}