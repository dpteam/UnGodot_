using System.Buffers.Binary;
using System.Text;
using UnGodot.Resources;
using UnGodot.Variants;
using Xunit;

namespace UnGodot.Tests;

/// <summary>
/// Godot 4 support: the variant ids of <c>resource_format_binary.cpp</c>, the 4.x resource
/// header and the <c>format=3</c> text output with string resource ids.
/// </summary>
public class Godot4Tests
{
    // -------------------------------------------------------------------------------------
    // Builders
    // -------------------------------------------------------------------------------------

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] I32(int value) => BitConverter.GetBytes(value);

    private static byte[] U64(ulong value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A Godot unicode string: u32 length including the NUL, then the bytes.</summary>
    private static byte[] Text(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32((uint)(utf8.Length + 1)), .. utf8, 0];
    }

    /// <summary>An inline <c>_get_string</c>: high bit set, length including NUL, then bytes.</summary>
    private static byte[] Inline(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32(0x8000_0000u | (uint)(utf8.Length + 1)), .. utf8, 0];
    }

    /// <summary>Resource-format variant ids (not contiguous), from <c>VARIANT_*</c>.</summary>
    private const uint VNil = 1;
    private const uint VBool = 2;
    private const uint VInt = 3;
    private const uint VReal = 4;
    private const uint VString = 5;
    private const uint VVector2 = 10;
    private const uint VVector2i = 45;
    private const uint VVector4 = 50;
    private const uint VColor = 20;
    private const uint VStringName = 44;
    private const uint VObject = 24;
    private const uint VDictionary = 26;
    private const uint VArray = 30;
    private const uint VPackedByte = 31;
    private const uint VPackedInt64 = 48;
    private const uint VPackedFloat64 = 49;
    private const uint VPackedVector4 = 53;

    private static byte[] VIntValue(int value) => Concat(U32(VInt), I32(value));

    private static byte[] VStringValue(string value) => Concat(U32(VString), Text(value));

    private static byte[] VObjectInternal(int index) => Concat(U32(VObject), U32(2), I32(index));

    private sealed record TestObject(string Type, string Path, IReadOnlyList<(string Name, byte[] Value)> Properties);

    /// <summary>
    /// Builds a Godot 4 <c>RSRC</c> file: magic, header with flags/uid/reserved fields, string
    /// table, ext resources (with uid), internal resources and objects.
    /// </summary>
    private static byte[] BuildGodot4Resource(
        string headerType,
        IReadOnlyList<(string Type, string Path)> externalResources,
        IReadOnlyList<TestObject> objects,
        uint flags = 3,
        ulong uid = ulong.MaxValue,
        string? scriptClass = null,
        bool realIsDouble = false)
    {
        var body = new List<byte>();
        body.AddRange(U32(0)); // big endian
        body.AddRange(U32(realIsDouble ? 1u : 0u));
        body.AddRange(U32(4)); // version major
        body.AddRange(U32(0)); // version minor
        body.AddRange(U32(6)); // format version
        body.AddRange(Text(headerType));
        body.AddRange(U64(0)); // import metadata offset
        body.AddRange(U32(flags));
        body.AddRange(U64(uid));
        if (scriptClass is not null)
        {
            body.AddRange(Text(scriptClass));
        }

        for (int i = 0; i < 11; i++)
        {
            body.AddRange(U32(0));
        }

        body.AddRange(U32(0)); // string table
        body.AddRange(U32((uint)externalResources.Count));
        foreach ((string type, string path) in externalResources)
        {
            body.AddRange(Text(type));
            body.AddRange(Text(path));
            if ((flags & 2) != 0)
            {
                body.AddRange(U64(ulong.MaxValue));
            }
        }

        body.AddRange(U32((uint)objects.Count));
        var offsetSlots = new List<int>();
        foreach (TestObject obj in objects)
        {
            body.AddRange(Text(obj.Path));
            offsetSlots.Add(body.Count);
            body.AddRange(U64(0));
        }

        var objectOffsets = new List<int>();
        foreach (TestObject obj in objects)
        {
            objectOffsets.Add(body.Count);
            body.AddRange(Text(obj.Type));
            body.AddRange(U32((uint)obj.Properties.Count));
            foreach ((string name, byte[] value) in obj.Properties)
            {
                body.AddRange(Inline(name));
                body.AddRange(value);
            }
        }

        byte[] file = [.. "RSRC"u8, .. body];
        for (int i = 0; i < objects.Count; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(
                file.AsSpan(4 + offsetSlots[i]), (ulong)(4 + objectOffsets[i]));
        }

        return file;
    }

    // -------------------------------------------------------------------------------------
    // Variant decoding
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ReadsGodot4ResourceVariants()
    {
        byte[] stream = Concat(
            U32(VNil),
            U32(VBool), U32(1),
            VIntValue(42),
            U32(40), BitConverter.GetBytes(5_000_000_000L), // int64
            U32(VReal), BitConverter.GetBytes(0.5f),
            U32(41), BitConverter.GetBytes(2.25), // double
            VStringValue("hi"),
            Concat(U32(VStringName), Text("name")),
            Concat(U32(VVector2), BitConverter.GetBytes(1f), BitConverter.GetBytes(2f)),
            Concat(U32(VVector2i), I32(3), I32(4)),
            Concat(U32(VVector4), BitConverter.GetBytes(1f), BitConverter.GetBytes(2f),
                BitConverter.GetBytes(3f), BitConverter.GetBytes(4f)),
            Concat(U32(VColor), BitConverter.GetBytes(0.5f), BitConverter.GetBytes(0.5f),
                BitConverter.GetBytes(0.5f), BitConverter.GetBytes(1f)),
            Concat(U32(VPackedByte), I32(3), new byte[] { 1, 2, 3 }, new byte[1]), // padded
            Concat(U32(VPackedInt64), I32(2), BitConverter.GetBytes(7L), BitConverter.GetBytes(8L)),
            Concat(U32(VPackedFloat64), I32(1), BitConverter.GetBytes(1.5)),
            Concat(U32(VPackedVector4), I32(1), BitConverter.GetBytes(1f), BitConverter.GetBytes(2f),
                BitConverter.GetBytes(3f), BitConverter.GetBytes(4f)));

        var reader = new VariantReader(stream, VariantFormat.Godot4Resource);

        Assert.True(reader.Read().IsNil);
        Assert.True(reader.Read().AsBool());
        Assert.Equal(42, reader.Read().AsInt());
        Assert.Equal(5_000_000_000L, reader.Read().AsInt());
        Assert.Equal(0.5, reader.Read().AsDouble(), 6);
        Assert.Equal(2.25, reader.Read().AsDouble(), 6);
        Assert.Equal("hi", reader.Read().AsString());
        Variant stringName = reader.Read();
        Assert.Equal(VariantType.StringName, stringName.Type);
        Assert.Equal("name", stringName.AsString());

        Variant v2 = reader.Read();
        Assert.Equal(VariantType.Vector2, v2.Type);

        Variant v2i = reader.Read();
        Assert.Equal(VariantType.Vector2i, v2i.Type);
        Assert.Equal(new[] { 3, 4 }, (int[])v2i.Value!);

        Variant v4 = reader.Read();
        Assert.Equal(VariantType.Vector4, v4.Type);
        Assert.Equal(4, v4.AsFloats().Length);

        Variant color = reader.Read();
        Assert.Equal(VariantType.Color, color.Type);

        Variant bytes = reader.Read();
        Assert.Equal(new byte[] { 1, 2, 3 }, (byte[])bytes.Value!);

        Variant int64s = reader.Read();
        Assert.Equal(new long[] { 7, 8 }, (long[])int64s.Value!);

        Variant float64s = reader.Read();
        Assert.Equal(1.5, ((double[])float64s.Value!)[0], 6);

        Variant vector4s = reader.Read();
        Assert.Equal(4, vector4s.AsFloats().Length);
        Assert.Equal(stream.Length, reader.Position);
    }

    [Fact]
    public void ReadsGodot4MarshallsVariants()
    {
        const uint flag64 = 1 << 16;

        byte[] stream = Concat(
            U32(2), // Variant::INT
            I32(7),
            U32(2 | flag64),
            BitConverter.GetBytes(9_000_000_000L),
            U32(4), // Variant::STRING, padded to 4 bytes
            I32(3), Encoding.UTF8.GetBytes("abc"), new byte[1],
            U32(6), // Variant::VECTOR2I
            I32(1), I32(2),
            U32(38), // Variant::PACKED_VECTOR4_ARRAY
            I32(1), BitConverter.GetBytes(1f), BitConverter.GetBytes(2f),
            BitConverter.GetBytes(3f), BitConverter.GetBytes(4f));

        var reader = new VariantReader(stream, engineMajor: 4);

        Assert.Equal(7, reader.Read().AsInt());
        Assert.Equal(9_000_000_000L, reader.Read().AsInt());
        Assert.Equal("abc", reader.Read().AsString());

        Variant v2i = reader.Read();
        Assert.Equal(VariantType.Vector2i, v2i.Type);
        Assert.Equal(new[] { 1, 2 }, (int[])v2i.Value!);

        Variant packed = reader.Read();
        Assert.Equal(VariantType.PoolVector4Array, packed.Type);
        Assert.Equal(4, packed.AsFloats().Length);
        Assert.Equal(stream.Length, reader.Position);
    }

    [Fact]
    public void ReadsGodot4ProjectBinary()
    {
        byte[] key = Encoding.UTF8.GetBytes("application/config/name");

        // Marshalls string: length includes the NUL and the payload is padded to 4 bytes.
        byte[] value = Concat(U32(4), I32(5), Encoding.UTF8.GetBytes("game"), new byte[3]);

        byte[] ecfg = Concat(
            "ECFG"u8.ToArray(),
            U32(1),
            U32((uint)key.Length), key,
            U32((uint)value.Length), value);

        UnGodot.Recovery.Project.ProjectConfig config =
            UnGodot.Recovery.Project.ProjectConfig.ParseEcFg(ecfg, engineMajor: 4);

        Assert.Equal(4, config.EngineMajor);
        Assert.Equal(5, config.ConfigVersion);
        Assert.Equal("game", config.Get("application/config/name")!.AsString());
    }

    [Fact]
    public void WritesGodot4TypeNames()
    {
        Assert.Equal("PackedVector3Array", VariantTextWriter.PackedName(VariantType.PoolVector3Array, 4));
        Assert.Equal("PackedInt32Array", VariantTextWriter.PackedName(VariantType.PoolIntArray, 4));
        Assert.Equal("PoolVector3Array", VariantTextWriter.PackedName(VariantType.PoolVector3Array, 3));
        Assert.Equal(
            "Transform3D( 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 )",
            VariantTextWriter.Write(new Variant(VariantType.Transform, new float[12]), 4));
        Assert.Equal("Quaternion( 0, 0, 0, 1 )",
            VariantTextWriter.Write(new Variant(VariantType.Quat, new[] { 0f, 0f, 0f, 1f }), 4));
        Assert.Equal("&\"n\"", VariantTextWriter.Write(new Variant(VariantType.StringName, "n"), 4));
        Assert.Equal("Vector4i( 1, 2, 3, 4 )",
            VariantTextWriter.Write(new Variant(VariantType.Vector4i, new[] { 1, 2, 3, 4 }), 4));
    }

    // -------------------------------------------------------------------------------------
    // Resources
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ReadsGodot4ResourceHeader()
    {
        byte[] file = BuildGodot4Resource(
            "Resource",
            [("Script", "res://a.gd")],
            [new TestObject("Resource", "res://thing.tres", [("count", VIntValue(5))])],
            flags: 1 | 2 | 8,
            uid: 1234,
            scriptClass: "MyClass");

        BinaryResource resource = BinaryResource.Load(file);

        Assert.Equal(4, resource.VersionMajor);
        Assert.Equal(6, resource.FormatVersion);
        Assert.Equal("Resource", resource.Type);
        Assert.Equal(1234UL, resource.Uid);
        Assert.Equal("MyClass", resource.ScriptClass);
        Assert.True(resource.UsesUids);
        Assert.Equal("res://a.gd", resource.ExternalResources[0].Path);
        Assert.Equal(5, resource.Main!.Properties[0].Value.AsInt());
    }

    [Fact]
    public void WritesGodot4ResourceText()
    {
        byte[] file = BuildGodot4Resource(
            "Resource",
            [("Texture2D", "res://art/hero.png")],
            [
                new TestObject("Gradient", "local://Gradient_x", [("offsets", Concat(U32(33), I32(2),
                    BitConverter.GetBytes(0f), BitConverter.GetBytes(1f)))]),
                new TestObject("Resource", "res://thing.tres", [
                    ("gradient", VObjectInternal(0)),
                    ("count", VIntValue(7)),
                ]),
            ]);

        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(file));

        Assert.Contains("[gd_resource type=\"Resource\" load_steps=3 format=3]", text.Text);
        Assert.Contains("[ext_resource type=\"Texture2D\" path=\"res://art/hero.png\" id=\"1_hero\"]", text.Text);
        Assert.Contains("[sub_resource type=\"Gradient\" id=\"Gradient_x\"]", text.Text);
        Assert.Contains("offsets = PackedFloat32Array( 0, 1 )", text.Text);
        Assert.Contains("gradient = SubResource(\"Gradient_x\")", text.Text);
        Assert.Contains("count = 7", text.Text);
    }

    [Fact]
    public void WritesGodot4SceneConnectionsWithUnbinds()
    {
        byte[] names = Concat(U32(34), I32(4), Text("Root"), Text("Node2D"), Text("Child"), Text("ready"));

        byte[] bundled = Concat(
            U32(VDictionary), U32(5),
            VStringValue("names"), names,
            VStringValue("variants"), Concat(U32(VArray), U32(0)),
            VStringValue("nodes"), Concat(U32(32), I32(14),
                I32(-1), I32(-1), I32(1), I32(0), I32(-1), I32(0), I32(0),
                I32(0), I32(0), I32(1), I32(2), I32(-1), I32(0), I32(0)),
            VStringValue("conns"), Concat(U32(32), I32(7), I32(0), I32(0), I32(3), I32(3), I32(0), I32(0), I32(1)),
            VStringValue("version"), VIntValue(3));

        byte[] file = BuildGodot4Resource(
            "PackedScene", [], [new TestObject("PackedScene", "res://s.tscn", [("_bundled", bundled)])]);

        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(file));

        Assert.True(text.IsScene);
        Assert.Contains("[gd_scene format=3]", text.Text);
        Assert.Contains("[node name=\"Root\" type=\"Node2D\"]", text.Text);
        Assert.Contains("[node name=\"Child\" parent=\".\" type=\"Node2D\"]", text.Text);
        Assert.Contains("method=\"ready\" unbinds=1]", text.Text);
    }
}