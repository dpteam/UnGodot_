using System.Globalization;
using System.Text;

namespace UnGodot.Variants;

/// <summary>Where the text will be embedded; Godot uses slightly different syntax in each.</summary>
public enum GodotTextStyle
{
    /// <summary>GDScript source (<c>Vector2( 1, 2 )</c>, <c>@"NodePath"</c>).</summary>
    Script,

    /// <summary><c>project.godot</c> / <c>.tscn</c> (<c>Vector2(1, 2)</c>, <c>NodePath("...")</c>).</summary>
    Config,
}

/// <summary>
/// Writes variants back as Godot text, mirroring <c>VariantWriterCompat</c> so decompiled
/// constants are byte-for-byte the same as the ones GDRE Tools produces.
/// </summary>
public static class VariantTextWriter
{
    public static string Write(Variant value, int engineMajor = 3, GodotTextStyle style = GodotTextStyle.Script)
    {
        return Write(value, engineMajor, style, null);
    }

    public static string Write(
        Variant value,
        int engineMajor,
        GodotTextStyle style,
        Func<Variant, string?>? objectResolver)
    {
        var builder = new StringBuilder();
        Write(builder, value, engineMajor, style, objectResolver);
        return builder.ToString();
    }

    public static void Write(StringBuilder output, Variant value, int engineMajor = 3, GodotTextStyle style = GodotTextStyle.Script)
    {
        Write(output, value, engineMajor, style, null);
    }

    public static void Write(
        StringBuilder output,
        Variant value,
        int engineMajor,
        GodotTextStyle style,
        Func<Variant, string?>? objectResolver)
    {
        bool script = style == GodotTextStyle.Script;

        switch (value.Type)
        {
            case VariantType.Nil:
                output.Append("null");
                break;

            case VariantType.Bool:
                output.Append(value.AsBool() ? "true" : "false");
                break;

            case VariantType.Int:
                output.Append(value.AsInt().ToString(CultureInfo.InvariantCulture));
                break;

            case VariantType.Real:
                output.Append(FormatRealConstant(value.AsDouble(), script));
                break;

            case VariantType.String:
                output.Append('"').Append(Escape(value.AsString())).Append('"');
                break;

            case VariantType.Vector2:
            case VariantType.Vector3:
                WriteVector(output, TypeName(value.Type), value.AsFloats(), script);
                break;

            case VariantType.Rect2:
                WriteVector(output, "Rect2", value.AsFloats(), script);
                break;

            case VariantType.Transform2D:
                WriteVector(output, "Transform2D", value.AsFloats(), script);
                break;

            case VariantType.Plane:
                WriteVector(output, "Plane", value.AsFloats(), script);
                break;

            case VariantType.Quat:
                WriteVector(output, engineMajor >= 4 ? "Quaternion" : "Quat", value.AsFloats(), script);
                break;

            case VariantType.Aabb:
                WriteVector(output, "AABB", value.AsFloats(), script);
                break;

            case VariantType.Basis:
                WriteVector(output, "Basis", value.AsFloats(), script);
                break;

            case VariantType.Transform:
                WriteVector(output, engineMajor >= 4 ? "Transform3D" : "Transform", value.AsFloats(), script);
                break;

            case VariantType.Color:
                WriteVector(output, "Color", value.AsFloats(), script);
                break;

            case VariantType.Vector4:
                WriteVector(output, "Vector4", value.AsFloats(), script);
                break;

            case VariantType.Projection:
                WriteVector(output, "Projection", value.AsFloats(), script);
                break;

            case VariantType.Vector2i:
            case VariantType.Rect2i:
            case VariantType.Vector3i:
            case VariantType.Vector4i:
                WriteIntVector(output, TypeName(value.Type), (int[])value.Value!);
                break;

            case VariantType.StringName:
                output.Append("&\"").Append(Escape(value.AsString())).Append('"');
                break;

            case VariantType.Callable:
                output.Append("Callable()");
                break;

            case VariantType.Signal:
                output.Append("Signal()");
                break;

            case VariantType.NodePath:
            {
                var path = (NodePathValue)value.Value!;
                string escaped = Escape(path.ToString());
                if (script && engineMajor == 3)
                {
                    output.Append("@\"").Append(escaped).Append('"');
                }
                else
                {
                    output.Append("NodePath(\"").Append(escaped).Append("\")");
                }

                break;
            }

            case VariantType.Rid:
                output.Append("RID()");
                break;

            case VariantType.Object:
                string? resolved = objectResolver?.Invoke(value);
                if (resolved is not null)
                {
                    output.Append(resolved);
                }
                else
                {
                    WriteObject(output, value, engineMajor, style);
                }

                break;

            case VariantType.Dictionary:
            {
                IReadOnlyList<KeyValuePair<Variant, Variant>> entries = value.AsDictionary();
                output.Append("{\n");
                for (int i = 0; i < entries.Count; i++)
                {
                    Write(output, entries[i].Key, engineMajor, style, objectResolver);
                    output.Append(": ");
                    Write(output, entries[i].Value, engineMajor, style, objectResolver);
                    if (i < entries.Count - 1)
                    {
                        output.Append(",\n");
                    }
                }

                output.Append("\n}");
                break;
            }

            case VariantType.Array:
            {
                IReadOnlyList<Variant> items = value.AsArray();
                output.Append("[ ");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0)
                    {
                        output.Append(", ");
                    }

                    Write(output, items[i], engineMajor, style, objectResolver);
                }

                output.Append(" ]");
                break;
            }

