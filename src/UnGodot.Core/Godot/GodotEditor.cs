using System.Diagnostics;
using System.Text.Json;
using UnGodot.Core.Logging;
using UnGodot.Core.Net;

namespace UnGodot.Core.Godot;

/// <summary>The editor a recovered project needs.</summary>
public sealed record EditorSpec(string Version, bool Mono)
{
    /// <summary><c>4.5.1-stable</c> (or the pre-release tag verbatim).</summary>
    public string Tag => Version.Contains('-') ? Version : Version + "-stable";

    public string Label => Mono ? $"Godot {Version} (.NET)" : $"Godot {Version}";

    public override string ToString() => Label;
}

/// <summary>
/// Works out which Godot editor a recovered project needs and installs it when it is missing.
/// A C# project needs the .NET build: the regular build cannot load <c>.cs</c> scripts at all.
/// </summary>
public static class GodotEditor
{
    /// <summary>Detects the editor version from the C# SDK pin, the GDRE log or project.godot.</summary>
    public static EditorSpec? Detect(string projectDirectory)
    {
        bool mono = IsMono(projectDirectory);

        if (CsprojSdkVersion(projectDirectory) is { } sdkVersion)
        {
            return new EditorSpec(sdkVersion, Mono: true);
        }

        string logPath = Path.Combine(projectDirectory, "gdre_export.log");
        if (File.Exists(logPath))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(logPath);
            }
            catch (IOException)
            {
                lines = [];
            }

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                const string marker = "Use Godot editor version ";
                if (trimmed.StartsWith(marker, StringComparison.Ordinal))
                {
                    string rest = trimmed[marker.Length..];
                    string version = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd('.', ',') ?? string.Empty;
                    if (version.Length > 0)
                    {
                        bool monoHere = mono || rest.Contains("Mono", StringComparison.OrdinalIgnoreCase) || rest.Contains(".NET", StringComparison.OrdinalIgnoreCase);
                        return new EditorSpec(version, monoHere);
                    }
                }
            }

            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                const string detected = "Detected Engine Version:";
                if (trimmed.StartsWith(detected, StringComparison.Ordinal))
                {
                    string version = trimmed[detected.Length..].Trim();
                    if (version.Length > 0)
                    {
                        return new EditorSpec(version, mono);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>True when the project has C# code (a .csproj at its root).</summary>
    public static bool IsMono(string projectDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(projectDirectory, "*.csproj", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? CsprojSdkVersion(string projectDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(projectDirectory, "*.csproj", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (string file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                continue;
            }

            const string marker = "Godot.NET.Sdk/";
            int index = text.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            int start = index + marker.Length;
            int end = start;
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] is '.' or '-'))
            {
                end++;
            }

            if (end > start)
            {
                return text[start..end];
            }
        }

        return null;
    }

    public static string InstallDirectory(EditorSpec spec)
    {
        string name = spec.Mono ? spec.Version + "-mono" : spec.Version;
        return Path.Combine(Http.DataDirectory, "godot", name);
    }

    private static (string Plain, string Mono) Platform()
    {
        if (OperatingSystem.IsWindows())
        {
            return ("win64.exe", "win64");
        }

        if (OperatingSystem.IsMacOS())
        {
            return ("macos.universal", "macos.universal");
        }

        return System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64
            ? ("linux.arm64", "linux_arm64")
            : ("linux.x86_64", "linux_x86_64");
    }

    public static string EditorUrl(EditorSpec spec)
    {
        (string plain, string mono) = Platform();
        if (spec.Version.StartsWith("3.", StringComparison.Ordinal) && OperatingSystem.IsLinux())
        {
            (plain, mono) = ("x11.64", "x11_64");
        }

        string tag = spec.Tag;
        string file = spec.Mono
            ? $"Godot_v{tag}_mono_{mono}.zip"
            : $"Godot_v{tag}_{plain}.zip";

        return $"https://github.com/godotengine/godot-builds/releases/download/{tag}/{file}";
    }

    public static string TemplatesUrl(EditorSpec spec)
    {
        string tag = spec.Tag;
        string file = spec.Mono
            ? $"Godot_v{tag}_mono_export_templates.tpz"
            : $"Godot_v{tag}_export_templates.tpz";
        return $"https://github.com/godotengine/godot-builds/releases/download/{tag}/{file}";
    }

    /// <summary>The editor executable inside an install folder, if it is there.</summary>
    public static string? FindEditor(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var stack = new Stack<string>();
        stack.Push(directory);
        while (stack.Count > 0)
        {
            string current = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                if (Directory.Exists(entry))
                {
                    if (name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                    {
                        string binary = Path.Combine(entry, "Contents", "MacOS", "Godot");
                        if (File.Exists(binary))
                        {
                            return binary;
                        }
                    }

                    if (!name.StartsWith("GodotSharp", StringComparison.OrdinalIgnoreCase) &&
                        !name.StartsWith("data_", StringComparison.OrdinalIgnoreCase))
                    {
                        stack.Push(entry);
                    }

                    continue;
                }

                bool isEditor = name.StartsWith("Godot_v", StringComparison.OrdinalIgnoreCase) &&
                                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                !name.EndsWith(".tpz", StringComparison.OrdinalIgnoreCase) &&
                                !name.Contains("console", StringComparison.OrdinalIgnoreCase);
                if (isEditor)
                {
                    return entry;
                }
            }
        }

        return null;
    }

    /// <summary>A Godot editor already installed on the system whose version matches exactly.</summary>
    public static string? FindSystem(EditorSpec spec)
    {
        string[] names = spec.Mono
            ? ["godot-mono", "godot4-mono", "godot", "godot4"]
            : ["godot", "godot4"];

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string name in names)
            {
                string binary = Path.Combine(directory, name);
                if (!File.Exists(binary))
                {
                    continue;
                }

                string reported;
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(binary, "--version")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });

                    if (process is null)
                    {
                        continue;
                    }

                    reported = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    continue;
                }

                if (VersionMatches(reported, spec))
                {
                    return binary;
                }
            }
        }

        return null;
    }

    internal static bool VersionMatches(string reported, EditorSpec spec)
    {
        string[] parts = reported.Split('.');
        var numbers = new List<string>();
        foreach (string part in parts)
        {
            if (part.Length > 0 && part.All(char.IsAsciiDigit))
            {
                numbers.Add(part);
            }
            else
            {
                break;
            }
        }

        string status = parts.Length > numbers.Count ? parts[numbers.Count] : string.Empty;
        bool mono = parts.Contains("mono");

        string[] wanted = spec.Version.Split('-', 2);
        string wantedNumber = wanted[0];
        string wantedStatus = wanted.Length > 1 ? wanted[1] : "stable";

        static string Normalize(string value) => value.TrimEnd('.', '0').TrimEnd('.');

        return Normalize(string.Join('.', numbers)) == Normalize(wantedNumber) &&
               status == wantedStatus &&
               mono == spec.Mono;
    }

    /// <summary>Installs the editor when needed and returns the executable.</summary>
    public static async Task<string> EnsureEditorAsync(EditorSpec spec, CancellationToken cancellationToken)
    {
        string directory = InstallDirectory(spec);
        string? existing = FindEditor(directory);
        if (existing is not null)
        {
            return existing;
        }

        string url = EditorUrl(spec);
        Log.Info($"downloading {spec.Label} from {url}");
        Directory.CreateDirectory(directory);
        string archive = Path.Combine(directory, "editor.zip");
        try
        {
            await Http.DownloadAsync(url, archive, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            throw new IOException($"cannot download {spec.Label} (does version {spec.Version} exist?): {ex.Message}", ex);
        }

        Http.ExtractZip(archive, directory);
        try
        {
            File.Delete(archive);
        }
        catch
        {
            // harmless
        }

        string binary = FindEditor(directory) ?? throw new IOException("the archive did not contain a Godot editor");

        // Self-contained mode: settings and caches live next to the editor instead of mixing
        // with a system Godot installation.
        string? parent = Path.GetDirectoryName(binary);
        if (parent is not null)
        {
            try
            {
                File.WriteAllBytes(Path.Combine(parent, "._sc_"), []);
            }
            catch (IOException)
            {
                // best effort
            }
        }

        return binary;
    }

    /// <summary>Installs the export templates where the editor gore installed will look for them.</summary>
    public static async Task<string> EnsureTemplatesAsync(EditorSpec spec, string editor, CancellationToken cancellationToken)
    {
        (string number, string status) = spec.Tag.Contains('-')
            ? (spec.Tag[..spec.Tag.IndexOf('-')], spec.Tag[(spec.Tag.IndexOf('-') + 1)..])
            : (spec.Version, "stable");

        string folder = spec.Mono ? $"{number}.{status}.mono" : $"{number}.{status}";
        string baseDirectory = Path.Combine(Path.GetDirectoryName(editor) ?? ".", "editor_data", "export_templates");
        string destination = Path.Combine(baseDirectory, folder);
        if (File.Exists(Path.Combine(destination, "version.txt")))
        {
            return destination;
        }

        string url = TemplatesUrl(spec);
        Log.Info($"downloading the export templates from {url}");
        Directory.CreateDirectory(baseDirectory);
        string archive = Path.Combine(baseDirectory, folder + ".tpz");
        await Http.DownloadAsync(url, archive, cancellationToken).ConfigureAwait(false);

        string temporary = Path.Combine(baseDirectory, folder + ".tmp");
        if (Directory.Exists(temporary))
        {
            Directory.Delete(temporary, recursive: true);
        }

        Http.ExtractZip(archive, temporary);
        string inner = Path.Combine(temporary, "templates");
        if (Directory.Exists(destination))
        {
            Directory.Delete(destination, recursive: true);
        }

        Directory.Move(Directory.Exists(inner) ? inner : temporary, destination);
        try
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }

            File.Delete(archive);
        }
        catch
        {
            // harmless
        }

        return destination;
    }

    /// <summary>Convenience: run the editor on a project.</summary>
    public static Process StartEditor(string editor, string projectDirectory)
    {
        var startInfo = new ProcessStartInfo(editor)
        {
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        startInfo.ArgumentList.Add("--editor");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(Path.GetFullPath(projectDirectory));
        return Process.Start(startInfo) ?? throw new IOException($"cannot start {editor}");
    }

    /// <summary>Reads the version from a json document (kept for tests/tools).</summary>
    internal static string? VersionFromJson(JsonElement root) =>
        root.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() : null;
}