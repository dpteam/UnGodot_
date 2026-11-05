using System.Buffers.Binary;
using System.Text;
using UnGodot.Dump;
using UnGodot.Textures;
using Xunit;

namespace UnGodot.Tests;

public class DumpTests
{
    // -------------------------------------------------------------------------------------
    // Builders (a v3 resource and a GDScript container are enough to prove the carving path)
    // -------------------------------------------------------------------------------------

    private static byte[] U32(uint value) => BitConverter.GetBytes(value);

    private static byte[] I32(int value) => BitConverter.GetBytes(value);

    private static byte[] U64(ulong value) => BitConverter.GetBytes(value);

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Text(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32((uint)(utf8.Length + 1)), .. utf8, 0];
    }

    private static byte[] Inline(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32(0x8000_0000u | (uint)(utf8.Length + 1)), .. utf8, 0];
    }

    /// <summary>A minimal uncompressed Godot 3 resource with one object and one property.</summary>
    private static byte[] BuildResource(string type, string path, string propertyName, int value)
    {
        var body = new List<byte>();
        body.AddRange(U32(0)); // big endian
        body.AddRange(U32(0)); // real64
        body.AddRange(U32(3)); // version major
        body.AddRange(U32(1)); // version minor
        body.AddRange(U32(3)); // format
        body.AddRange(Text(""));
        body.AddRange(U64(0));
        for (int i = 0; i < 14; i++)
        {
            body.AddRange(U32(0));
        }

        body.AddRange(U32(0)); // string table
        body.AddRange(U32(0)); // external resources
        body.AddRange(U32(1)); // internal resources
        body.AddRange(Text(path));
        int offsetSlot = body.Count;
        body.AddRange(U64(0));

        int objectOffset = body.Count;
        body.AddRange(Text(type));
        body.AddRange(U32(1));
        body.AddRange(Inline(propertyName));
        body.AddRange(U32(3)); // VARIANT_INT
        body.AddRange(I32(value));

        byte[] file = [.. "RSRC"u8, .. body];
        BinaryPrimitives.WriteUInt64LittleEndian(file.AsSpan(4 + offsetSlot), (ulong)(4 + objectOffset));
        return file;
    }

    private static byte[] BuildMemoryImage(params byte[][] artifacts)
    {
        var image = new List<byte>();
        var random = new Random(1234);
        byte[] filler = new byte[4096];
        foreach (byte[] artifact in artifacts)
        {
            random.NextBytes(filler);
            image.AddRange(filler);
            image.AddRange(artifact);
        }

        random.NextBytes(filler);
        image.AddRange(filler);
        return image.ToArray();
    }

    // -------------------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------------------

    [Fact]
    public void CarvesScriptsResourcesTexturesAndSettingsFromAMemoryImage()
    {
        byte[] image = BuildMemoryImage(
            TestBytecode.SimpleScript(),
            BuildResource("ShaderMaterial", "res://thing.material", "render_priority", 3),
            TextureTests.BuildStreamTexture(1, 1, Godot3ImageFormat.RGBA8, [255, 0, 0, 255]),
            ProjectConfigTests.BuildEcFg(("application/config/name", ProjectConfigTests.EncodeString("Dumped"))));

        string root = Path.Combine(Path.GetTempPath(), "ungodot-dump", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string imagePath = Path.Combine(root, "memory.bin");
            File.WriteAllBytes(imagePath, image);

            using var source = new FileMemorySource(imagePath);
            DumpReport report = MemoryDump.Run(source, Path.Combine(root, "out"));

            Assert.Equal(1, report.Artifacts.GetValueOrDefault("scripts"));
            Assert.Equal(1, report.Artifacts.GetValueOrDefault("resources"));
            Assert.Equal(1, report.Artifacts.GetValueOrDefault("textures"));
            Assert.Equal(1, report.Artifacts.GetValueOrDefault("project settings"));

            Assert.Single(Directory.GetFiles(Path.Combine(root, "out", "scripts"), "*.gd"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "out", "resources"), "*.tres"));
            Assert.Single(Directory.GetFiles(Path.Combine(root, "out", "textures"), "*.png"));
            Assert.Contains(
                "config/name=\"Dumped\"",
                File.ReadAllText(Path.Combine(root, "out", "project", "project.godot")));
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
    public void IgnoresMagicNumbersThatAreNotRealArtifacts()
    {
        var image = new List<byte>();
        var random = new Random(7);
        byte[] noise = new byte[64 * 1024];
        random.NextBytes(noise);
        image.AddRange(noise);
        // A magic number followed by rubbish: every parser must reject it.
        image.AddRange("GDSC"u8);
        image.AddRange(noise.AsSpan(0, 4096).ToArray());
        image.AddRange("RSRC"u8);
        image.AddRange(noise.AsSpan(4096, 4096).ToArray());
        image.AddRange("ECFG"u8);
        image.AddRange(noise.AsSpan(8192, 4096).ToArray());

        string root = Path.Combine(Path.GetTempPath(), "ungodot-dump-noise", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string imagePath = Path.Combine(root, "memory.bin");
            File.WriteAllBytes(imagePath, image.ToArray());

            using var source = new FileMemorySource(imagePath);
            DumpReport report = MemoryDump.Run(source, Path.Combine(root, "out"));

            Assert.Equal(0, report.Artifacts.Values.Sum());
            Assert.Equal(3, report.Hits.Values.Sum());
            Assert.Equal(3, report.Rejected);
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
    public void DeduplicatesRepeatedArtifacts()
    {
        byte[] script = TestBytecode.SimpleScript();
        byte[] image = BuildMemoryImage(script, script, script);

        string root = Path.Combine(Path.GetTempPath(), "ungodot-dump-dedupe", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string imagePath = Path.Combine(root, "memory.bin");
            File.WriteAllBytes(imagePath, image);

            using var source = new FileMemorySource(imagePath);
            DumpReport report = MemoryDump.Run(source, Path.Combine(root, "out"));

            Assert.Equal(3, report.Hits.GetValueOrDefault("GDSC"));
            Assert.Equal(1, report.Artifacts.GetValueOrDefault("scripts"));
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
}