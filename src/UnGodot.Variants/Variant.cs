namespace UnGodot.Variants;

/// <summary>Godot 3 variant type ids (the same order Godot 2 used for its basic types).</summary>
public enum VariantType
{
    Nil = 0,
    Bool = 1,
    Int = 2,
    Real = 3,
    String = 4,
    Vector2 = 5,
    Rect2 = 6,
    Vector3 = 7,
    Transform2D = 8,
    Plane = 9,
    Quat = 10,
    Aabb = 11,
    Basis = 12,
    Transform = 13,
    Color = 14,
    NodePath = 15,
    Rid = 16,
    Object = 17,
    Dictionary = 18,
    Array = 19,
    PoolByteArray = 20,
    PoolIntArray = 21,
    PoolRealArray = 22,
    PoolStringArray = 23,
    PoolVector2Array = 24,
    PoolVector3Array = 25,
    PoolColorArray = 26,
}

/// <summary>A node path, kept structurally because Godot stores names and subnames separately.</summary>
public sealed class NodePathValue
{
    public NodePathValue(IReadOnlyList<string> names, IReadOnlyList<string> subNames, bool absolute)
    {
        Names = names;
        SubNames = subNames;
        Absolute = absolute;
    }

    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<string> SubNames { get; }
    public bool Absolute { get; }

    public override string ToString()
    {
        string path = string.Join('/', Names);
        if (SubNames.Count > 0)
        {
            path += ":" + string.Join(':', SubNames);
        }

        return Absolute && !path.StartsWith('/') ? "/" + path : path;
    }
}

/// <summary>
/// A Godot object decoded from a variant stream: the class name plus the stored properties.
/// The toolkit never instantiates engine objects, so the properties are kept as variants.
/// </summary>
public sealed class GodotObject
{
    public GodotObject(string className)
    {
        ClassName = className;
    }

    public string ClassName { get; }

    public List<KeyValuePair<string, Variant>> Properties { get; } = [];
}

/// <summary>
/// A decoded Godot variant value. Godot has no sum types, so neither do we: the
/// <see cref="Type"/> field selects how <see cref="Value"/> is interpreted.
/// </summary>
public sealed class Variant
{
    public static readonly Variant Nil = new(VariantType.Nil, null);

    public Variant(VariantType type, object? value)
    {
        Type = type;
        Value = value;
    }

    public VariantType Type { get; }

    public object? Value { get; }

    public bool IsNil => Type == VariantType.Nil;

    public bool AsBool() => (bool)Value!;

    public long AsInt() => (long)Value!;

    public double AsDouble() => Type switch
    {
        VariantType.Real => (double)Value!,
        VariantType.Int => (long)Value!,
        _ => throw new InvalidOperationException($"not a number: {Type}"),
    };

    public string AsString() => (string)Value!;

    public float[] AsFloats() => (float[])Value!;

    public GodotObject? AsObject() => Value as GodotObject;

    public IReadOnlyList<KeyValuePair<Variant, Variant>> AsDictionary() =>
        (IReadOnlyList<KeyValuePair<Variant, Variant>>)Value!;

    public IReadOnlyList<Variant> AsArray() => (IReadOnlyList<Variant>)Value!;

    public static Variant FromBool(bool value) => new(VariantType.Bool, value);
    public static Variant FromInt(long value) => new(VariantType.Int, value);
    public static Variant FromReal(double value) => new(VariantType.Real, value);
    public static Variant FromString(string value) => new(VariantType.String, value);

    public override string ToString() => Type switch
    {
        VariantType.Nil => "null",
        VariantType.Bool => (bool)Value! ? "true" : "false",
        VariantType.Int => ((long)Value!).ToString(),
        VariantType.Real => ((double)Value!).ToString("R"),
        VariantType.String => (string)Value!,
        VariantType.NodePath => ((NodePathValue)Value!).ToString(),
        _ => $"{Type}(...)",
    };
}

public sealed class VariantException : Exception
{
    public VariantException(string message) : base(message)
    {
    }
}