            case VariantType.PoolByteArray:
            {
                byte[] bytes = (byte[])value.Value!;
                output.Append(PackedName(VariantType.PoolByteArray, engineMajor) + "( ");
                for (int i = 0; i < bytes.Length; i++)
                {
                    if (i > 0)
                    {
                        output.Append(", ");
                    }

                    output.Append(bytes[i].ToString(CultureInfo.InvariantCulture));
                }

                output.Append(" )");
                break;
            }

            case VariantType.PoolIntArray:
            {
                int[] values = (int[])value.Value!;
                WriteNumbers(output, PackedName(VariantType.PoolIntArray, engineMajor),
                    values.Select(v => v.ToString(CultureInfo.InvariantCulture)), script);
                break;
            }

            case VariantType.PoolRealArray:
            {
                float[] values = (float[])value.Value!;
                WriteNumbers(output, PackedName(VariantType.PoolRealArray, engineMajor),
                    values.Select(v => FormatNumber(v, script)), script);
                break;
            }

            case VariantType.PoolStringArray:
            {
                string[] values = (string[])value.Value!;
                output.Append(PackedName(VariantType.PoolStringArray, engineMajor) + "( ");
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                    {
                        output.Append(", ");
                    }

                    output.Append('"').Append(Escape(values[i])).Append('"');
                }

