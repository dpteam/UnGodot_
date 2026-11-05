using System.Buffers.Binary;
using System.Text;
using UnGodot.Variants;

namespace UnGodot.Resources;

/// <summary>How an object variant points at a resource.</summary>
public enum ResourceRefKind
{
    Empty,
    Internal,
    ExternalIndex,
    ExternalPath,
}

public sealed class ResourceReference
{
    public ResourceRefKind Kind { get; init; }
    public int Index { get; init; }
    public string? Type { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// The variant encoding used *inside* Godot binary resources. It is the legacy Godot 2 set
/// of type ids (<c>VARIANT_*</c> in <c>core/io/resource_format_binary.cpp</c>), not the
/// <c>marshalls</c> encoding used by compiled script constants.
/// </summary>
public sealed class ResourceVariantReader
{
    private const uint Nil = 1;
    private const uint Bool = 2;
    private const uint Int = 3;
    private const uint Real = 4;
    private const uint String = 5;
    private const uint Vector2 = 10;
    private const uint Rect2 = 11;
    private const uint Vector3 = 12;
    private const uint Plane = 13;
    private const uint Quat = 14;
    private const uint Aabb = 15;
    private const uint Matrix3 = 16;
    private const uint Transform = 17;
    private const uint Matrix32 = 18;
    private const uint Color = 20;
    private const uint NodePath = 22;
    private const uint Rid = 23;
    private const uint Object = 24;
    private const uint Dictionary = 26;
    private const uint Array = 30;
    private const uint RawArray = 31;
    private const uint IntArray = 32;
    private const uint RealArray = 33;
    private const uint StringArray = 34;
    private const uint Vector3Array = 35;
    private const uint ColorArray = 36;
    private const uint Vector2Array = 37;
    private const uint Int64 = 40;
    private const uint Double = 41;

    private readonly byte[] _data;
    private readonly IReadOnlyList<string> _strings;
    private readonly bool _realIsDouble;
    private readonly int _formatVersion;
    private readonly int _engineMajor;
    private int _position;

    public ResourceVariantReader(
        byte[] data,
        int position,
        IReadOnlyList<string> strings,
        bool realIsDouble,
        int formatVersion,
        int engineMajor = 3)
    {
        _data = data;
        _position = position;
        _strings = strings;
        _realIsDouble = realIsDouble;
        _formatVersion = formatVersion;
        _engineMajor = engineMajor;
    }

    public int Position => _position;

    public Variant Read()
    {
        uint type = ReadU32();
        if (_engineMajor >= 4)
        {
            return ReadGodot4(type);
        }

        switch (type)
        {
            case Nil:
                return Variant.Nil;

            case Bool:
                return Variant.FromBool(ReadU32() != 0);

            case Int:
                return Variant.FromInt(unchecked((int)ReadU32()));

            case Int64:
                return Variant.FromInt(unchecked((long)ReadU64()));

            case Real:
                return Variant.FromReal(ReadReal());

            case Double:
                return Variant.FromReal(BitConverter.Int64BitsToDouble(unchecked((long)ReadU64())));

            case String:
                return Variant.FromString(ReadString());

            case Vector2:
                return new Variant(VariantType.Vector2, new[] { ReadRealF(), ReadRealF() });

            case Rect2:
                return new Variant(VariantType.Rect2, ReadFloats(4));

            case Vector3:
                return new Variant(VariantType.Vector3, ReadFloats(3));

            case Plane:
                return new Variant(VariantType.Plane, ReadFloats(4));

            case Quat:
                return new Variant(VariantType.Quat, ReadFloats(4));

            case Aabb:
                return new Variant(VariantType.Aabb, ReadFloats(6));

            case Matrix3:
                return new Variant(VariantType.Basis, ReadFloats(9));

            case Transform:
                return new Variant(VariantType.Transform, ReadFloats(12));

            case Matrix32:
                return new Variant(VariantType.Transform2D, ReadFloats(6));

            case Color:
                return new Variant(VariantType.Color, ReadFloats(4));

            case NodePath:
                return new Variant(VariantType.NodePath, ReadNodePath());

            case Rid:
                _ = ReadU32();
                return new Variant(VariantType.Rid, null);

            case Object:
                return new Variant(VariantType.Object, ReadResourceReference());

            case Dictionary:
            {
                int count = (int)(ReadU32() & 0x7FFF_FFFF);
                var entries = new List<KeyValuePair<Variant, Variant>>(Math.Min(count, 1024));
                for (int i = 0; i < count; i++)
                {
                    Variant key = Read();
                    Variant value = Read();
                    entries.Add(new KeyValuePair<Variant, Variant>(key, value));
                }

                return new Variant(VariantType.Dictionary, entries);
            }

            case Array:
            {
                int count = (int)(ReadU32() & 0x7FFF_FFFF);
                var items = new List<Variant>(Math.Min(count, 1024));
                for (int i = 0; i < count; i++)
                {
                    items.Add(Read());
                }

                return new Variant(VariantType.Array, items);
            }

            case RawArray:
            {
                int count = ReadI32();
                byte[] bytes = _data.AsSpan(_position, count).ToArray();
                _position += count;
                _position += (4 - (count % 4)) % 4;
                return new Variant(VariantType.PoolByteArray, bytes);
            }

            case IntArray:
            {
                int count = ReadI32();
                var values = new int[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadI32();
                }

                return new Variant(VariantType.PoolIntArray, values);
            }

            case RealArray:
            {
                int count = ReadI32();
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadRealF();
                }

                return new Variant(VariantType.PoolRealArray, values);
            }

            case StringArray:
            {
                int count = ReadI32();
                var values = new string[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadString();
                }

                return new Variant(VariantType.PoolStringArray, values);
            }

            case Vector3Array:
            case ColorArray:
            case Vector2Array:
            {
                int width = type switch
                {
                    Vector3Array => 3,
                    ColorArray => 4,
                    _ => 2,
                };

                int count = ReadI32();
                float[] values = ReadFloats(count * width);
                VariantType variantType = type switch
                {
                    Vector3Array => VariantType.PoolVector3Array,
                    ColorArray => VariantType.PoolColorArray,
                    _ => VariantType.PoolVector2Array,
                };
                return new Variant(variantType, values);
            }

            default:
                throw new ResourceFormatException($"unsupported resource variant type {type} at offset {_position - 4}");
        }
    }

    /// <summary>
    /// Godot 4 resource variants. The ids come from the <c>VARIANT_*</c> table in
    /// <c>core/io/resource_format_binary.cpp</c>; they are not contiguous and differ from both
    /// the Godot 3 table and Godot 4's <c>Variant::Type</c>.
    /// </summary>
    private Variant ReadGodot4(uint type)
    {
        switch (type)
        {
            case 1:
                return Variant.Nil;

            case 2:
                return Variant.FromBool(ReadU32() != 0);

            case 3:
                return Variant.FromInt(ReadI32());

            case 40:
                return Variant.FromInt(unchecked((long)ReadU64()));

            case 4:
                return Variant.FromReal(ReadReal());

            case 41:
                return Variant.FromReal(BitConverter.Int64BitsToDouble(unchecked((long)ReadU64())));

            case 5:
                return Variant.FromString(ReadString());

            case 44:
                return new Variant(VariantType.StringName, ReadString());

            case 10:
                return new Variant(VariantType.Vector2, ReadFloats(2));

            case 45:
                return new Variant(VariantType.Vector2i, ReadInts(2));

            case 11:
                return new Variant(VariantType.Rect2, ReadFloats(4));

            case 46:
                return new Variant(VariantType.Rect2i, ReadInts(4));

            case 12:
                return new Variant(VariantType.Vector3, ReadFloats(3));

            case 47:
                return new Variant(VariantType.Vector3i, ReadInts(3));

            case 50:
                return new Variant(VariantType.Vector4, ReadFloats(4));

            case 51:
                return new Variant(VariantType.Vector4i, ReadInts(4));

            case 13:
                return new Variant(VariantType.Plane, ReadFloats(4));

            case 14:
                return new Variant(VariantType.Quat, ReadFloats(4));

            case 15:
                return new Variant(VariantType.Aabb, ReadFloats(6));

            case 16:
                return new Variant(VariantType.Basis, ReadFloats(9));

            case 17:
                return new Variant(VariantType.Transform, ReadFloats(12));

            case 18:
                return new Variant(VariantType.Transform2D, ReadFloats(6));

            case 52:
                return new Variant(VariantType.Projection, ReadFloats(16));

            case 20:
                return new Variant(VariantType.Color, ReadSingleFloats(4));

            case 22:
                return new Variant(VariantType.NodePath, ReadNodePath());

            case 23:
                _ = ReadU32();
                return new Variant(VariantType.Rid, null);

            case 42:
                return new Variant(VariantType.Callable, null);

            case 43:
                return new Variant(VariantType.Signal, null);

            case 24:
                return new Variant(VariantType.Object, ReadResourceReference());

            case 26:
            {
                int count = (int)(ReadU32() & 0x7FFF_FFFF);
                var entries = new List<KeyValuePair<Variant, Variant>>(Math.Min(count, 1024));
                for (int i = 0; i < count; i++)
                {
                    Variant key = Read();
                    Variant value = Read();
                    entries.Add(new KeyValuePair<Variant, Variant>(key, value));
                }

                return new Variant(VariantType.Dictionary, entries);
            }

            case 30:
            {
                int count = (int)(ReadU32() & 0x7FFF_FFFF);
                var items = new List<Variant>(Math.Min(count, 1024));
                for (int i = 0; i < count; i++)
                {
                    items.Add(Read());
                }

                return new Variant(VariantType.Array, items);
            }

            case 31:
            {
                int count = ReadI32();
                byte[] bytes = _data.AsSpan(_position, count).ToArray();
                _position += count + ((4 - (count % 4)) % 4);
                return new Variant(VariantType.PoolByteArray, bytes);
            }

            case 32:
            {
                int count = ReadI32();
                var values = new int[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadI32();
                }

                return new Variant(VariantType.PoolIntArray, values);
            }

            case 48:
            {
                int count = ReadI32();
                var values = new long[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = unchecked((long)ReadU64());
                }

                return new Variant(VariantType.PoolInt64Array, values);
            }

            case 33:
            {
                int count = ReadI32();
                var values = new float[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadRealF();
                }

                return new Variant(VariantType.PoolRealArray, values);
            }

            case 49:
            {
                int count = ReadI32();
                var values = new double[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = BitConverter.Int64BitsToDouble(unchecked((long)ReadU64()));
                }

                return new Variant(VariantType.PoolFloat64Array, values);
            }

            case 34:
            {
                int count = ReadI32();
                var values = new string[count];
                for (int i = 0; i < count; i++)
                {
                    values[i] = ReadString();
                }

                return new Variant(VariantType.PoolStringArray, values);
            }

            case 35:
                return ReadFloatArray(3, VariantType.PoolVector3Array);

            case 36:
                return new Variant(VariantType.PoolColorArray, ReadSingleFloats(ReadI32() * 4));

            case 37:
                return ReadFloatArray(2, VariantType.PoolVector2Array);

            case 53:
                return ReadFloatArray(4, VariantType.PoolVector4Array);

            default:
                throw new ResourceFormatException(
                    $"unsupported Godot 4 resource variant type {type} at offset {_position - 4}");
        }
    }

    private Variant ReadFloatArray(int width, VariantType type)
    {
        int count = ReadI32();
        return new Variant(type, ReadFloats(count * width));
    }

    private int[] ReadInts(int count)
    {
        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadI32();
        }

        return values;
    }

