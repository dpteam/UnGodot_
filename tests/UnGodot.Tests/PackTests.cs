using UnGodot.Core.Extract;
using UnGodot.Core.IO;
using UnGodot.Core.Pck;
using Xunit;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Tests;

public class PackTests
{
    private static readonly TestPacks.FileSpec[] Files =
    [
        new("res://icon.svg", "<svg/>"u8.ToArray()),
        new("res://levels/one.tscn", "[gd_scene format=3]\n"u8.ToArray()),
        new("res://empty.txt", []),
    ];

    [Fact]
    public void LocatesAndParsesAStandalonePack()
    {
        byte[] pack = TestPacks.BuildV4(Files);
        using var source = new MemorySource(pack);

        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out string? error), error);
        Assert.Equal(0, header!.Start);
        Assert.Equal(4u, header.FormatVersion);
        Assert.False(header.DirEncrypted);

        PckFile opened = PckFile.Open(source, header);
        Assert.Equal(3, opened.Entries.Count);
        Assert.Equal("res://icon.svg", opened.Entries[0].Path);
        Assert.Equal(Files[0].Content, source.ReadAt(opened.DataOffset(opened.Entries[0]), Files[0].Content.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void ParsesEveryFormatGeneration(int format)
    {
        byte[] pack = format switch
        {
            0 => TestPacks.BuildV0(Files),
            1 => TestPacks.BuildV1(Files),
            2 => TestPacks.BuildV2(Files),
            3 => TestPacks.BuildV4(Files, formatVersion: 3, engineMinor: 2),
            _ => TestPacks.BuildV4(Files, formatVersion: 4, engineMinor: 4),
        };

        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out string? error), error);
        Assert.Equal((uint)format, header!.FormatVersion);

        PckFile opened = PckFile.Open(source, header);
        Assert.Equal(3, opened.Entries.Count);

        var (content, _) = Extractor.Decode(source, opened, opened.Entries[1], null);
        Assert.Equal(Files[1].Content, content);
    }

    [Fact]
    public void ParsesAnEncryptedPackAndRejectsWrongKeys()
    {
        byte[] key = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var files = new[]
        {
            new TestPacks.FileSpec("res://main.gd", "extends Node\n"u8.ToArray(), PckFlags.FileEncrypted),
            new TestPacks.FileSpec("res://plain.txt", "hello"u8.ToArray()),
        };

        byte[] pack = TestPacks.BuildV4(files, key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        Assert.True(header!.DirEncrypted);

        Assert.Throws<PckException>(() => PckFile.Open(source, header));
        Assert.Throws<PckException>(() => PckFile.Open(source, header, new byte[32]));

        PckFile opened = PckFile.Open(source, header, key);
        Assert.Equal(2, opened.Entries.Count);
        Assert.True(opened.Entries[0].Encrypted);

        var (content, decrypted) = Extractor.Decode(source, opened, opened.Entries[0], key);
        Assert.True(decrypted);
        Assert.Equal(files[0].Content, content);
    }

    [Fact]
    public void FindsAPackAppendedToAnExecutable()
    {
        byte[] pack = TestPacks.BuildV4(Files);
        byte[] blob = TestPacks.AppendToExecutable(pack);
        using var source = new MemorySource(blob);

        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out string? error), error);
        Assert.Equal(2048, header!.Start);
        Assert.Equal(3, PckFile.Open(source, header).Entries.Count);
    }

    [Fact]
    public void FindsAPackWithARenamedMagic()
    {
        byte[] pack = TestPacks.BuildV4(Files);
        "XXXX"u8.ToArray().CopyTo(pack, 0);

        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out string? error), error);
        Assert.Equal(0, header!.Start);
        Assert.Single(PckFile.Open(source, header).Entries, e => e.Path == "res://icon.svg");
    }

    [Fact]
    public void FindsASelfContainedPackWithARenamedMagic()
    {
        byte[] pack = TestPacks.BuildV4(Files);
        byte[] blob = TestPacks.AppendToExecutable(pack, "ZZZZ"u8.ToArray());
        using var source = new MemorySource(blob);

        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out string? error), error);
        Assert.Equal(3, PckFile.Open(source, header!).Entries.Count);
    }

    [Fact]
    public void RejectsFilesThatAreNotPacks()
    {
        using var source = new MemorySource(new byte[4096]);
        Assert.False(PckLocator.TryLocate(source, out _, out string? error));
        Assert.NotNull(error);
    }

    [Fact]
    public void RejectsSparseBundlesWithAClearMessage()
    {
        byte[] pack = TestPacks.BuildV4(Files);
        // Flip the sparse bundle flag in the header.
        BitConverter.GetBytes(PckFlags.PackSparseBundle).CopyTo(pack, 20);

        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        PckException error = Assert.Throws<PckException>(() => PckFile.Open(source, header!));
        Assert.Contains("sparse bundle", error.Message);
    }

    [Fact]
    public void ExtractsAnEncryptedPackToDisk()
    {
        byte[] key = Enumerable.Repeat((byte)0x77, 32).ToArray();
        var files = new[]
        {
            new TestPacks.FileSpec("res://main.gd", "extends Node\n"u8.ToArray(), PckFlags.FileEncrypted),
            new TestPacks.FileSpec("res://art/icon.png", [0x89, 0x50, 0x4E, 0x47], PckFlags.FileEncrypted),
        };

        byte[] pack = TestPacks.BuildV4(files, key);
        using var source = new MemorySource(pack);
        Assert.True(PckLocator.TryLocate(source, out PckHeader? header, out _));
        PckFile opened = PckFile.Open(source, header!, key);

        string output = Path.Combine(Path.GetTempPath(), "ungodot-tests", Guid.NewGuid().ToString("N"));
        try
        {
            ExtractStats stats = Extractor.Extract(source, opened, output, key, new ExtractOptions());
            Assert.Equal(2, stats.Written);
            Assert.Equal(2, stats.Decrypted);
            Assert.Equal(0, stats.Failed);
            Assert.Empty(stats.ChecksumErrors);
            Assert.Equal("extends Node\n", File.ReadAllText(Path.Combine(output, "main.gd")));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, File.ReadAllBytes(Path.Combine(output, "art", "icon.png")));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    [Fact]
    public void SanitisesEntryPaths()
    {
        Assert.Equal("icon.svg", SafePath.Relative("res://icon.svg"));
        Assert.Equal(Path.Combine("a", "b", "c.png"), SafePath.Relative("res://a/b/c.png"));
        Assert.Null(SafePath.Relative("res://../../etc/passwd"));
        Assert.Null(SafePath.Relative("res://"));
        Assert.Equal(Path.Combine("@@user@@", "save.dat"), SafePath.Relative("user://save.dat"));
    }

    [Fact]
    public void GlobMatchingWorks()
    {
        Assert.True(Extractor.GlobMatch("levels/one.tscn", "*.tscn"));
        Assert.True(Extractor.GlobMatch("levels/one.tscn", "levels/*"));
        Assert.False(Extractor.GlobMatch("levels/one.tscn", "*.gd"));
        Assert.True(Extractor.GlobMatch("a/b/c.gd", "a/*/c.gd"));
    }
}