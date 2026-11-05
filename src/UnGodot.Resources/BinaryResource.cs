using System.Buffers.Binary;
using System.Text;
using UnGodot.Variants;

namespace UnGodot.Resources;

public sealed class ExternalResourceEntry
{
    public required string Type { get; init; }
    public required string Path { get; init; }

    /// <summary>Godot 4 resource UID (<c>0xFFFFFFFFFFFFFFFF</c> when the file has none).</summary>
    public ulong Uid { get; init; } = BinaryResource.InvalidUid;
}

public sealed class InternalResourceEntry
{
    public required string Path { get; init; }
    public required long Offset { get; init; }
}

public sealed class BinaryObject
{
    public required string Type { get; init; }
    public required string Path { get; init; }
    public required long Offset { get; init; }
    public List<KeyValuePair<string, Variant>> Properties { get; } = [];
}

/// <summary>
/// A parsed Godot binary resource (<c>.res</c>, <c>.scn</c>, <c>.material</c>,
/// <c>*.converted.res</c>): header, string table, external/internal resource tables and the
/// objects with their property lists. Godot 2/3 (format 0-3) and Godot 4 (format 4-6) are
/// both supported.
/// </summary>
public sealed class BinaryResource
{
    /// <summary>Godot 4's "no uid" sentinel.</summary>
    public const ulong InvalidUid = ulong.MaxValue;

    private const uint FlagNamedSceneIds = 1;
    private const uint FlagUids = 2;
    private const uint FlagRealTIsDouble = 4;
    private const uint FlagHasScriptClass = 8;
    private const int ReservedFieldsGodot4 = 11;

    private BinaryResource()
    {
    }

    public int VersionMajor { get; private init; }
    public int VersionMinor { get; private init; }
    public int FormatVersion { get; private init; }
    public bool RealIsDouble { get; private set; }
    public bool BigEndian { get; private init; }

    /// <summary>Godot 4 header flags (0 for Godot 2/3 files).</summary>
    public uint Flags { get; private set; }

    /// <summary>Godot 4 resource UID, or <see cref="InvalidUid"/>.</summary>
    public ulong Uid { get; private set; } = InvalidUid;

    /// <summary>Godot 4 global script class name, when the header stores one.</summary>
    public string? ScriptClass { get; private set; }

    /// <summary>True when the file references resources by UID as well as by path.</summary>
    public bool UsesUids => (Flags & FlagUids) != 0;

    /// <summary>The class name stored in the header (Sonic's converted files leave it empty).</summary>
    public string Type { get; private set; } = string.Empty;

    public List<string> Strings { get; } = [];
    public List<ExternalResourceEntry> ExternalResources { get; } = [];
    public List<InternalResourceEntry> InternalResources { get; } = [];
    public List<BinaryObject> Objects { get; } = [];

    /// <summary>The main resource is always the last internal entry.</summary>
    public BinaryObject? Main => Objects.Count > 0 ? Objects[^1] : null;

    public static bool LooksLikeResource(ReadOnlySpan<byte> data) => ResourceContainer.LooksLikeResource(data);

    public static BinaryResource Load(ReadOnlySpan<byte> file)
    {
        byte[] body = ResourceContainer.ReadBody(file, out int offsetBase);
        return Parse(body, offsetBase);
    }

    public static BinaryResource Load(string path) => Load(File.ReadAllBytes(path));

    /// <param name="body">The resource stream starting at <c>big_endian</c>.</param>
    /// <param name="offsetBase">
    /// Base for internal object offsets: 4 for uncompressed <c>RSRC</c> files (offsets count
    /// from the file start and therefore include the magic), 0 for <c>RSCC</c> (offsets count
    /// from the decompressed stream).
    /// </param>
    public static BinaryResource Parse(byte[] body, int offsetBase = 0)
    {
        int position = 0;
        var resource = new BinaryResource
        {
            BigEndian = ReadU32(body, ref position) != 0,
            RealIsDouble = ReadU32(body, ref position) != 0,
            VersionMajor = (int)ReadU32(body, ref position),
            VersionMinor = (int)ReadU32(body, ref position),
            FormatVersion = (int)ReadU32(body, ref position),
        };

        if (resource.BigEndian)
        {
            throw new ResourceFormatException("big-endian binary resources are not supported");
        }

        if (resource.VersionMajor > 4 || resource.FormatVersion > 6)
        {
            throw new ResourceFormatException(
                $"unsupported binary resource version {resource.VersionMajor}.{resource.VersionMinor} format {resource.FormatVersion}");
        }

        string type = ReadUnicodeString(body, ref position);
        resource.Type = type;

        _ = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(position));
        position += 8; // import metadata offset