    /// <summary>Reads single-precision floats regardless of the file's real width (colors).</summary>
    private float[] ReadSingleFloats(int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadSingle();
        }

        return values;
    }

    private ResourceReference ReadResourceReference()
    {
        uint kind = ReadU32();
        return kind switch
        {
            0 => new ResourceReference { Kind = ResourceRefKind.Empty },

            // OBJECT_EXTERNAL_RESOURCE: legacy form carrying the type and path inline.
            1 => new ResourceReference
            {
                Kind = ResourceRefKind.ExternalPath,
                Type = ReadString(),
                Path = ReadString(),
            },

            // OBJECT_INTERNAL_RESOURCE: index into the internal resource table.
            2 => new ResourceReference { Kind = ResourceRefKind.Internal, Index = (int)ReadU32() },

            // OBJECT_EXTERNAL_RESOURCE_INDEX: index into the external resource table.
            3 => new ResourceReference { Kind = ResourceRefKind.ExternalIndex, Index = (int)ReadU32() },

            _ => throw new ResourceFormatException($"unknown object reference kind {kind}"),
        };
    }

    private NodePathValue ReadNodePath()
    {
        int nameCount = ReadU16();
        int subNameCount = ReadU16();
        bool absolute = (subNameCount & 0x8000) != 0;
        subNameCount &= 0x7FFF;
        if (_formatVersion < 3)
        {
            // Old format stored the property as one extra subname.
            subNameCount++;
        }

        var names = new List<string>(nameCount);
        var subNames = new List<string>(subNameCount);
        for (int i = 0; i < nameCount; i++)
        {
            names.Add(ReadStringRef());
        }

        for (int i = 0; i < subNameCount; i++)
        {
            subNames.Add(ReadStringRef());
        }

        return new NodePathValue(names, subNames, absolute);
    }

    /// <summary>Godot's <c>_get_string</c>: an inline string (high bit set) or a table index.</summary>
    public string ReadStringRef()
    {
        uint id = ReadU32();
        if ((id & 0x8000_0000) != 0)
        {
            int length = (int)(id & 0x7FFF_FFFF);
            if (length == 0)
            {
                return string.Empty;
            }

            string inline = Encoding.UTF8.GetString(_data, _position, length);
            _position += length;
            return inline.TrimEnd('\0');
        }

        return id < _strings.Count ? _strings[(int)id] : string.Empty;
    }

    public string ReadString()
    {
        int length = ReadI32();
        string value = Encoding.UTF8.GetString(_data, _position, length);
        _position += length;
        return value.TrimEnd('\0');
    }

    private float[] ReadFloats(int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = ReadRealF();
        }

        return values;
    }

    private double ReadReal() => _realIsDouble ? BitConverter.Int64BitsToDouble(unchecked((long)ReadU64())) : ReadSingle();

    private float ReadRealF() => _realIsDouble ? (float)BitConverter.Int64BitsToDouble(unchecked((long)ReadU64())) : ReadSingle();

    private float ReadSingle()
    {
        float value = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_position));
        _position += 4;
        return value;
    }

    private ushort ReadU16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_position));
        _position += 2;
        return value;
    }

    private int ReadI32() => unchecked((int)ReadU32());

    private uint ReadU32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_position));
        _position += 4;
        return value;
    }

    private ulong ReadU64()
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_position));
        _position += 8;
        return value;
    }
}