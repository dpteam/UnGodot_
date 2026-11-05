using System.Buffers.Binary;
using System.Text;
using UnGodot.Recovery;
using UnGodot.Recovery.Stages;
using UnGodot.Resources;
using UnGodot.Textures;
using UnGodot.Variants;
using Xunit;
using ZstdSharp;

namespace UnGodot.Tests;

public class ResourceTests
{
    // -------------------------------------------------------------------------------------
    // Binary resource builders
    // -------------------------------------------------------------------------------------

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] I32(int value) => BitConverter.GetBytes(value);

    private static byte[] U64(ulong value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    /// <summary>A Godot "unicode string": u32 length (NUL included), the bytes and the NUL.</summary>
    private static byte[] Text(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32((uint)(utf8.Length + 1)), .. utf8, 0];
    }

    /// <summary>An inline <c>_get_string</c> reference: high bit set, length, bytes and NUL.</summary>
    private static byte[] Inline(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32(0x8000_0000u | (uint)(utf8.Length + 1)), .. utf8, 0];
    }

    /// <summary>A variant string: u32 length (NUL included), the bytes and the NUL.</summary>
    private static byte[] VString(string value) => Concat(U32(5), Text(value));

    private static byte[] VInt(int value) => Concat(U32(3), I32(value));

    private static byte[] VBool(bool value) => Concat(U32(2), U32(value ? 1u : 0u));

    private static byte[] VObjectEmpty() => Concat(U32(24), U32(0));

    private static byte[] VObjectInternal(int index) => Concat(U32(24), U32(2), I32(index));

    private static byte[] VObjectExternalIndex(int index) => Concat(U32(24), U32(3), I32(index));

    private static byte[] VArray(params byte[][] items) =>
        Concat(U32(30), U32((uint)items.Length), Concat(items));

    private static byte[] VStringArray(params string[] items) =>
        Concat(U32(34), I32(items.Length), Concat(items.Select(Text).ToArray()));

    private static byte[] VIntArray(params int[] items) =>
        Concat(U32(32), I32(items.Length), Concat(items.Select(I32).ToArray()));

    private static byte[] VDictionary(params (string Key, byte[] Value)[] entries) =>
        Concat(
            U32(26),
            U32((uint)entries.Length),
            Concat(entries.Select(entry => Concat(VString(entry.Key), entry.Value)).ToArray()));

    private sealed record TestObject(string Type, string Path, IReadOnlyList<(string Name, byte[] Value)> Properties);

    /// <summary>
    /// Builds a minimal uncompressed <c>RSRC</c> file. Object offsets are written the way the
    /// engine writes them: relative to the file start, magic included.
    /// </summary>
    private static byte[] BuildResource(
        string headerType,
        IReadOnlyList<(string Type, string Path)> externalResources,
        IReadOnlyList<TestObject> objects)
    {
        var body = new List<byte>();
        body.AddRange(U32(0)); // big endian
        body.AddRange(U32(0)); // real64
        body.AddRange(U32(3)); // version major
        body.AddRange(U32(1)); // version minor
        body.AddRange(U32(3)); // format version
        body.AddRange(Text(headerType));
        body.AddRange(U64(0)); // import metadata offset
        for (int i = 0; i < 14; i++)
        {
            body.AddRange(U32(0));
        }

        body.AddRange(U32(0)); // string table
        body.AddRange(U32((uint)externalResources.Count));
        foreach ((string type, string path) in externalResources)
        {
            body.AddRange(Text(type));
            body.AddRange(Text(path));
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
                body.AddRange(Inline(name)); // property names use _get_string (inline here)
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

    /// <summary>
    /// Builds an <c>RSCC</c> container exactly like <c>FileAccessCompressed::close</c>:
    /// header, a table with one size per block, the blocks, then the magic again.
    /// </summary>
    private static byte[] BuildCompressed(byte[] payload, int blockSize, bool frameContentSize)
    {
        int blockCount = payload.Length / blockSize + 1;
        var blocks = new List<byte[]>();
        var sizes = new List<byte>();
        for (int i = 0; i < blockCount; i++)
        {
            int length = i == blockCount - 1 ? payload.Length % blockSize : blockSize;
            byte[] compressed = Compress(payload.AsSpan(i * blockSize, length).ToArray(), frameContentSize);
            blocks.Add(compressed);
            sizes.AddRange(U32((uint)compressed.Length));
        }

        return
        [
            .. "RSCC"u8,
            .. U32(2), // mode: zstd
            .. U32((uint)blockSize),
            .. U32((uint)payload.Length),
            .. sizes,
            .. Concat(blocks.ToArray()),
            .. "RSCC"u8,
        ];
    }

    private static byte[] Compress(byte[] data, bool frameContentSize)
    {
        using var compressor = new Compressor();
        if (frameContentSize)
        {
            compressor.SetPledgedSrcSize((ulong)data.Length);
        }
        else
        {
            compressor.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
        }

        return compressor.Wrap(data).ToArray();
    }

    // -------------------------------------------------------------------------------------
    // Containers
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ReadsCompressedResourceBlockTable()
    {
        byte[] payload = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJ");
        byte[] file = BuildCompressed(payload, blockSize: 8, frameContentSize: true);
        Assert.Equal(payload, ResourceContainer.ReadBody(file));
    }

    [Fact]
    public void ReadsCompressedResourceWithoutFrameContentSize()
    {
        // Sonic writes frames without a content size; only the block table knows the size.
        byte[] payload = Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJ");
        byte[] file = BuildCompressed(payload, blockSize: 8, frameContentSize: false);
        Assert.Equal(payload, ResourceContainer.ReadBody(file));
    }

    [Fact]
    public void ReadsBlockTableWithEmptyTrailingBlock()
    {
        // Exactly a multiple of the block size: the engine still writes a zero-length block.
        byte[] payload = Encoding.ASCII.GetBytes("0123456789ABCDEF");
        byte[] file = BuildCompressed(payload, blockSize: 8, frameContentSize: true);
        Assert.Equal(payload, ResourceContainer.ReadBody(file));
    }

    [Fact]
    public void RejectsForeignFiles()
    {
        Assert.False(ResourceContainer.LooksLikeResource("not a resource"u8));
        Assert.Throws<ResourceFormatException>(() => ResourceContainer.ReadBody("nope"u8));
    }

    // -------------------------------------------------------------------------------------
    // Binary resources and text output
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ReadsInlinePropertyNamesAndFileRelativeObjectOffsets()
    {
        byte[] file = BuildResource(
            headerType: "",
            externalResources: [],
            objects:
            [
                new TestObject(
                    "DynamicFont",
                    "local://0",
                    [
                        ("size", VInt(42)),
                        ("use_filter", VBool(true)),
                    ]),
            ]);

        BinaryResource resource = BinaryResource.Load(file);

        Assert.Equal(3, resource.VersionMajor);
        Assert.Equal(1, resource.VersionMinor);
        Assert.Equal("DynamicFont", resource.Main!.Type);
        Assert.Equal(2, resource.Main.Properties.Count);
        Assert.Equal("size", resource.Main.Properties[0].Key);
        Assert.Equal(42, resource.Main.Properties[0].Value.AsInt());
        Assert.True(resource.Main.Properties[1].Value.AsBool());
    }

    [Fact]
    public void WritesExternalAndSparseInternalReferences()
    {
        byte[] file = BuildResource(
            headerType: "",
            externalResources: [("Shader", "res://shaders/a.shader")],
            objects:
            [
                new TestObject("Gradient", "local://37", [("interpolation", VInt(0))]),
                new TestObject(
                    "ShaderMaterial",
                    "res://thing.material",
                    [
                        ("next_pass", VObjectEmpty()),
                        ("shader", VObjectExternalIndex(0)),
                        ("gradient", VObjectInternal(37)),
                    ]),
            ]);

        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(file));

        Assert.False(text.IsScene);
        Assert.Empty(text.Notes);
        Assert.Contains("[gd_resource type=\"ShaderMaterial\" load_steps=3 format=2]", text.Text);
        Assert.Contains("[ext_resource path=\"res://shaders/a.shader\" type=\"Shader\" id=1]", text.Text);
        Assert.Contains("[sub_resource type=\"Gradient\" id=1]", text.Text);
        Assert.Contains("next_pass = null", text.Text);
        Assert.Contains("shader = ExtResource( 1 )", text.Text);
        Assert.Contains("gradient = SubResource( 1 )", text.Text);
    }

    [Fact]
    public void WritesPackedSceneNodesFromBundledDictionary()
    {
        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(MinimalScene()));

        Assert.True(text.IsScene);
        Assert.Contains("[gd_scene load_steps=1 format=2]", text.Text);
        Assert.Contains("[node name=\"Root\" type=\"Node2D\"]", text.Text);
        Assert.Contains("[node name=\"Child\" parent=\".\" type=\"Node2D\"]", text.Text);
    }

    [Fact]
    public async Task StageConvertsResourcesAndKeepsOriginals()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-resources", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(
                Path.Combine(root, "thing.material"),
                BuildResource(
                    "", [], [new TestObject("ShaderMaterial", "res://thing.material", [("next_pass", VObjectEmpty())])]));
            File.WriteAllBytes(Path.Combine(root, "level.scn"), MinimalScene());
            File.WriteAllBytes(Path.Combine(root, "junk.mesh"), [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(root, "already.tres"), "[gd_resource type=\"Resource\" format=2]\n");

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };
            var result = new RecoveryStageResult { Stage = "resources" };

            await new ResourceStage().RunAsync(context, result, CancellationToken.None);

            Assert.Contains("[gd_resource type=\"ShaderMaterial\"", File.ReadAllText(Path.Combine(root, "thing.tres")));
            Assert.Contains("[gd_scene", File.ReadAllText(Path.Combine(root, "level.tscn")));
            Assert.True(File.Exists(Path.Combine(root, "thing.material")), "the binary original must be kept");
            Assert.Equal(2, result.Counters["converted"]);
            Assert.Equal(1, result.Counters["scenes"]);
            Assert.Equal(1, result.Counters["not binary"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void WritesNodeGroupsInTheNodeHeader()
    {
        byte[] bundled = VDictionary(
            ("names", VStringArray("Root", "Node2D", "Enemies", "Pickups")),
            ("variants", VArray()),
            ("nodes", VIntArray(
                -1, -1, 1, 0, -1, 0, 2, 2, 3)), // two groups at the end of the node
            ("conns", VIntArray()));

        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(
            BuildResource("PackedScene", [], [new TestObject("PackedScene", "local://0", [("_bundled", bundled)])])));

        Assert.Contains("[node name=\"Root\" type=\"Node2D\" groups=[\"Enemies\", \"Pickups\"]]", text.Text);
    }

    [Fact]
    public void WritesInstancePlaceholders()
    {
        byte[] bundled = VDictionary(
            ("names", VStringArray("Root", "Node2D", "Child")),
            ("variants", VArray(VString("res://sub/thing.tscn"))),
            ("nodes", VIntArray(
                -1, -1, 1, 0, -1, 0, 0,
                0, 0, 0x7FFFFFFF, 2, (1 << 30) | 0, 0, 0)),
            ("conns", VIntArray()));

        ResourceTextResult text = ResourceTextWriter.Write(BinaryResource.Load(
            BuildResource("PackedScene", [], [new TestObject("PackedScene", "local://0", [("_bundled", bundled)])])));

        Assert.True(
            text.Text.Contains("[node name=\"Child\" parent=\".\" instance_placeholder=\"res://sub/thing.tscn\"]", StringComparison.Ordinal),
            text.Text);
    }

    [Fact]
    public async Task RelinkStageRewritesOnlyConvertedReferences()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-relink", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(
                Path.Combine(root, "scene.tscn"),
                "[gd_scene load_steps=2 format=2]\n\n" +
                "[ext_resource path=\"res://a/thing.material\" type=\"Material\" id=1]\n\n" +
                "[node name=\"X\" type=\"Node\"]\nmaterial = ExtResource( 1 )\n");
            File.WriteAllText(
                Path.Combine(root, "script.gd"),
                "const M = preload(\"res://a/thing.material\")\nconst T = preload(\"res://a/tex.png\")\n");
            File.WriteAllText(
                Path.Combine(root, "project.godot"),
                "[application]\n\nrun/main_scene=\"res://level.scn\"\n");
            File.WriteAllText(
                Path.Combine(root, "nested.tres"),
                "[gd_resource type=\"Resource\" format=2]\n\n[resource]\ninner = \"res://a/thing.material::3\"\n");

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };
            context.RemappedPaths["res://a/thing.material"] = "res://a/thing.tres";
            context.RemappedPaths["res://level.scn"] = "res://level.tscn";

            var result = new RecoveryStageResult { Stage = "references" };
            await new RelinkStage().RunAsync(context, result, CancellationToken.None);

            Assert.Contains("path=\"res://a/thing.tres\"", File.ReadAllText(Path.Combine(root, "scene.tscn")));
            string script = File.ReadAllText(Path.Combine(root, "script.gd"));
            Assert.Contains("preload(\"res://a/thing.tres\")", script);
            Assert.Contains("preload(\"res://a/tex.png\")", script);
            Assert.Contains(
                "run/main_scene=\"res://level.tscn\"", File.ReadAllText(Path.Combine(root, "project.godot")));
            Assert.Contains(
                "inner = \"res://a/thing.tres::3\"", File.ReadAllText(Path.Combine(root, "nested.tres")));
            Assert.Equal(4, result.Counters["files updated"]);
            Assert.Equal(4, result.Counters["references updated"]);

            // Running again changes nothing.
            var again = new RecoveryStageResult { Stage = "references" };
            await new RelinkStage().RunAsync(context, again, CancellationToken.None);
            Assert.Empty(again.Counters);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task StageConvertsAndRelinksScenesThatUseAMaterial()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-relink-e2e", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(
                Path.Combine(root, "thing.material"),
                BuildResource("", [], [new TestObject("ShaderMaterial", "res://thing.material", [])]));

            byte[] bundled = VDictionary(
                ("names", VStringArray("Root", "Node2D", "material_override")),
                ("variants", VArray(VObjectExternalIndex(0))),
                ("nodes", VIntArray(
                    -1, -1, 1, 0, -1, 1, 2, 0, 0)), // one property: material_override = variants[0]
                ("conns", VIntArray()));

            File.WriteAllBytes(
                Path.Combine(root, "level.scn"),
                BuildResource("PackedScene", [("Material", "res://thing.material")], [
                    new TestObject("PackedScene", "local://0", [("_bundled", bundled)]),
                ]));

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };

            var converted = new RecoveryStageResult { Stage = "resources" };
            await new ResourceStage().RunAsync(context, converted, CancellationToken.None);
            Assert.Equal(1, converted.Counters["scenes"]);
            Assert.Equal(2, context.RemappedPaths.Count);

            var relinked = new RecoveryStageResult { Stage = "references" };
            await new RelinkStage().RunAsync(context, relinked, CancellationToken.None);

            string scene = File.ReadAllText(Path.Combine(root, "level.tscn"));
            Assert.Contains("[ext_resource path=\"res://thing.tres\" type=\"Material\" id=1]", scene);
            Assert.Contains("material_override = ExtResource( 1 )", scene);
            Assert.Equal(1, relinked.Counters["files updated"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public void ImportMetadataMapsImportedFilesToTheirSource()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-import", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "art"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, "art", "hero.tga.import"),
                "[remap]\n\nimporter=\"texture\"\ntype=\"StreamTexture\"\n" +
                "path.astc=\"res://.import/hero.tga-abc.astc.stex\"\n" +
                "path.s3tc=\"res://.import/hero.tga-abc.s3tc.stex\"\n\n[deps]\n\nsource_file=\"res://art/hero.tga\"\n");

            Dictionary<string, string> map = ImportMetadata.ReadSourceMap(root);

            Assert.Equal(2, map.Count);
            Assert.Equal("res://art/hero.tga", map["res://.import/hero.tga-abc.s3tc.stex"]);
            Assert.Equal("res://art/hero.tga", map["res://.import/hero.tga-abc.astc.stex"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task TextureStageWritesThePngAtTheImportedSourcePath()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-texplace", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".import"));
        Directory.CreateDirectory(Path.Combine(root, "art"));
        try
        {
            const string hash = "0123456789abcdef0123456789abcdef";
            File.WriteAllBytes(
                Path.Combine(root, ".import", $"hero.tga-{hash}.s3tc.stex"),
                TextureTests.BuildStreamTexture(1, 1, Godot3ImageFormat.RGBA8, [255, 0, 0, 255]));
            File.WriteAllText(
                Path.Combine(root, "art", "hero.tga.import"),
                $"[remap]\n\nimporter=\"texture\"\npath.s3tc=\"res://.import/hero.tga-{hash}.s3tc.stex\"\n");

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };
            var result = new RecoveryStageResult { Stage = "textures" };

            await new TextureStage().RunAsync(context, result, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(root, "art", "hero.png")));
            Assert.Equal(
                "res://art/hero.png",
                context.RemappedPaths["res://art/hero.tga"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task RelinkArchivesRemapsThatWouldShadowTheTextFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-remap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "All.tres"), "[gd_resource type=\"Resource\" format=2]\n");
            File.WriteAllText(
                Path.Combine(root, "All.tres.remap"), "[remap]\n\npath=\"res://All.tres.converted.res\"\n");

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };
            context.RemappedPaths["res://All.tres.converted.res"] = "res://All.tres";

            var result = new RecoveryStageResult { Stage = "references" };
            await new RelinkStage().RunAsync(context, result, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(root, "All.tres.remap")));
            Assert.True(File.Exists(Path.Combine(root, ".autoconverted", "All.tres.remap")));
            Assert.Equal(1, result.Counters["remaps archived"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    [Fact]
    public async Task ResourceStageWritesImportedScenesAtTheSourcePath()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-scenepath", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".import"));
        Directory.CreateDirectory(Path.Combine(root, "models"));
        try
        {
            File.WriteAllBytes(Path.Combine(root, ".import", "hero.dae-abc.scn"), MinimalScene());
            File.WriteAllText(
                Path.Combine(root, "models", "hero.dae.import"),
                "[remap]\n\nimporter=\"scene\"\ntype=\"PackedScene\"\npath=\"res://.import/hero.dae-abc.scn\"\n");

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
                EngineMajor = 3,
            };
            var result = new RecoveryStageResult { Stage = "resources" };

            await new ResourceStage().RunAsync(context, result, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(root, "models", "hero.tscn")));
            Assert.Equal("res://models/hero.tscn", context.RemappedPaths["res://models/hero.dae"]);
            Assert.Equal("res://models/hero.tscn", context.RemappedPaths["res://.import/hero.dae-abc.scn"]);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }

    private static byte[] MinimalScene()
    {
        byte[] bundled = VDictionary(
            ("names", VStringArray("Root", "Node2D", "Child")),
            ("variants", VArray()),
            ("node_count", VInt(2)),
            ("nodes", VIntArray(
                -1, -1, 1, 0, -1, 0, 0, // root: parent, owner, type, name, instance, props, groups
                0, 0, 1, 2, -1, 0, 0)),  // child of node 0
            ("conn_count", VInt(0)),
            ("conns", VIntArray()));

        return BuildResource(
            headerType: "PackedScene",
            externalResources: [],
            objects: [new TestObject("PackedScene", "local://0", [("_bundled", bundled)])]);
    }

    [Theory]
    [InlineData("res://a/b.material", false, "b.tres")]
    [InlineData("res://a/b.scn", true, "b.tscn")]
    [InlineData("res://a/b.tres.converted.res", false, "b.tres")]
    [InlineData("res://a/b.tscn.converted.res", true, "b.tscn")]
    [InlineData("res://.import/b.dae-0123456789abcdef0123456789abcdef.scn", true, "b.tscn")]
    [InlineData("res://.import/b.obj-0123456789abcdef0123456789abcdef.mesh", false, "b.tres")]
    public void MapsSourceNamesToTextTargets(string source, bool isScene, string expected)
    {
        string target = ResourceStage.TargetPath(source.Replace('/', Path.DirectorySeparatorChar), isScene);
        Assert.Equal(expected, Path.GetFileName(target));
    }
}