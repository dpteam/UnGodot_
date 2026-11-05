using System.Buffers.Binary;
using System.Text;

namespace UnGodot.Variants;

/// <summary>
/// Decoder for Godot's binary variant format, the one used inside <c>.gdc</c> constants,
/// <c>project.binary</c>, <c>.res</c>/<c>.scn</c> files and import metadata.
///
/// Godot 3 layout is implemented here (the version this pipeline targets first);
/// <paramref name="engineMajor"/> is kept explicit so the Godot 4 decoder can be added
/// without changing call sites.
/// </summary>
public sealed class VariantReader
{
    private const uint EncodeMask = 0xFF;
    private const uint EncodeFlag64 = 1 << 16;
    private const uint EncodeFlagObjectAsId = 1 << 16;

    /// <summary>Guard against hostile/corrupted nesting, exactly like the engine does.</summary>
    private const int MaxDepth = 64;

    private readonly byte[] _data;
    private int _position;

    public VariantReader(ReadOnlySpan<byte> data, int engineMajor = 3)
    {
        _data = data.ToArray();
        EngineMajor = engineMajor;
    }

    public VariantReader(byte[] data, int engineMajor = 3)
    {
        _data = data;
        EngineMajor = engineMajor;
    }

    public int EngineMajor { get; }

    public int Position
    {
        get => _position;
        set => _position = value;
    }

    public int Length => _data.Length;

    public int Remaining => _data.Length - _position;

    /// <summary>Reads one variant and returns it. Throws <see cref="VariantException"/> on bad data.</summary>
    public Variant Read() => ReadVariant(0);

    /// <summary>Reads every variant until the buffer is exhausted (used by array-like payloads).</summary>
    public List<Variant> ReadAll()
    {
        var values = new List<Variant>();
        while (_position < _data.Length)
        {
            values.Add(Read());
        }

        return values;
    }

