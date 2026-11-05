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
        var builder = new StringBuilder();
        Write(builder, value, engineMajor, style);
        return builder.ToString();
    }

    public static void Write(StringBuilder output, Variant value, int engineMajor = 3, GodotTextStyle style = GodotTextStyle.Script)
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
                WriteVector(output, engineMajor == 2 ? "Quat" : "Quat", value.AsFloats(), script);
                break;

            case VariantType.Aabb:
                WriteVector(output, "AABB", value.AsFloats(), script);
                break;

            case VariantType.Basis:
                WriteVector(output, "Basis", value.AsFloats(), script);
                break;

            case VariantType.Transform:
                WriteVector(output, "Transform", value.AsFloats(), script);
                break;

            case VariantType.Color:
                WriteVector(output, "Color", value.AsFloats(), script);
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
                WriteObject(output, value, engineMajor, style);
                break;

            case VariantType.Dictionary:
            {
                IReadOnlyList<KeyValuePair<Variant, Variant>> entries = value.AsDictionary();
                output.Append("{\n");
                for (int i = 0; i < entries.Count; i++)
                {
                    Write(output, entries[i].Key, engineMajor, style);
                    output.Append(": ");
                    Write(output, entries[i].Value, engineMajor, style);
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

                    Write(output, items[i], engineMajor, style);
                }

                output.Append(" ]");
                break;
            }

            case VariantType.PoolByteArray:
            {
                byte[] bytes = (byte[])value.Value!;
                output.Append("PoolByteArray( ");
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
                WriteNumbers(output, "PoolIntArray", values.Select(v => v.ToString(CultureInfo.InvariantCulture)), script);
                break;
            }

            case VariantType.PoolRealArray:
            {
                float[] values = (float[])value.Value!;
                WriteNumbers(output, "PoolRealArray", values.Select(v => FormatNumber(v, script)), script);
                break;
            }

            case VariantType.PoolStringArray:
            {
                string[] values = (string[])value.Value!;
                output.Append("PoolStringArray( ");
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

            case VariantType.PoolVector2Array:
            case VariantType.PoolVector3Array:
            case VariantType.PoolColorArray:
            {
                int width = value.Type switch
                {
                    VariantType.PoolVector2Array => 2,
                    VariantType.PoolVector3Array => 3,
                    _ => 4,
                };

                string element = value.Type switch
                {
                    VariantType.PoolVector2Array => "Vector2",
                    VariantType.PoolVector3Array => "Vector3",
                    _ => "Color",
                };

                string name = TypeName(value.Type);
                float[] values = value.AsFloats();
                output.Append(name + "( ");
                for (int i = 0; i + width <= values.Length; i += width)
                {
                    if (i > 0)
                    {
                        output.Append(", ");
                    }

                    output.Append(element).Append('(');
                    for (int component = 0; component < width; component++)
                    {
                        if (component > 0)
                        {
                            output.Append(", ");
                        }

                        output.Append(FormatNumber(values[i + component], script));
                    }

                    output.Append(')');
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