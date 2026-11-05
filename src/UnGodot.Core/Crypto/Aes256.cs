using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using AesIntrinsics = System.Runtime.Intrinsics.X86.Aes;

namespace UnGodot.Core.Crypto;

[InlineArray(15)]
internal struct AesRoundKeys
{
    private Vector128<byte> _element;
}

/// <summary>
/// AES-256 with a pre-computed key schedule and a single block encryption primitive:
/// exactly what Godot's CFB keystream and the pack-key oracle need, without allocating.
///
/// The hot path uses the AES-NI instructions through <see cref="Aes"/> intrinsics (key
/// schedule included, ~20 ns per candidate window). On hardware without AES-NI it falls
/// back to the platform implementation.
/// </summary>
public struct Aes256Schedule
{
    private AesRoundKeys _keys;
    private Vector128<byte> _raw0;
    private Vector128<byte> _raw1;

    public static bool IsHardwareAccelerated => AesIntrinsics.IsSupported && Sse2.IsSupported;

    public Aes256Schedule(ReadOnlySpan<byte> key)
    {
        if (key.Length != GodotCrypto.KeyLength)
        {
            throw new ArgumentException($"an AES-256 key must be {GodotCrypto.KeyLength} bytes", nameof(key));
        }

        _keys = default;
        ref byte start = ref MemoryMarshal.GetReference(key);
        _raw0 = Unsafe.ReadUnaligned<Vector128<byte>>(ref start);
        _raw1 = Unsafe.ReadUnaligned<Vector128<byte>>(ref Unsafe.AddByteOffset(ref start, 16));

        if (!IsHardwareAccelerated)
        {
            return;
        }

        Vector128<byte> t1 = _raw0;
        Vector128<byte> t3 = _raw1;
        _keys[0] = t1;
        _keys[1] = t3;

        // Unrolled on purpose: the AES-NI keygenassist and shuffle controls must be
        // compile-time constants for the JIT to emit a single instruction, and this runs
        // once per candidate key during the key search.
        Vector128<byte> t2;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x01));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[2] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[3] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x02));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[4] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[5] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x04));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[6] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[7] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x08));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[8] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[9] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x10));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[10] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[11] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x20));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[12] = t1;

        t2 = Broadcast2(AesIntrinsics.KeygenAssist(t1, 0x00));
        t3 = Sse2.Xor(XorLanes(t3), t2);
        _keys[13] = t3;

        t2 = Broadcast3(AesIntrinsics.KeygenAssist(t3, 0x40));
        t1 = Sse2.Xor(XorLanes(t1), t2);
        _keys[14] = t1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Broadcast3(Vector128<byte> value) =>
        Sse2.Shuffle(value.AsInt32(), 0xFF).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Broadcast2(Vector128<byte> value) =>
        Sse2.Shuffle(value.AsInt32(), 0xAA).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> XorLanes(Vector128<byte> value)
    {
        // Prefix XOR of the four 32-bit words: [a, b, c, d] -> [a, a^b, a^b^c, a^b^c^d].
        // Two lane shifts are enough; a third one would undo the first word again.
        value = Sse2.Xor(value, Sse2.ShiftLeftLogical128BitLane(value, 4));
        value = Sse2.Xor(value, Sse2.ShiftLeftLogical128BitLane(value, 8));
        return value;
    }

    /// <summary>Encrypts one 16 byte block.</summary>
    public readonly void EncryptBlock(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length < 16 || output.Length < 16)
        {
            throw new ArgumentException("an AES block is 16 bytes");
        }

        if (IsHardwareAccelerated)
        {
            Vector128<byte> state = Unsafe.ReadUnaligned<Vector128<byte>>(ref MemoryMarshal.GetReference(input));
            state = Sse2.Xor(state, _keys[0]);
            for (int i = 1; i < 14; i++)
            {
                state = AesIntrinsics.Encrypt(state, _keys[i]);
            }

            state = AesIntrinsics.EncryptLast(state, _keys[14]);
            Unsafe.WriteUnaligned(ref MemoryMarshal.GetReference(output), state);
            return;
        }

        Span<byte> key = stackalloc byte[GodotCrypto.KeyLength];
        MemoryMarshal.Write(key, in _raw0);
        MemoryMarshal.Write(key[16..], in _raw1);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        _ = aes.EncryptEcb(input[..16], output[..16], PaddingMode.None);
    }
}

