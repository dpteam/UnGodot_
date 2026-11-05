using System.Buffers.Binary;
using System.Text;

namespace UnGodot.Variants;

/// <summary>Which of Godot's variant encodings a buffer uses.</summary>
public enum VariantFormat
{
    /// <summary>Godot 2/3 <c>marshalls</c>: <c>.gdc</c> constants, <c>project.binary</c>.</summary>
    Godot3,

    /// <summary>Godot 4 <c>marshalls</c>: same layout, Godot 4 type ids and 64-bit flags.</summary>
    Godot4Marshalls,

    /// <summary>
    /// Godot 4 binary resources (<c>.res</c>/<c>.scn</c>): its own <c>VARIANT_*</c> ids,
    /// unpadded strings and a real width taken from the file header flags.
    /// </summary>
    Godot4Resource,
}

/// <summary>
/// Decoder for Godot's binary variant format, the one used inside <c>.gdc</c> constants,
/// <c>project.binary</c>, <c>.res</c>/<c>.scn</c> files and import metadata.
///
/// Godot 3 and Godot 4 both work; the two use different type id tables and, in resources,
/// different string padding, so the encoding is selected with <see cref="VariantFormat"/>.
/// </summary>
public sealed class VariantReader
{
    private const uint EncodeMask = 0xFF;
    private const uint EncodeFlag64 = 1 << 16;
    private const uint EncodeFlagObjectAsId = 1 << 16;

    /// <summary>Guard against hostile/corrupted nesting, exactly like the engine does.</summary>
    private const int MaxDepth = 64;

    private readonly byte[] _data;
    private readonly bool _realIsDouble;
    private uint _rawType;
    private int _position;

    public VariantReader(ReadOnlySpan<byte> data, int engineMajor = 3)
        : this(data.ToArray(), engineMajor >= 4 ? VariantFormat.Godot4Marshalls : VariantFormat.Godot3)
    {
    }

    public VariantReader(byte[] data, int engineMajor = 3)
        : this(data, engineMajor >= 4 ? VariantFormat.Godot4Marshalls : VariantFormat.Godot3)
    {
    }

    public VariantReader(ReadOnlySpan<byte> data, VariantFormat format, bool realIsDouble = false)
        : this(data.ToArray(), format, realIsDouble)
    {
    }

    public VariantReader(byte[] data, VariantFormat format, bool realIsDouble = false)
    {
        _data = data;
        Format = format;
        _realIsDouble = realIsDouble;
    }

    public VariantFormat Format { get; }

    /// <summary>Engine major version implied by the encoding.</summary>
    public int EngineMajor => Format == VariantFormat.Godot3 ? 3 : 4;

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
        VariantType type = ResolveType(header);

        // The marshalls encodings mark 64-bit values with a header flag; the resource
        // encoding has separate ids for int64/double and takes real width from the header.
        bool wide = Format switch
        {
            VariantFormat.Godot3 => (header & EncodeFlag64) != 0,
            VariantFormat.Godot4Marshalls => (header & EncodeFlag64) != 0,
            _ => _realIsDouble,
        };

        bool forced64 = Format == VariantFormat.Godot4Resource && _rawType is 40 or 41;

