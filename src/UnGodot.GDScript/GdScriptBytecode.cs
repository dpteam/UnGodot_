using System.Buffers.Binary;
using UnGodot.Variants;

namespace UnGodot.GDScript;

public sealed class GdScriptBytecodeException : Exception
{
    public GdScriptBytecodeException(string message) : base(message)
    {
    }
}

/// <summary>
/// The <c>GDSC</c> container Godot writes for compiled scripts: identifiers, constants and
/// the packed token stream. Only the layout used by GDScript 1.0 (Godot 2/3) is handled
/// here; GDScript 2.0 (Godot 4) compresses its payload with Zstandard and is a separate
/// reader.
/// </summary>
public sealed class GdScriptBytecode
{
    public const int GdScript2Version = 100;

    private static ReadOnlySpan<byte> Magic => "GDSC"u8;

    private readonly int[] _lineKeys;
    private readonly int[] _lineValues;

    private GdScriptBytecode(
        int version,
        List<string> identifiers,
        List<Variant> constants,
        uint[] tokens,
        SortedDictionary<int, int> lines)
    {
        Version = version;
        Identifiers = identifiers;
        Constants = constants;
        Tokens = tokens;
        Lines = lines;
        _lineKeys = lines.Keys.ToArray();
        _lineValues = lines.Values.ToArray();
    }

    public int Version { get; }

    public List<string> Identifiers { get; }

    public List<Variant> Constants { get; }

    public uint[] Tokens { get; }

    /// <summary>Token offset to packed line (low 24 bits) / column (high 8 bits).</summary>
    public SortedDictionary<int, int> Lines { get; }

    public bool IsGdScript2 => Version >= GdScript2Version;

    /// <summary>Reads the bytecode version without parsing the rest.</summary>
    public static int PeekVersion(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8 || !buffer[..4].SequenceEqual(Magic))
        {
            throw new GdScriptBytecodeException("not a compiled GDScript file (missing GDSC header)");
        }

        return (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
    }

    public static bool LooksLikeBytecode(ReadOnlySpan<byte> buffer) =>
        buffer.Length >= 8 && buffer[..4].SequenceEqual(Magic);

    public static GdScriptBytecode Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 24 || !buffer[..4].SequenceEqual(Magic))
        {
            throw new GdScriptBytecodeException("not a compiled GDScript file (missing GDSC header)");
        }

        int version = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (version >= GdScript2Version)
        {
            throw new GdScriptBytecodeException(
                $"GDScript bytecode version {version} (Godot 4) uses a Zstandard-compressed container which is not supported yet");
        }

        uint identifierCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]);
        uint constantCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]);
        uint lineCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        uint tokenCount = BinaryPrimitives.ReadUInt32LittleEndian(buffer[20..]);

        if (identifierCount > 10_000_000 || constantCount > 10_000_000 || tokenCount > 100_000_000)
        {
            throw new GdScriptBytecodeException("implausible script header counts");
        }

        int position = 24;

        var identifiers = new List<string>((int)identifierCount);
        for (uint i = 0; i < identifierCount; i++)
        {
            int length = ReadInt32(buffer, ref position);
            if (length < 0 || position + length > buffer.Length)
            {
                throw new GdScriptBytecodeException($"identifier {i} is out of bounds");
            }

            // Godot obfuscates identifiers with a fixed XOR and terminates them with NUL
            // (the rest of the length may be padding, which the engine also ignores).
            byte[] bytes = buffer.Slice(position, length).ToArray();
            int end = bytes.Length;
            for (int b = 0; b < bytes.Length; b++)
            {
                bytes[b] ^= 0xB6;
                if (bytes[b] == 0)
                {
                    end = b;
                    break;
                }
            }

            identifiers.Add(System.Text.Encoding.UTF8.GetString(bytes, 0, end));
            position += length;
        }

        var constants = new List<Variant>((int)constantCount);
        for (uint i = 0; i < constantCount; i++)
        {
            var reader = new VariantReader(buffer[position..], engineMajor: 3);
            Variant value = reader.Read();
            constants.Add(value);
            position += reader.Position;
            if (position > buffer.Length)
            {
                throw new GdScriptBytecodeException($"constant {i} runs past the end of the file");
            }
        }

        var lines = new SortedDictionary<int, int>();
        for (uint i = 0; i < lineCount; i++)
        {
            int token = ReadInt32(buffer, ref position);
            int lineColumn = ReadInt32(buffer, ref position);
            if (!lines.ContainsKey(token))
            {
                lines[token] = lineColumn;
            }
        }

        var tokens = new uint[tokenCount];
        for (uint i = 0; i < tokenCount; i++)
        {
            if (position >= buffer.Length)
            {
                throw new GdScriptBytecodeException("the token stream is truncated");
            }

            byte first = buffer[position];
            if ((first & 0x80) != 0)
            {
                if (position + 4 > buffer.Length)
                {
                    throw new GdScriptBytecodeException("a long token is truncated");
                }

                tokens[i] = BinaryPrimitives.ReadUInt32LittleEndian(buffer[position..]) & ~0x80u;
                position += 4;
            }
            else
            {
                tokens[i] = first;
                position += 1;
            }
        }

        return new GdScriptBytecode(version, identifiers, constants, tokens, lines);
    }

    /// <summary>Line for a token offset, with the same "closest at or before" rule Godot uses.</summary>
    public int GetLine(int tokenOffset)
    {
        if (_lineKeys.Length == 0)
        {
            return -1;
        }

        int index = Array.BinarySearch(_lineKeys, tokenOffset);
        if (index < 0)
        {
            index = ~index - 1;
        }

        int value = index >= 0 ? _lineValues[index] : _lineValues[0];
        return value & 0xFFFFFF;
    }

    private static int ReadInt32(ReadOnlySpan<byte> buffer, ref int position)
    {
        if (position + 4 > buffer.Length)
        {
            throw new GdScriptBytecodeException("unexpected end of the script buffer");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(buffer[position..]);
        position += 4;
        return value;
    }
}