/// <summary>Godot's crypto primitives: MD5 and AES-256-CFB (128 bit feedback).</summary>
public static class GodotCrypto
{
    public const int KeyLength = 32;
    public const int IvLength = 16;
    public const int BlockSize = 16;

    public static byte[] Md5(ReadOnlySpan<byte> data) => MD5.HashData(data);

    public static bool Md5Equals(ReadOnlySpan<byte> data, ReadOnlySpan<byte> expected)
    {
        Span<byte> digest = stackalloc byte[16];
        _ = MD5.HashData(data, digest);
        return CryptographicOperations.FixedTimeEquals(digest, expected);
    }

    /// <summary>AES-256-CFB decryption, as implemented by <c>CryptoCore::decrypt_cfb</c>.</summary>
    public static byte[] DecryptCfb(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        var output = new byte[data.Length];
        DecryptCfb(data, key, iv, output);
        return output;
    }

    public static void DecryptCfb(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> output)
    {
        var schedule = new Aes256Schedule(key);
        Span<byte> previous = stackalloc byte[BlockSize];
        Span<byte> keystream = stackalloc byte[BlockSize];
        iv[..BlockSize].CopyTo(previous);

        int offset = 0;
        while (offset < data.Length)
        {
            schedule.EncryptBlock(previous, keystream);

            int count = Math.Min(BlockSize, data.Length - offset);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
            }

            // Feedback is the previous ciphertext block, zero padded for a short tail.
            previous.Clear();
            data.Slice(offset, count).CopyTo(previous);
            offset += count;
        }
    }

    /// <summary>AES-256-CFB encryption; only the tests and pack builders need it.</summary>
    public static byte[] EncryptCfb(ReadOnlySpan<byte> data, ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv)
    {
        var schedule = new Aes256Schedule(key);
        var output = new byte[data.Length];
        Span<byte> previous = stackalloc byte[BlockSize];
        Span<byte> keystream = stackalloc byte[BlockSize];
        iv[..BlockSize].CopyTo(previous);

        int offset = 0;
        while (offset < data.Length)
        {
            schedule.EncryptBlock(previous, keystream);

            int count = Math.Min(BlockSize, data.Length - offset);
            previous.Clear();
            for (int i = 0; i < count; i++)
            {
                byte cipher = (byte)(data[offset + i] ^ keystream[i]);
                output[offset + i] = cipher;
                previous[i] = cipher;
            }

            offset += count;
        }

        return output;
    }

    /// <summary>Key derivation of <c>FileAccessEncrypted::open_and_parse_password</c>.</summary>
    public static byte[] KeyFromPassword(string password)
    {
        byte[] digest = MD5.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        var key = new byte[KeyLength];
        for (int i = 0; i < digest.Length; i++)
        {
            key[i * 2] = HexDigit(digest[i] >> 4);
            key[i * 2 + 1] = HexDigit(digest[i] & 0x0F);
        }

        return key;
    }

    private static byte HexDigit(int value) => (byte)(value < 10 ? '0' + value : 'a' + (value - 10));

    public static bool TryParseHexKey(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (value.Length != KeyLength * 2)
        {
            return false;
        }

        var result = new byte[KeyLength];
        for (int i = 0; i < KeyLength; i++)
        {
            int high = HexValue(value[i * 2]);
            int low = HexValue(value[i * 2 + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            result[i] = (byte)((high << 4) | low);
        }

        key = result;
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}