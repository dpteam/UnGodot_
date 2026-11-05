using System.IO.Compression;
using UnGodot.Core.Discovery;
using UnGodot.Core.Extract;
using UnGodot.Core.Gdre;
using UnGodot.Core.Godot;
using UnGodot.Core.Plugins;
using UnGodot.Core.Workspace;
using Xunit;

namespace UnGodot.Tests;

public class EditorAndPluginTests : IDisposable
{
    private readonly string _root;

    public EditorAndPluginTests()
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

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void ReadsTheEditorVersionFromTheCsproj()
    {
        Write("game.csproj", """<Project Sdk="Godot.NET.Sdk/4.5.1"></Project>""");
        EditorSpec? spec = GodotEditor.Detect(_root);
        Assert.NotNull(spec);
        Assert.Equal("4.5.1", spec!.Version);
        Assert.True(spec.Mono);
        Assert.Contains("/4.5.1-stable/Godot_v4.5.1-stable_mono_", GodotEditor.EditorUrl(spec));
    }

    [Fact]
    public void ReadsTheEditorVersionFromTheGdreLog()
    {
        Write("gdre_export.log", "Detected Engine Version: 4.3.0\nUse Godot editor version 4.3 to edit the project.\n");
        EditorSpec? spec = GodotEditor.Detect(_root);
        Assert.NotNull(spec);
        Assert.Equal("4.3", spec!.Version);
        Assert.False(spec.Mono);
        Assert.Equal("4.3-stable", spec.Tag);
    }

    [Theory]
    [InlineData("4.7.2.stable.mono.arch_linux.ed1daf0bf", "4.7.2", true, true)]
    [InlineData("4.7.2.stable.arch_linux.ed1daf0bf", "4.7.2", true, false)]
    [InlineData("4.5.1.stable.mono.official.f62fdbde1", "4.7.2", true, false)]
    [InlineData("4.5.stable.official.876b29033", "4.5", false, true)]
    public void MatchesSystemEditorVersions(string reported, string version, bool mono, bool expected)
    {
        Assert.Equal(expected, GodotEditor.VersionMatches(reported, new EditorSpec(version, mono)));
    }

    [Fact]
    public void ParsesLibrariesAndDependenciesOfAGdextension()
    {
        string addon = Path.Combine(_root, "addons", "demo");
        Directory.CreateDirectory(addon);
        string file = Path.Combine(addon, "demo.gdextension");
        File.WriteAllText(file, """
            [configuration]
            entry_symbol = "demo_init"

            [libraries]
            windows.release.x86_64 = "res://addons/demo/win/demo.dll"
            windows.debug.x86_64 = "res://addons/demo/win/demo.debug.dll"
            linux.editor.x86_64 = "res://addons/demo/linux/demo.editor.so"
            linux.release.arm64 = "linux/demo.arm64.so"
            macos.release = "mac/demo.dylib"

            [dependencies]
            windows.x86_64 = { "res://addons/demo/win/helper.exe": "" }
            """);

        Gdextension extension = PluginRestorer.ParseExtension(_root, file);
        Assert.Equal("demo", extension.Name);
        Assert.Equal("demo_init", extension.EntrySymbol);

        List<string> needed = extension.Needed.Select(path => Path.GetRelativePath(_root, path).Replace('\\', '/')).ToList();
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("addons/demo/win/demo.dll", needed);
            Assert.Contains("addons/demo/win/demo.debug.dll", needed);
            Assert.Contains("addons/demo/win/helper.exe", needed);
            Assert.DoesNotContain("addons/demo/linux/demo.editor.so", needed);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains("addons/demo/linux/demo.editor.so", needed);
            Assert.DoesNotContain("addons/demo/win/demo.dll", needed);
        }
    }

    [Fact]
    public void FindsSentryStyleVersions()
    {
        Assert.Equal("2.1.0+f8cb981", PluginRestorer.FindVersionWithHash("\0junk 1.2\0sentry 2.1.0+f8cb981\0more"u8));
        Assert.Null(PluginRestorer.FindVersionWithHash("0.15.3\0"u8));
    }

    [Fact]
    public void StripsAnsiSequences()
    {
        Assert.Equal("9.0.121", GdreOutputRenderer.StripAnsi("\x1b[?1h9.0.121"));
        Assert.Equal("red", GdreOutputRenderer.StripAnsi("\x1b[31mred\x1b[0m"));
    }

    [Fact]
    public void OpensAPackStoredInsideAZip()
    {
        byte[] pack = TestPacks.BuildV1([new TestPacks.FileSpec("res://zipped.txt", "inside a zip"u8.ToArray())]);
        string archive = Path.Combine(_root, "game.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = zip.CreateEntry("data/game.pck");
            using Stream stream = entry.Open();
            stream.Write(pack);
        }

        DiscoveryResult discovery = GameDiscovery.Discover(archive);
        PackCandidate candidate = Assert.Single(discovery.Candidates, c => c.IsArchivedPack);

        var workspace = new GameWorkspace(new WorkspaceOptions());
        using OpenedPack opened = workspace.Open(candidate);
        Assert.NotNull(opened.Pack);
        Assert.Single(opened.Pack!.Entries);

        string output = Path.Combine(_root, "out");
        ExtractStats stats = Extractor.Extract(opened.Source, opened.Pack, output, null, new ExtractOptions { WriteManifest = false });
        Assert.Equal(1, stats.Written);
        Assert.Equal("inside a zip", File.ReadAllText(Path.Combine(output, "zipped.txt")));
    }
}