    private Variant ReadVariant(int depth)
    {
        if (depth > MaxDepth)
        {
            throw new VariantException("variant nesting is too deep");
        }

        uint header = ReadUInt32();
        VariantType type = (VariantType)(header & EncodeMask);
        if ((uint)type >= 27)
        {
            throw new VariantException($"unknown variant type {type} at offset {_position - 4}");
        }

        switch (type)
        {
            case VariantType.Nil:
                return Variant.Nil;

            case VariantType.Bool:
                return Variant.FromBool(ReadUInt32() != 0);

            case VariantType.Int:
                return Variant.FromInt((header & EncodeFlag64) != 0 ? ReadInt64() : ReadInt32());

            case VariantType.Real:
                return Variant.FromReal((header & EncodeFlag64) != 0 ? ReadDouble() : ReadSingle());

            case VariantType.String:
                return Variant.FromString(ReadString());

            case VariantType.Vector2:
                return new Variant(type, new[] { ReadSingle(), ReadSingle() });

            case VariantType.Rect2:
                return new Variant(type, ReadFloats(4));

            case VariantType.Vector3:
                return new Variant(type, ReadFloats(3));

            case VariantType.Transform2D:
                return new Variant(type, ReadFloats(6));

            case VariantType.Plane:
                return new Variant(type, ReadFloats(4));

            case VariantType.Quat:
                return new Variant(type, ReadFloats(4));

            case VariantType.Aabb:
                return new Variant(type, ReadFloats(6));

            case VariantType.Basis:
                return new Variant(type, ReadFloats(9));

            case VariantType.Transform:
                return new Variant(type, ReadFloats(12));

            case VariantType.Color:
                return new Variant(type, ReadFloats(4));

            case VariantType.NodePath:
                return new Variant(type, ReadNodePath());

            case VariantType.Rid:
                return new Variant(type, null);

            case VariantType.Object:
                return ReadObject(header, depth);

            case VariantType.Dictionary:
            {
                uint count = ReadUInt32() & 0x7FFF_FFFF;
                var entries = new List<KeyValuePair<Variant, Variant>>((int)Math.Min(count, 1024));
                for (uint i = 0; i < count; i++)
                {
                    Variant key = ReadVariant(depth + 1);
                    Variant value = ReadVariant(depth + 1);
                    entries.Add(new KeyValuePair<Variant, Variant>(key, value));
                }

                return new Variant(type, entries);
            }

            case VariantType.Array:
            {
                uint count = ReadUInt32() & 0x7FFF_FFFF;
                var items = new List<Variant>((int)Math.Min(count, 1024));
                for (uint i = 0; i < count; i++)
                {
                    items.Add(ReadVariant(depth + 1));
                }

                return new Variant(type, items);
            }

            case VariantType.PoolByteArray:
            {
                int count = ReadInt32();
                Require(count);
                byte[] bytes = _data.AsSpan(_position, count).ToArray();
                _position += count;
                _position += Padding(count);
                return new Variant(type, bytes);
            }

            case VariantType.PoolIntArray:
            {
                int count = ReadInt32();
                Require(count * 4);
                var values = new int[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadInt32();
                }

                return new Variant(type, values);
            }

            case VariantType.PoolRealArray:
            {
                int count = ReadInt32();
                Require(count * 4);
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadSingle();
                }

                return new Variant(type, values);
            }

            case VariantType.PoolStringArray:
            {
                int count = ReadInt32();
                var values = new string[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadString();
                }

                return new Variant(type, values);
            }

            case VariantType.PoolVector2Array:
            case VariantType.PoolVector3Array:
            case VariantType.PoolColorArray:
            {
                int width = type switch
                {
                    VariantType.PoolVector2Array => 2,
                    VariantType.PoolVector3Array => 3,
                    _ => 4,
                };

                int count = ReadInt32();
                Require(count * 4 * width);
                return new Variant(type, ReadFloats(count * width));
            }

            default:
                throw new VariantException($"unsupported variant type {type} at offset {_position - 4}");
        }
    }

    private Variant ReadObject(uint header, int depth)
    {
        if ((header & EncodeFlagObjectAsId) != 0)
        {
            ulong id = ReadUInt64();
            return new Variant(VariantType.Object, id == 0 ? null : new GodotObject($"ObjectID({id})"));
        }

        string className = ReadString();
        if (className.Length == 0)
        {
            return new Variant(VariantType.Object, null);
        }

        var obj = new GodotObject(className);
        uint count = ReadUInt32();
        if (count > 1_000_000)
        {
            throw new VariantException($"implausible property count {count} for {className}");
        }

        for (uint i = 0; i < count; i++)
        {
            string name = ReadString();
            Variant value = ReadVariant(depth + 1);
            obj.Properties.Add(new KeyValuePair<string, Variant>(name, value));
        }

        return new Variant(VariantType.Object, obj);
    }

    private NodePathValue ReadNodePath()
    {
        uint first = ReadUInt32();
        if ((first & 0x8000_0000) == 0)
        {
            // Godot 2 style: the path is stored as a plain string. Kept for completeness.
            _position -= 4;
            string legacy = ReadString();
            return new NodePathValue(legacy.Split('/', StringSplitOptions.RemoveEmptyEntries), [], legacy.StartsWith('/'));
        }

        uint nameCount = first & 0x7FFF_FFFF;
        uint subNameCount = ReadUInt32();
        uint flags = ReadUInt32();

        if ((flags & 2) != 0)
        {
            subNameCount++;
        }

        uint total = nameCount + subNameCount;
        if (total > 1_000_000)
        {
            throw new VariantException($"implausible node path segment count {total}");
        }

        var names = new List<string>((int)nameCount);
        var subNames = new List<string>((int)subNameCount);
        for (uint i = 0; i < total; i++)
        {
            string segment = ReadString();
            if (i < nameCount)
            {
                names.Add(segment);
            }
            else
            {
                subNames.Add(segment);
            }
        }

        return new NodePathValue(names, subNames, (flags & 1) != 0);
    }

    private string ReadString()
    {
        int length = ReadInt32();
        if (length < 0)
        {
            throw new VariantException($"negative string length {length}");
        }

        Require(length);

        // Godot stores packed string array entries with a length that includes the NUL
        // terminator. Godot's String stops at the first NUL, so drop it here too.
        int contentLength = length;
        if (contentLength > 0 && _data[_position + contentLength - 1] == 0)
        {
            contentLength--;
        }

        string value = Encoding.UTF8.GetString(_data, _position, contentLength);
        _position += length + Padding(length);
        return value;
    }

    private float[] ReadFloats(int count)
    {
        Require(count * 4);
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadSingle();
        }

        return values;
    }

    private static int Padding(int length) => (4 - (length % 4)) % 4;

    private void Require(long count)
    {
        if (count < 0 || _position + count > _data.Length)
        {
            throw new VariantException($"variant stream truncated at offset {_position} (needed {count} more bytes)");
        }
    }

    private uint ReadUInt32()
    {
        Require(4);
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    private int ReadInt32()
    {
        Require(4);
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    private ulong ReadUInt64()
    {
        Require(8);
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    private long ReadInt64()
    {
        Require(8);
        long value = BinaryPrimitives.ReadInt64LittleEndian(_data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    private float ReadSingle()
    {
        Require(4);
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_position, 4));
        _position += 4;
        return value;
    }

    private double ReadDouble()
    {
        Require(8);
        double value = BinaryPrimitives.ReadDoubleLittleEndian(_data.AsSpan(_position, 8));
        _position += 8;
        return value;
    }

    /// <summary>Convenience: decodes the first variant of a buffer and reports how many bytes it used.</summary>
    public static Variant Decode(ReadOnlySpan<byte> data, out int consumed, int engineMajor = 3)
    {
        var reader = new VariantReader(data, engineMajor);
        Variant value = reader.Read();
        consumed = reader.Position;
        return value;
    }
}