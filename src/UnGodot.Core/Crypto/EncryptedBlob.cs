using System.Buffers.Binary;
using System.Security.Cryptography;
using UnGodot.Core.IO;

namespace UnGodot.Core.Crypto;

/// <summary>
/// The framing of Godot's <c>FileAccessEncrypted</c> (no magic header variant):
/// <code>
/// [16] MD5 of the plaintext
/// [ 8] plaintext length (u64 LE)
/// [16] IV
/// [ n] AES-256-CFB ciphertext, padded to a multiple of 16
/// </code>
/// Both the file table of an encrypted pack and individually encrypted files use it.
/// </summary>
public sealed class EncryptedBlob
{
    public const int HeaderSize = 16 + 8 + 16;

    /// <summary>Refuse absurd length fields coming from corrupted or hostile packs.</summary>
    public const long MaxPlaintextLength = 1L << 38;

    private EncryptedBlob(byte[] plaintextMd5, long plaintextLength, byte[] iv, byte[] ciphertext)
    {
        PlaintextMd5 = plaintextMd5;
        PlaintextLength = plaintextLength;
        Iv = iv;
        Ciphertext = ciphertext;
    }

    public byte[] PlaintextMd5 { get; }

    public long PlaintextLength { get; }

    public byte[] Iv { get; }

    public byte[] Ciphertext { get; }

    public long CiphertextLength => Pad16(PlaintextLength);

    public long TotalLength => HeaderSize + CiphertextLength;

    public static long Pad16(long length) => (length + 15) & ~15L;

    public static bool TryParseHeader(ReadOnlySpan<byte> buffer, out EncryptedBlob blob, out string? error)
    {
        blob = null!;
        error = null;
        if (buffer.Length < HeaderSize)
        {
            error = "the encrypted blob header is truncated";
            return false;
        }

        ulong plainLength = BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..24]);
        if (plainLength > MaxPlaintextLength)
        {
            error = $"implausible encrypted blob length {plainLength}";
            return false;
        }

        blob = new EncryptedBlob(
            buffer[..16].ToArray(),
            (long)plainLength,
            buffer.Slice(24, 16).ToArray(),
            []);
        return true;
    }

    /// <summary>Reads the header only; the ciphertext is not loaded.</summary>
    public static EncryptedBlob? ReadHeader(IRandomAccessSource source, long offset, out string? error)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        if (source.ReadAt(offset, header) != HeaderSize)
        {
            error = "the encrypted blob header is truncated";
            return null;
        }

        if (!TryParseHeader(header, out EncryptedBlob blob, out error))
        {
            return null;
        }

        return blob;
    }

    /// <summary>Reads the header and the ciphertext of a blob stored at <paramref name="offset"/>.</summary>
    public static EncryptedBlob? Read(IRandomAccessSource source, long offset, out string? error)
    {
        EncryptedBlob? header = ReadHeader(source, offset, out error);
        if (header is null)
        {
            return null;
        }

        long total = header.TotalLength;
        if (offset + total > source.Length)
        {
            error = $"the encrypted blob at {offset} needs {total} bytes but the source ends at {source.Length}";
            return null;
        }

        byte[] ciphertext = source.ReadAt(offset + HeaderSize, (int)header.CiphertextLength);
        if (ciphertext.Length != header.CiphertextLength)
        {
            error = "the encrypted blob ciphertext is truncated";
            return null;
        }

        return new EncryptedBlob(header.PlaintextMd5, header.PlaintextLength, header.Iv, ciphertext);
    }

    /// <summary>Reads only the first <paramref name="ciphertextBytes"/> ciphertext bytes.</summary>
    public static EncryptedBlob? ReadPrefix(IRandomAccessSource source, long offset, int ciphertextBytes, out string? error)
    {
        EncryptedBlob? header = ReadHeader(source, offset, out error);
        if (header is null)
        {
            return null;
        }

        int want = (int)Math.Min(ciphertextBytes, header.CiphertextLength);
        var ciphertext = new byte[want];
        int read = source.ReadAt(offset + HeaderSize, ciphertext);
        if (read < want)
        {
            error = "the encrypted blob ciphertext is truncated";
            return null;
        }

        return new EncryptedBlob(header.PlaintextMd5, header.PlaintextLength, header.Iv, ciphertext);
    }

    /// <summary>Decrypts and verifies the plaintext MD5. Returns null when the key is wrong.</summary>
    public byte[]? TryDecrypt(ReadOnlySpan<byte> key)
    {
        if (key.Length != GodotCrypto.KeyLength)
        {
            throw new ArgumentException("the key must be 32 bytes", nameof(key));
        }

        int plainLength = (int)PlaintextLength;
        if (plainLength < 0 || Ciphertext.Length < plainLength)
        {
            return null;
        }

        byte[] plain = GodotCrypto.DecryptCfb(Ciphertext.AsSpan(0, (int)CiphertextLength), key, Iv);
        if (plain.Length < plainLength)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                MD5.HashData(plain.AsSpan(0, plainLength)),
                PlaintextMd5))
        {
            return null;
        }

        return plain[..plainLength];
    }

    /// <summary>Encrypts a plaintext blob the way Godot writes one (used by tests and pack builders).</summary>
    public static byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        var output = new byte[HeaderSize + Pad16(plaintext.Length)];
        MD5.HashData(plaintext, output.AsSpan(0, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(16, 8), (ulong)plaintext.Length);
        iv[..16].CopyTo(output.AsSpan(24, 16));
        GodotCrypto.EncryptCfb(plaintext, key, iv).CopyTo(output, HeaderSize);
        return output;
    }
}