                output.Append(" )");
                break;
            }

            case VariantType.PoolInt64Array:
            {
                long[] values = (long[])value.Value!;
                WriteNumbers(output, PackedName(VariantType.PoolInt64Array, engineMajor),
                    values.Select(v => v.ToString(CultureInfo.InvariantCulture)), script);
                break;
            }

            case VariantType.PoolFloat64Array:
            {
                double[] values = (double[])value.Value!;
                WriteNumbers(output, PackedName(VariantType.PoolFloat64Array, engineMajor),
                    values.Select(v => FormatNumber(v, script)), script);
                break;
            }

            case VariantType.PoolVector2Array:
            case VariantType.PoolVector3Array:
            case VariantType.PoolVector4Array:
            case VariantType.PoolColorArray:
            {
                // Godot's text format flattens struct arrays into plain numbers:
                // PoolColorArray( 1, 0, 0, 1, 0, 1, 0, 1 ).
                string name = PackedName(value.Type, engineMajor);
                float[] values = value.AsFloats();
                output.Append(name + "( ");
                for (int i = 0; i < values.Length; i++)
                {
                    if (i > 0)
                    {
                        output.Append(", ");
                    }

                    output.Append(FormatNumber(values[i], script));
                }

                output.Append(" )");
                break;
            }

            default:
                throw new VariantException($"cannot write a {value.Type} as text");
        }
    }

    private static void WriteObject(StringBuilder output, Variant value, int engineMajor, GodotTextStyle style)
    {
        GodotObject? obj = value.AsObject();
        if (obj is null)
        {
            output.Append("null");
            return;
        }

        output.Append("Object(").Append(obj.ClassName).Append(',');
        for (int i = 0; i < obj.Properties.Count; i++)
        {
            if (i > 0)
            {
                output.Append(',');
            }

            output.Append('"').Append(Escape(obj.Properties[i].Key)).Append("\":");
            Write(output, obj.Properties[i].Value, engineMajor, style);
        }

        output.Append(')');
    }

    private static void WriteVector(StringBuilder output, string name, float[] values, bool script)
    {
        output.Append(name + "( ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                output.Append(", ");
            }

            output.Append(FormatNumber(values[i], script));
        }

        output.Append(" )");
    }

    private static void WriteNumbers(StringBuilder output, string name, IEnumerable<string> values, bool script)
    {
        output.Append(name + "( ");
        bool first = true;
        foreach (string value in values)
        {
            if (!first)
            {
                output.Append(", ");
            }

            first = false;
            output.Append(value);
        }

        output.Append(" )");
    }

    private static void WriteIntVector(StringBuilder output, string name, int[] values)
    {
        output.Append(name + "( ");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
            {
                output.Append(", ");
            }

            output.Append(values[i].ToString(CultureInfo.InvariantCulture));
        }

        output.Append(" )");
    }

    /// <summary>
    /// Name of a packed array in the given engine generation: Godot 3 calls them
    /// <c>Pool*Array</c>, Godot 4 <c>Packed*Array</c> (and splits ints/floats by width).
    /// </summary>
    public static string PackedName(VariantType type, int engineMajor)
    {
        if (engineMajor < 4)
        {
            return TypeName(type);
        }

        return type switch
        {
            VariantType.PoolByteArray => "PackedByteArray",
            VariantType.PoolIntArray => "PackedInt32Array",
            VariantType.PoolInt64Array => "PackedInt64Array",
            VariantType.PoolRealArray => "PackedFloat32Array",
            VariantType.PoolFloat64Array => "PackedFloat64Array",
            VariantType.PoolStringArray => "PackedStringArray",
            VariantType.PoolVector2Array => "PackedVector2Array",
            VariantType.PoolVector3Array => "PackedVector3Array",
            VariantType.PoolVector4Array => "PackedVector4Array",
            VariantType.PoolColorArray => "PackedColorArray",
            _ => TypeName(type),
        };
    }

    public static string TypeName(VariantType type) => type switch
    {
        VariantType.Nil => "Nil",
        VariantType.Bool => "bool",
        VariantType.Int => "int",
        VariantType.Real => "float",
        VariantType.String => "String",
        VariantType.Vector2 => "Vector2",
        VariantType.Rect2 => "Rect2",
        VariantType.Vector3 => "Vector3",
        VariantType.Transform2D => "Transform2D",
        VariantType.Plane => "Plane",
        VariantType.Quat => "Quat",
        VariantType.Aabb => "AABB",
        VariantType.Basis => "Basis",
        VariantType.Transform => "Transform",
        VariantType.Color => "Color",
        VariantType.NodePath => "NodePath",
        VariantType.Rid => "RID",
        VariantType.Object => "Object",
        VariantType.Dictionary => "Dictionary",
        VariantType.Array => "Array",
        VariantType.PoolByteArray => "PoolByteArray",
        VariantType.PoolIntArray => "PoolIntArray",
        VariantType.PoolRealArray => "PoolRealArray",
        VariantType.PoolStringArray => "PoolStringArray",
        VariantType.PoolVector2Array => "PoolVector2Array",
        VariantType.PoolVector3Array => "PoolVector3Array",
        VariantType.PoolColorArray => "PoolColorArray",
        VariantType.Vector2i => "Vector2i",
        VariantType.Rect2i => "Rect2i",
        VariantType.Vector3i => "Vector3i",
        VariantType.Vector4 => "Vector4",
        VariantType.Vector4i => "Vector4i",
        VariantType.Projection => "Projection",
        VariantType.StringName => "StringName",
        VariantType.Callable => "Callable",
        VariantType.Signal => "Signal",
        VariantType.PoolInt64Array => "PoolInt64Array",
        VariantType.PoolFloat64Array => "PoolFloat64Array",
        VariantType.PoolVector4Array => "PoolVector4Array",
        _ => type.ToString(),
    };

    /// <summary>
    /// Formats a float the way Godot's <c>String::num_scientific</c> does: the shortest
    /// round-tripping form, lowercase exponent, and a trailing <c>.0</c> when the result
    /// would otherwise parse as an integer.
    /// </summary>
    public static string FormatNumber(double value, bool script = true)
    {
        if (value == 0)
        {
            // Avoid "-0", which would show up as a bogus diff on every recovery.\n            return "0";
        }

        if (double.IsNaN(value))
        {
            return script ? "NAN" : "nan";
        }

        if (double.IsInfinity(value))
        {
            return value < 0 ? (script ? "-INF" : "-inf") : (script ? "INF" : "inf");
        }

        string text = (double)(float)value == value
            ? ((float)value).ToString("R", CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);

        if (text.Contains('E'))
        {
            int exponentIndex = text.IndexOf('E');
            string mantissa = text[..exponentIndex];
            string exponent = text[(exponentIndex + 1)..];
            string sign = exponent.StartsWith('-') ? "-" : "+";
            string digits = exponent.TrimStart('+', '-');
            if (digits.Length < 2)
            {
                digits = digits.PadLeft(2, '0');
            }

            text = mantissa + "e" + sign + digits;
        }

        return text;
    }

    /// <summary>A scalar REAL constant: the number, plus <c>.0</c> when it would parse as an int.</summary>
    public static string FormatRealConstant(double value, bool script = true)
    {
        string text = FormatNumber(value, script);
        if (IsPlainInteger(text))
        {
            text += ".0";
        }

        return text;
    }

    private static bool IsPlainInteger(string text)
    {
        int start = text.Length > 0 && (text[0] == '-' || text[0] == '+') ? 1 : 0;
        if (start >= text.Length)
        {
            return false;
        }

        for (int i = start; i < text.Length; i++)
        {
            if (!char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Godot's <c>c_escape</c>, used for both strings and node paths.</summary>
    public static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}