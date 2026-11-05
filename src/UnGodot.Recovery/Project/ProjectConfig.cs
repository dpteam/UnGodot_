using System.Buffers.Binary;
using System.Text;
using UnGodot.Variants;

namespace UnGodot.Recovery.Project;

public sealed class ProjectConfigException : Exception
{
    public ProjectConfigException(string message) : base(message)
    {
    }
}

/// <summary>
/// Godot's project settings, read from <c>project.binary</c> (<c>ECFG</c>) and written back as
/// an editor-openable <c>project.godot</c>.
///
/// The binary layout is: <c>"ECFG"</c>, a u32 count, then per setting a pascal string key and
/// a length-prefixed encoded variant. The text layout follows Godot's own writer: one section
/// per first path segment, settings in their original order, values in config syntax.
/// </summary>
public sealed class ProjectConfig
{
    private const uint EcFgMagic = 0x47464345; // "ECFG" little endian

    private readonly List<KeyValuePair<string, Variant>> _settings;

    private ProjectConfig(int engineMajor, int engineMinor, List<KeyValuePair<string, Variant>> settings)
    {
        EngineMajor = engineMajor;
        EngineMinor = engineMinor;
        _settings = settings;
    }

    public int EngineMajor { get; }

    public int EngineMinor { get; }

    public IReadOnlyList<KeyValuePair<string, Variant>> Settings => _settings;

    public int ConfigVersion => EngineMajor switch
    {
        4 => 5,
        3 => EngineMinor == 0 ? 3 : 4,
        2 => 2,
        _ => 1,
    };

    public Variant? Get(string key)
    {
        foreach (KeyValuePair<string, Variant> setting in _settings)
        {
            if (setting.Key == key)
            {
                return setting.Value;
            }
        }

        return null;
    }

    public static ProjectConfig LoadEcFg(string path, int engineMajor = 3, int engineMinor = 0)
    {
        byte[] data = File.ReadAllBytes(path);
        return ParseEcFg(data, engineMajor, engineMinor);
    }

    public static ProjectConfig ParseEcFg(ReadOnlySpan<byte> data, int engineMajor = 3, int engineMinor = 0)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(data) != EcFgMagic)
        {
            throw new ProjectConfigException("not a Godot binary project config (missing ECFG header)");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if (count > 1_000_000)
        {
            throw new ProjectConfigException($"implausible project settings count {count}");
        }

        var settings = new List<KeyValuePair<string, Variant>>((int)count);
        int position = 8;

        for (uint i = 0; i < count; i++)
        {
            int keyLength = ReadInt32(data, ref position);
            if (keyLength < 0 || position + keyLength > data.Length)
            {
                throw new ProjectConfigException($"setting {i} has an invalid key length");
            }

            string key = Encoding.UTF8.GetString(data.Slice(position, keyLength));
            position += keyLength;

            int valueLength = ReadInt32(data, ref position);
            if (valueLength < 0 || position + valueLength > data.Length)
            {
                throw new ProjectConfigException($"setting '{key}' has an invalid value length");
            }

            var reader = new VariantReader(data.Slice(position, valueLength), engineMajor);
            Variant value;
            try
            {
                value = reader.Read();
            }
            catch (VariantException ex)
            {
                throw new ProjectConfigException($"cannot decode setting '{key}': {ex.Message}");
            }

            position += valueLength;
            settings.Add(new KeyValuePair<string, Variant>(key, value));
        }

        return new ProjectConfig(engineMajor, engineMinor, settings);
    }

    /// <summary>Renders the settings as the <c>project.godot</c> the editor expects.</summary>
    public string ToProjectGodotText()
    {
        var output = new StringBuilder(16384);
        output.Append("; Engine configuration file.\n");
        output.Append("; It's best edited using the editor UI and not directly,\n");
        output.Append("; since the parameters that go here are not all obvious.\n");
        output.Append(";\n");
        output.Append("; Format:\n");
        output.Append(";   [section] ; section goes between []\n");
        output.Append(";   param=value ; assign values to parameters\n");
        output.Append('\n');
        output.Append("config_version=").Append(ConfigVersion).Append("\n\n");

        // Group by the first path segment, keeping the first appearance order of a section
        // and the original order inside it.
        var categories = new List<string>();
        var grouped = new Dictionary<string, List<KeyValuePair<string, Variant>>>(StringComparer.Ordinal);

        foreach (KeyValuePair<string, Variant> setting in _settings)
        {
            int slash = setting.Key.IndexOf('/');
            string category = slash < 0 ? string.Empty : setting.Key[..slash];
            string name = slash < 0 ? setting.Key : setting.Key[(slash + 1)..];

            if (!grouped.TryGetValue(category, out List<KeyValuePair<string, Variant>>? entries))
            {
                entries = [];
                grouped[category] = entries;
                categories.Add(category);
            }

            entries.Add(new KeyValuePair<string, Variant>(name, setting.Value));
        }

        // Godot's own writer iterates a sorted map, so sections come out alphabetically and
        // the top-level block (no section) comes first.
        categories.Sort(StringComparer.Ordinal);

        bool first = true;
        foreach (string category in categories)
        {
            if (category.Length > 0)
            {
                if (!first)
                {
                    output.Append('\n');
                }

                output.Append('[').Append(category).Append("]\n\n");
            }

            foreach (KeyValuePair<string, Variant> entry in grouped[category])
            {
                output.Append(EncodeKey(entry.Key)).Append('=');
                output.Append(VariantTextWriter.Write(entry.Value, EngineMajor, GodotTextStyle.Config));
                output.Append('\n');
            }

            first = false;
        }

        return output.ToString();
    }

    /// <summary>
    /// Godot's <c>String::property_name_encode</c>: quote names that contain characters the
    /// config parser would choke on.
    /// </summary>
    public static string EncodeKey(string name)
    {
        foreach (char c in name)
        {
            if (c is '=' or '"' or < ' ' or > '~')
            {
                return "\"" + VariantTextWriter.Escape(name) + "\"";
            }
        }

        return name;
    }

    private static int ReadInt32(ReadOnlySpan<byte> data, ref int position)
    {
        if (position + 4 > data.Length)
        {
            throw new ProjectConfigException("the project config is truncated");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        position += 4;
        return value;
    }
}