using System.Security.Cryptography;
using System.Text;
using UnGodot.Core.Logging;
using UnGodot.GDScript;
using UnGodot.Recovery.Project;
using UnGodot.Resources;
using UnGodot.Textures;

namespace UnGodot.Dump;

public sealed class DumpReport
{
    public long BytesScanned { get; set; }
    public TimeSpan Elapsed { get; set; }
    public Dictionary<string, int> Hits { get; } = new(StringComparer.Ordinal);
    public int Rejected { get; set; }
    public Dictionary<string, int> Artifacts { get; } = new(StringComparer.Ordinal);
    public List<string> Notes { get; } = [];

    public void CountHit(string signature) => Hits[signature] = Hits.GetValueOrDefault(signature) + 1;

    public void CountArtifact(string kind) => Artifacts[kind] = Artifacts.GetValueOrDefault(kind) + 1;
}

/// <summary>
/// Turns raw memory hits into real files. Every candidate is validated by the *same* parsers
/// the extraction pipeline uses, so a magic-number coincidence in a heap block is dropped and
/// a genuinely decrypted resource, script or texture is written out.
/// </summary>
public sealed class ArtifactCarver
{
    private readonly string _root;
    private readonly Dictionary<string, int> _windows;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public ArtifactCarver(string outputDirectory, int engineMajor = 3)
    {
        _root = outputDirectory;
        EngineMajor = engineMajor;
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "resources"));
        Directory.CreateDirectory(Path.Combine(_root, "textures"));
        Directory.CreateDirectory(Path.Combine(_root, "project"));

        _windows = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["GDSC"] = 8 * 1024 * 1024,
            ["RSRC"] = 16 * 1024 * 1024,
            ["RSCC"] = 16 * 1024 * 1024,
            ["GDST"] = 16 * 1024 * 1024,
            ["ECFG"] = 4 * 1024 * 1024,
        };
    }

    public int EngineMajor { get; }

    public DumpReport Report { get; } = new();

    public void Handle(string signature, IMemorySource source, long offset)
    {
        Report.CountHit(signature);
        if (!_windows.TryGetValue(signature, out int windowSize))
        {
            return;
        }

        byte[] window = ReadWindow(source, offset, windowSize);
        if (window.Length < 8)
        {
            return;
        }

        try
        {
            switch (signature)
            {
                case "GDSC":
                    CarveScript(window, offset);
                    break;
                case "RSRC":
                case "RSCC":
                    CarveResource(window, offset);
                    break;
                case "GDST":
                    CarveTexture(window, offset);
                    break;
                case "ECFG":
                    CarveProjectSettings(window, offset);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report.Rejected++;
            if (Report.Notes.Count < 20)
            {
                Report.Notes.Add($"{signature} at 0x{offset:x}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static byte[] ReadWindow(IMemorySource source, long offset, int windowSize)
    {
        var window = new byte[windowSize];
        int read = source.Read(offset, window);
        return read == windowSize ? window : window.AsSpan(0, Math.Max(0, read)).ToArray();
    }

    private void CarveScript(byte[] window, long offset)
    {
        RevisionDetection detection = RevisionDetector.Detect(window);
        if (detection.Revision is null)
        {
            throw new DumpException(detection.Error ?? "no matching GDScript revision");
        }

        DecompileResult result = GdScriptDecompiler.Decompile(window, detection.Revision, new DecompileOptions());
        string hash = Hash(Encoding.UTF8.GetBytes(result.Source));
        if (!_seen.Add("script:" + hash))
        {
            return;
        }

        string name = $"{detection.Revision.EngineVersion}-{hash[..12]}.gd";
        File.WriteAllText(Path.Combine(_root, "scripts", name), result.Source);
        Report.CountArtifact("scripts");
        Log.Debug($"script at 0x{offset:x} -> {name} ({detection.Revision.EngineVersion})");
    }

    private void CarveResource(byte[] window, long offset)
    {
        BinaryResource resource = BinaryResource.Load(window);
        ResourceTextResult text = ResourceTextWriter.Write(resource);
        string hash = Hash(Encoding.UTF8.GetBytes(text.Text));
        if (!_seen.Add("resource:" + hash))
        {
            return;
        }

        string type = resource.Main?.Type ?? resource.Type;
        if (string.IsNullOrEmpty(type))
        {
            type = "Resource";
        }

        string name = $"{Sanitize(type)}-{hash[..12]}" + (text.IsScene ? ".tscn" : ".tres");
        File.WriteAllText(Path.Combine(_root, "resources", name), text.Text);
        Report.CountArtifact(text.IsScene ? "scenes" : "resources");
        Log.Debug($"resource at 0x{offset:x} -> {name}");
    }

    private void CarveTexture(byte[] window, long offset)
    {
        if (!StreamTexture.LooksLikeStreamTexture(window))
        {
            return;
        }

        byte[] png = StreamTexture.ToPng(window);
        string hash = Hash(png);
        if (!_seen.Add("texture:" + hash))
        {
            return;
        }

        string name = $"{hash[..12]}.png";
        File.WriteAllBytes(Path.Combine(_root, "textures", name), png);
        Report.CountArtifact("textures");
        Log.Debug($"texture at 0x{offset:x} -> {name}");
    }

    private void CarveProjectSettings(byte[] window, long offset)
    {
        // Godot 3 and 4 use the same ECFG layout; only the variant encoding differs.
        ProjectConfig config;
        try
        {
            config = ProjectConfig.ParseEcFg(window, EngineMajor);
        }
        catch (ProjectConfigException) when (EngineMajor == 3)
        {
            config = ProjectConfig.ParseEcFg(window, 4);
        }

        if (config.Settings.Count == 0)
        {
            return;
        }

        string text = config.ToProjectGodotText();
        string hash = Hash(Encoding.UTF8.GetBytes(text));
        if (!_seen.Add("project:" + hash))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_root, "project", "project.godot"), text);
        Report.CountArtifact("project settings");
        Log.Debug($"project settings at 0x{offset:x} ({config.Settings.Count} entries)");
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return builder.Length == 0 ? "Resource" : builder.ToString();
    }
}