        switch (type)
        {
            case VariantType.Nil:
                return Variant.Nil;

            case VariantType.Bool:
                return Variant.FromBool(ReadUInt32() != 0);

            case VariantType.Int:
                // The resource encoding always writes 32-bit ints unless the id says int64.
                return Variant.FromInt(forced64 || (wide && Format != VariantFormat.Godot4Resource)
                    ? ReadInt64()
                    : ReadInt32());

            case VariantType.Real:
                return Variant.FromReal(wide || forced64 ? ReadDouble() : ReadSingle());

            case VariantType.String:
                return Variant.FromString(ReadString());

            case VariantType.StringName:
                return new Variant(VariantType.StringName, ReadString());

            case VariantType.Vector2:
                return new Variant(type, ReadReals(2, wide));

            case VariantType.Vector2i:
                return new Variant(type, ReadInts(2));

            case VariantType.Rect2:
                return new Variant(type, ReadReals(4, wide));

            case VariantType.Rect2i:
                return new Variant(type, ReadInts(4));

            case VariantType.Vector3:
                return new Variant(type, ReadReals(3, wide));

            case VariantType.Vector3i:
                return new Variant(type, ReadInts(3));

            case VariantType.Vector4:
                return new Variant(type, ReadReals(4, wide));

            case VariantType.Vector4i:
                return new Variant(type, ReadInts(4));

            case VariantType.Transform2D:
                return new Variant(type, ReadReals(6, wide));

            case VariantType.Plane:
                return new Variant(type, ReadReals(4, wide));

            case VariantType.Quat:
                return new Variant(type, ReadReals(4, wide));

            case VariantType.Aabb:
                return new Variant(type, ReadReals(6, wide));

            case VariantType.Basis:
                return new Variant(type, ReadReals(9, wide));

            case VariantType.Transform:
                return new Variant(type, ReadReals(12, wide));

            case VariantType.Projection:
                return new Variant(type, ReadReals(16, wide));

            case VariantType.Color:
                // Colors are always single precision, in every format.
                return new Variant(type, ReadReals(4, wide: false));

            case VariantType.NodePath:
                return new Variant(type, ReadNodePath());

            case VariantType.Rid:
                // Godot 4 marshalls stores the RID id as 64 bits.
                if (Format == VariantFormat.Godot4Marshalls)
                {
                    _ = ReadUInt64();
                }
                else
                {
                    _ = ReadUInt32();
                }

                return new Variant(type, null);

            case VariantType.Callable:
            case VariantType.Signal:
                // Neither the resource nor the marshalls encoding stores a payload.
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
                _position += count + Padding(count);
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

            case VariantType.PoolInt64Array:
            {
                int count = ReadInt32();
                Require(count * 8);
                var values = new long[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadInt64();
                }

                return new Variant(type, values);
            }

            case VariantType.PoolRealArray:
            {
                int count = ReadInt32();
                Require(count * (wide ? 8 : 4));
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = wide ? (float)ReadDouble() : ReadSingle();
                }

                return new Variant(type, values);
            }