        if (resource.VersionMajor >= 4)
        {
            ParseGodot4Header(body, ref position, resource);
        }
        else
        {
            for (int i = 0; i < 14; i++)
            {
                _ = ReadU32(body, ref position); // reserved
            }
        }

        uint stringCount = ReadU32(body, ref position);
        if (stringCount > 1_000_000)
        {
            throw new ResourceFormatException($"implausible string table size {stringCount}");
        }

        for (uint i = 0; i < stringCount; i++)
        {
            resource.Strings.Add(ReadUnicodeString(body, ref position));
        }

        uint externalCount = ReadU32(body, ref position);
        if (externalCount > 1_000_000)
        {
            throw new ResourceFormatException($"implausible external resource count {externalCount}");
        }

        for (uint i = 0; i < externalCount; i++)
        {
            var entry = new ExternalResourceEntry
            {
                Type = ReadUnicodeString(body, ref position),
                Path = ReadUnicodeString(body, ref position),
            };

            if (resource.UsesUids)
            {
                entry = new ExternalResourceEntry { Type = entry.Type, Path = entry.Path, Uid = ReadU64(body, ref position) };
            }

            resource.ExternalResources.Add(entry);
        }

        uint internalCount = ReadU32(body, ref position);
        if (internalCount > 1_000_000)
        {
            throw new ResourceFormatException($"implausible internal resource count {internalCount}");
        }

        for (uint i = 0; i < internalCount; i++)
        {
            resource.InternalResources.Add(new InternalResourceEntry
            {
                Path = ReadUnicodeString(body, ref position),
                Offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(position)),
            });
            position += 8;
        }

        foreach (InternalResourceEntry entry in resource.InternalResources)
        {
            long bodyOffset = entry.Offset - offsetBase;
            if (bodyOffset < 0 || bodyOffset + 8 > body.Length)
            {
                throw new ResourceFormatException($"internal resource '{entry.Path}' points outside the file");
            }

            int objectPosition = (int)bodyOffset;
            string objectType = ReadUnicodeString(body, ref objectPosition);
            var obj = new BinaryObject
            {
                Type = objectType,
                Path = entry.Path,
                Offset = entry.Offset,
            };

            uint propertyCount = ReadU32(body, ref objectPosition);
            if (propertyCount > 1_000_000)
            {
                throw new ResourceFormatException($"implausible property count {propertyCount} in '{entry.Path}'");
            }

            var reader = new ResourceVariantReader(
                body, objectPosition, resource.Strings, resource.RealIsDouble, resource.FormatVersion,
                resource.VersionMajor);

            for (uint i = 0; i < propertyCount; i++)
            {
                string name = reader.ReadStringRef();
                Variant value = reader.Read();
                obj.Properties.Add(new KeyValuePair<string, Variant>(name, value));
            }

            resource.Objects.Add(obj);
        }

        return resource;
    }

    /// <summary>
    /// Reads the Godot 4 header tail: flags, uid, optional script class and the reserved
    /// fields (see <c>ResourceLoaderBinary::open</c> in <c>resource_format_binary.cpp</c>).
    /// </summary>
    private static void ParseGodot4Header(byte[] body, ref int position, BinaryResource resource)
    {
        resource.Flags = ReadU32(body, ref position);
        resource.RealIsDouble = (resource.Flags & FlagRealTIsDouble) != 0;
        resource.Uid = ReadU64(body, ref position);

        if ((resource.Flags & FlagHasScriptClass) != 0)
        {
            resource.ScriptClass = ReadUnicodeString(body, ref position);
        }

        for (int i = 0; i < ReservedFieldsGodot4; i++)
        {
            _ = ReadU32(body, ref position);
        }
    }

    internal static string ReadUnicodeString(byte[] data, ref int position)
    {
        int length = (int)ReadU32(data, ref position);
        if (length < 0 || position + length > data.Length)
        {
            throw new ResourceFormatException($"string at offset {position} runs past the end of the file");
        }

        string value = Encoding.UTF8.GetString(data, position, length);
        position += length;
        return value.TrimEnd('\0');
    }

    internal static uint ReadU32(byte[] data, ref int position)
    {
        if (position + 4 > data.Length)
        {
            throw new ResourceFormatException("the resource file is truncated");
        }

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
        position += 4;
        return value;
    }

    internal static ulong ReadU64(byte[] data, ref int position)
    {
        if (position + 8 > data.Length)
        {
            throw new ResourceFormatException("the resource file is truncated");
        }

        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(position));
        position += 8;
        return value;
    }

    public Variant? GetProperty(string name)
    {
        BinaryObject? main = Main;
        if (main is null)
        {
            return null;
        }

        foreach (KeyValuePair<string, Variant> property in main.Properties)
        {
            if (property.Key == name)
            {
                return property.Value;
            }
        }

        return null;
    }
}