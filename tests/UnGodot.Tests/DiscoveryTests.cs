using UnGodot.Core.Discovery;
using UnGodot.Core.Extract;
using UnGodot.Core.Workspace;
using Xunit;

namespace UnGodot.Tests;

public class DiscoveryTests : IDisposable
{
    private readonly string _root;

    public DiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ungodot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    private string Write(string relative, byte[] content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void FindsPacksInSiblingFoldersAndTheEmbeddedOne()
    {
        Write("content/acorn/sonic0.pck", TestPacks.BuildV1(
        [
            new TestPacks.FileSpec("res://textures/a.stex", "a"u8.ToArray()),
            new TestPacks.FileSpec("res://project.binary", "project"u8.ToArray()),
        ]));

        Write("content/acorn/sonic1.pck", TestPacks.BuildV1(
        [
            new TestPacks.FileSpec("res://textures/b.stex", "b"u8.ToArray()),
        ]));

        byte[] exe = TestPacks.AppendToExecutable(TestPacks.BuildV4(
            [new TestPacks.FileSpec("res://embedded.gd", "extends Node"u8.ToArray())]));
        string exePath = Write("exec/game.exe", exe);

        DiscoveryResult result = GameDiscovery.Discover(exePath);

        Assert.True(result.HasPacks);
        Assert.Contains(result.Packs, c => c.Path.EndsWith("sonic0.pck", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Packs, c => c.Path.EndsWith("sonic1.pck", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Packs, c => c.Path.EndsWith("game.exe", StringComparison.OrdinalIgnoreCase));

        PackCandidate? primary = result.Primary;
        Assert.NotNull(primary);
        Assert.True(primary!.HasProjectSettings);
        Assert.Equal("sonic0.pck", Path.GetFileName(primary.Path));
    }

    [Fact]
    public void DiscoversFromADirectory()
    {
        Write("content/acorn/sonic0.pck", TestPacks.BuildV1([new TestPacks.FileSpec("res://a.txt", "a"u8.ToArray())]));
        Write("content/acorn/sonic1.pck", TestPacks.BuildV1([new TestPacks.FileSpec("res://b.txt", "b"u8.ToArray())]));

        DiscoveryResult result = GameDiscovery.Discover(_root);
        Assert.True(result.InputIsDirectory);
        Assert.Equal(2, result.Packs.Count());
    }

    [Fact]
    public void FallsBackToTheParentFolderWhenThePathIsGone()
    {
        Write("content/game.pck", TestPacks.BuildV1([new TestPacks.FileSpec("res://a.txt", "a"u8.ToArray())]));
        DiscoveryResult result = GameDiscovery.Discover(Path.Combine(_root, "missing", "game.pck"));
        Assert.True(result.HasPacks);
        Assert.Contains(result.Notes, note => note.Contains("does not exist"));
    }

    [Fact]
    public void ReportsCriCpkAsNotAGodotPack()
    {
        Write("content/PCCriPak.cpk", "CPK "u8.ToArray().Concat(new byte[1024]).ToArray());
        DiscoveryResult result = GameDiscovery.Discover(_root);
        Assert.False(result.HasPacks);
        Assert.Contains(result.Notes, note => note.Contains("CRI CPK"));
    }

    [Fact]
    public void ExtractsAllPacksWithTheBasePackWinning()
    {
        Write("content/acorn/sonic1.pck", TestPacks.BuildV1(
        [
            new TestPacks.FileSpec("res://shared.txt", "dlc"u8.ToArray()),
            new TestPacks.FileSpec("res://dlc_only.txt", "dlc"u8.ToArray()),
        ]));

        Write("content/acorn/sonic0.pck", TestPacks.BuildV1(
        [
            new TestPacks.FileSpec("res://shared.txt", "base"u8.ToArray()),
            new TestPacks.FileSpec("res://project.binary", "project"u8.ToArray()),
        ]));

        DiscoveryResult discovery = GameDiscovery.Discover(_root);
        var workspace = new GameWorkspace(new WorkspaceOptions());
        string output = Path.Combine(_root, "out");

        GameWorkspace.MultiExtractResult result = workspace.ExtractAll(
            discovery, output, new ExtractOptions { WriteManifest = false });

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.PerPack.Count);
        Assert.Equal("base", File.ReadAllText(Path.Combine(output, "shared.txt")));
        Assert.True(File.Exists(Path.Combine(output, "dlc_only.txt")));
        Assert.Equal(1, result.OverwrittenEntries);
    }
}