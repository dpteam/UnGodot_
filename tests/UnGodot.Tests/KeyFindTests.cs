using UnGodot.Core.Crypto;
using UnGodot.Core.IO;
using UnGodot.Core.KeyFind;
using UnGodot.Core.Pck;
using Xunit;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Tests;

public class KeyFindTests
{
    private static byte[] EncryptedPack(out byte[] key)
    {
        key = new byte[32];
        new Random(11).NextBytes(key);
        return TestPacks.BuildV4(
            [new TestPacks.FileSpec("res://levels/mirraxis/mirraxis.tscn", "x"u8.ToArray())],
            key);
    }

    private static Oracle OracleFor(byte[] pack, PckHeader header)
    {
        using var source = new MemorySource(pack);
        return Oracle.TryCreate(source, header.DirOffset + 4, out string? error)
               ?? throw new InvalidOperationException(error);
    }

    [Fact]
    public void OracleAcceptsOnlyTheRightKey()
    {
        byte[] pack = EncryptedPack(out byte[] key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        Oracle oracle = OracleFor(pack, header!);

        Assert.True(oracle.Test(key, Tier.Strict) > 0);
        Assert.Equal(-1, oracle.Test(new byte[32], Tier.Utf8));
    }

    [Fact]
    public void FindsAKeyHiddenInABinary()
    {
        byte[] pack = EncryptedPack(out byte[] key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        Oracle oracle = OracleFor(pack, header!);

        var binary = new byte[65536];
        new Random(3).NextBytes(binary);
        key.CopyTo(binary, 1234);

        bool Verify(byte[] candidate)
        {
            try
            {
                _ = PckFile.Open(source, header!, candidate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        KeyHit? hit = KeyScanner.ScanVerified(
            new MemorySource(binary), oracle, [(0, binary.Length)], 1, Tier.Strict, prefilter: true, Verify, default);

        Assert.NotNull(hit);
        Assert.Equal(1234, hit!.Offset);
        Assert.Equal(key, hit.Key);
    }

    [Fact]
    public void FindsAKeyStraddlingAChunkBoundary()
    {
        byte[] pack = EncryptedPack(out byte[] key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        Oracle oracle = OracleFor(pack, header!);

        int offset = KeyScanner.ChunkSize - 16;
        var binary = new byte[KeyScanner.ChunkSize + 4096];
        key.CopyTo(binary, offset);

        bool Verify(byte[] candidate)
        {
            try
            {
                _ = PckFile.Open(source, header!, candidate);
                return true;
            }
            catch
            {
                return false;
            }
        }

        KeyHit? hit = KeyScanner.ScanVerified(
            new MemorySource(binary), oracle, [(0, binary.Length)], 1, Tier.Utf8, prefilter: false, Verify, default);

        Assert.NotNull(hit);
        Assert.Equal(offset, hit!.Offset);
    }

    [Fact]
    public void PrefilterKeepsRandomKeysAndDropsPadding()
    {
        Assert.False(KeyScanner.PassesPrefilter(new byte[32]));
        Assert.False(KeyScanner.PassesPrefilter(Enumerable.Repeat((byte)0xAB, 32).ToArray()));
        Assert.True(KeyScanner.PassesPrefilter(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()));
    }

    [Fact]
    public void WordlistRecoversTheKey()
    {
        byte[] pack = EncryptedPack(out byte[] key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));

        // A password-derived key pack: build one whose key comes from the password "godot".
        byte[] derived = GodotCrypto.KeyFromPassword("godot");
        byte[] pack2 = TestPacks.BuildV4(
            [new TestPacks.FileSpec("res://main.gd", "extends Node\n"u8.ToArray())], derived);
        using var source2 = new MemorySource(pack2);
        Assert.True(PckLocator.TryLocate(source2, out PckHeader? header2, out _));
        Oracle oracle = OracleFor(pack2, header2!);

        KeyHit? hit = KeyScanner.TryPasswords(oracle, ["nope", "godot"]);
        Assert.NotNull(hit);
        Assert.Equal(derived, hit!.Key);
    }

    [Fact]
    public void KeyRecoveryWalksTheCandidates()
    {
        byte[] pack = EncryptedPack(out byte[] key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));

        string directory = Path.Combine(Path.GetTempPath(), "ungodot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string binary = Path.Combine(directory, "game.exe");
            var blob = new byte[32768];
            new Random(9).NextBytes(blob);
            key.CopyTo(blob, 4096);
            File.WriteAllBytes(binary, blob);

            byte[]? recovered = KeyRecovery.Recover(source, header!, [binary], null, default);
            Assert.NotNull(recovered);
            Assert.Equal(key, recovered);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}