            case VariantType.PoolFloat64Array:
            {
                int count = ReadInt32();
                Require(count * 8);
                var values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadDouble();
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
            case VariantType.PoolVector4Array:
            case VariantType.PoolColorArray:
            {
                int width = type switch
                {
                    VariantType.PoolVector2Array => 2,
                    VariantType.PoolVector3Array => 3,
                    VariantType.PoolVector4Array => 4,
                    _ => 4,
                };

                // Colors stay single precision; the other packed arrays follow the real width.
                bool wideValues = wide && type != VariantType.PoolColorArray;
                int count = ReadInt32();
                Require(count * width * (wideValues ? 8 : 4));
                return new Variant(type, ReadReals(count * width, wideValues));
            }

            default:
                throw new VariantException($"unsupported variant type {type} at offset {_position - 4}");
        }
    }

    private VariantType ResolveType(uint header)
    {
        _rawType = header;

        switch (Format)
        {
            case VariantFormat.Godot3:
            {
                var type = (VariantType)(header & EncodeMask);
                if ((uint)type > (uint)VariantType.PoolColorArray)
                {
                    throw new VariantException($"unknown variant type {(uint)type} at offset {_position - 4}");
                }

                return type;
            }

            case VariantFormat.Godot4Marshalls:
                _rawType = header & EncodeMask;
                return MapMarshalls4(_rawType);

            default:
                return MapResource4(header);
        }
    }

    /// <summary>Godot 4 <c>Variant::Type</c> (0-based) → the internal enum.</summary>
    private static VariantType MapMarshalls4(uint id) => id switch
    {
        0 => VariantType.Nil,
        1 => VariantType.Bool,
        2 => VariantType.Int,
        3 => VariantType.Real,
        4 => VariantType.String,
        5 => VariantType.Vector2,
        6 => VariantType.Vector2i,
        7 => VariantType.Rect2,
        8 => VariantType.Rect2i,
        9 => VariantType.Vector3,
        10 => VariantType.Vector3i,
        11 => VariantType.Transform2D,
        12 => VariantType.Vector4,
        13 => VariantType.Vector4i,
        14 => VariantType.Plane,
        15 => VariantType.Quat,
        16 => VariantType.Aabb,
        17 => VariantType.Basis,
        18 => VariantType.Transform,
        19 => VariantType.Projection,
        20 => VariantType.Color,
        21 => VariantType.StringName,
        22 => VariantType.NodePath,
        23 => VariantType.Rid,
        24 => VariantType.Object,
        25 => VariantType.Callable,
        26 => VariantType.Signal,
        27 => VariantType.Dictionary,
        28 => VariantType.Array,
        29 => VariantType.PoolByteArray,
        30 => VariantType.PoolIntArray,
        31 => VariantType.PoolInt64Array,
        32 => VariantType.PoolRealArray,
        33 => VariantType.PoolFloat64Array,
        34 => VariantType.PoolStringArray,
        35 => VariantType.PoolVector2Array,
        36 => VariantType.PoolVector3Array,
        37 => VariantType.PoolColorArray,
        38 => VariantType.PoolVector4Array,
        _ => throw new VariantException($"unknown Godot 4 variant type {id}"),
    };

    /// <summary>Godot 4 resource <c>VARIANT_*</c> ids (not contiguous) → the internal enum.</summary>
    private static VariantType MapResource4(uint id) => id switch
    {
        1 => VariantType.Nil,
        2 => VariantType.Bool,
        3 => VariantType.Int,
        4 => VariantType.Real,
        5 => VariantType.String,
        10 => VariantType.Vector2,
        11 => VariantType.Rect2,
        12 => VariantType.Vector3,
        13 => VariantType.Plane,
        14 => VariantType.Quat,
        15 => VariantType.Aabb,
        16 => VariantType.Basis,
        17 => VariantType.Transform,
        18 => VariantType.Transform2D,
        20 => VariantType.Color,
        22 => VariantType.NodePath,
        23 => VariantType.Rid,
        24 => VariantType.Object,
        26 => VariantType.Dictionary,
        30 => VariantType.Array,
        31 => VariantType.PoolByteArray,
        32 => VariantType.PoolIntArray,
        33 => VariantType.PoolRealArray,
        34 => VariantType.PoolStringArray,
        35 => VariantType.PoolVector3Array,
        36 => VariantType.PoolColorArray,
        37 => VariantType.PoolVector2Array,
        40 => VariantType.Int,
        41 => VariantType.Real,
        42 => VariantType.Callable,
        43 => VariantType.Signal,
        44 => VariantType.StringName,
        45 => VariantType.Vector2i,
        46 => VariantType.Rect2i,
        47 => VariantType.Vector3i,
        48 => VariantType.PoolInt64Array,
        49 => VariantType.PoolFloat64Array,
        50 => VariantType.Vector4,
        51 => VariantType.Vector4i,
        52 => VariantType.Projection,
        53 => VariantType.PoolVector4Array,
        _ => throw new VariantException($"unknown Godot 4 resource variant type {id}"),
    };

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

        // The marshalls encodings pad strings to 4 bytes; the binary resource format does not.
        bool padded = Format != VariantFormat.Godot4Resource;
        _position += padded ? length + Padding(length) : length;
        return value;
    }

    private float[] ReadReals(int count, bool wide)
    {
        Require(count * (wide ? 8 : 4));
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = wide ? (float)ReadDouble() : ReadSingle();
        }

        return values;
    }

    private int[] ReadInts(int count)
    {
        Require(count * 4);
        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadInt32();
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