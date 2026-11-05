using System.IO.Compression;
using System.Text;
using System.Text.Json;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Net;

namespace UnGodot.Core.Plugins;

/// <summary>One <c>.gdextension</c> file of a recovered project.</summary>
public sealed class Gdextension
{
    public required string FilePath { get; init; }
    public required string Name { get; init; }
    public string EntrySymbol { get; init; } = string.Empty;

    /// <summary>Files the editor on this machine will look for (absolute paths).</summary>
    public List<string> Needed { get; init; } = [];

    public List<string> Missing() => Needed.Where(path => !File.Exists(path)).ToList();
}

public sealed class PluginOutcome
{
    public required string Name { get; init; }
    public int Restored { get; init; }
    public List<string> StillMissing { get; init; } = [];
    public string? Source { get; init; }
}

/// <summary>
/// Restores the native libraries of GDExtension plugins in a recovered project.
///
/// A game ships only the libraries for the platform it was exported for, so a recovered
/// project usually cannot load its GDExtensions: <c>fmod</c>, <c>spine</c>, <c>sentry</c>…
/// This reads every <c>.gdextension</c>, works out which files the local editor needs and
/// fills the gaps from the game folder first, then from the upstream release.
/// </summary>
public static class PluginRestorer
{
    private enum KnownPlugin
    {
        None,
        Fmod,
        Spine,
        Sentry,
    }

    public static List<PluginOutcome> Restore(string project, string? gameDirectory, string? engineVersion, CancellationToken cancellationToken = default)
    {
        var outcomes = new List<PluginOutcome>();
        foreach (Gdextension extension in FindExtensions(project))
        {
            List<string> missing = extension.Missing();
            if (missing.Count == 0)
            {
                continue;
            }

            int restored = 0;
            string? source = null;

            if (gameDirectory is not null)
            {
                int copied = CopyFromFolder(extension, gameDirectory);
                if (copied > 0)
                {
                    restored += copied;
                    source = $"game folder {gameDirectory}";
                }
            }

            if (extension.Missing().Count > 0)
            {
                try
                {
                    (int copied, string from)? upstream = FetchUpstreamAsync(extension, project, gameDirectory, engineVersion, cancellationToken)
                        .GetAwaiter().GetResult();
                    if (upstream is not null)
                    {
                        restored += upstream.Value.copied;
                        source = upstream.Value.from;
                    }
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
                {
                    Log.Warn($"{extension.Name}: {ex.Message}");
                }
            }

            outcomes.Add(new PluginOutcome
            {
                Name = extension.Name,
                Restored = restored,
                StillMissing = extension.Missing(),
                Source = source,
            });
        }

        return outcomes;
    }

    /// <summary>Every <c>.gdextension</c> under <c>addons/</c> (or anywhere in the project).</summary>
    public static List<Gdextension> FindExtensions(string project)
    {
        var files = new List<string>();
        Collect(project, 0, files);
        files.Sort(StringComparer.OrdinalIgnoreCase);

        var extensions = new List<Gdextension>();
        foreach (string file in files)
        {
            try
            {
                extensions.Add(ParseExtension(project, file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"cannot parse '{file}': {ex.Message}");
            }
        }

        return extensions;
    }

    private static void Collect(string directory, int depth, List<string> output)
    {
        if (depth > 6)
        {
            return;
        }

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in entries)
        {
            string name = Path.GetFileName(entry);
            if (Directory.Exists(entry))
            {
                if (name.StartsWith('.') || name is "bin" or "obj")
                {
                    continue;
                }

                Collect(entry, depth + 1, output);
            }
            else if (name.EndsWith(".gdextension", StringComparison.OrdinalIgnoreCase))
            {
                output.Add(entry);
            }
        }
    }

    private static (string Os, string Arch) HostTags()
    {
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86_32",
            System.Runtime.InteropServices.Architecture.Arm => "arm32",
            _ => "x86_64",
        };

        return (os, arch);
    }

    private static readonly string[] Architectures = ["x86_64", "x86_32", "arm64", "arm32", "rv64", "wasm32", "universal"];

    /// <summary>True when a <c>.gdextension</c> key (<c>linux.editor.x86_64</c>) applies to this host.</summary>
    internal static bool KeyMatchesHost(string key)
    {
        (string os, string arch) = HostTags();
        string[] tags = key.Split('.').Select(tag => tag.Trim()).ToArray();
        if (tags.Length == 0 || tags[0] != os)
        {
            return false;
        }

        string? keyArch = tags.FirstOrDefault(tag => Architectures.Contains(tag));
        return keyArch is null || keyArch == arch || keyArch == "universal";
    }

    /// <summary>Parses the parts of a <c>.gdextension</c> file the restorer needs.</summary>
    public static Gdextension ParseExtension(string project, string file)
    {
        string text = File.ReadAllText(file);
        string baseDirectory = Path.GetDirectoryName(file) ?? project;
        string name = PluginName(project, file);

        string section = string.Empty;
        string entrySymbol = string.Empty;
        var needed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        bool? inBlock = null;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            if (inBlock is { } applies)
            {
                if (applies && FirstQuoted(line) is { Length: > 0 } path)
                {
                    needed.Add(Resolve(project, baseDirectory, path));
                }

                if (line.Contains('}'))
                {
                    inBlock = null;
                }

                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals < 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();

            switch (section)
            {
                case "configuration" when key == "entry_symbol":
                    entrySymbol = value.Trim('"');
                    break;

                case "libraries" when KeyMatchesHost(key):
                    if (FirstQuoted(value) is { Length: > 0 } library)
                    {
                        needed.Add(Resolve(project, baseDirectory, library));
                    }

                    break;

                case "dependencies":
                {
                    bool appliesHere = KeyMatchesHost(key);
                    if (value.StartsWith('{'))
                    {
                        if (appliesHere)
                        {
                            foreach (string path in AllQuotedKeys(value))
                            {
                                needed.Add(Resolve(project, baseDirectory, path));
                            }
                        }

                        if (!value.Contains('}'))
                        {
                            inBlock = appliesHere;
                        }
                    }

                    break;
                }
            }
        }

        return new Gdextension
        {
            FilePath = file,
            Name = name,
            EntrySymbol = entrySymbol,
            Needed = needed.ToList(),
        };
    }

    private static string PluginName(string project, string file)
    {
        string relative = Path.GetRelativePath(project, file);
        string[] components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (components.Length > 1 && components[0] == "addons")
        {
            return components[1];
        }

        return Path.GetFileNameWithoutExtension(file);
    }

    private static string? FirstQuoted(string value)
    {
        int start = value.IndexOf('"');
        if (start < 0)
        {
            return null;
        }

        int end = value.IndexOf('"', start + 1);
        return end < 0 ? null : value[(start + 1)..end];
    }

    private static IEnumerable<string> AllQuotedKeys(string value)
    {
        string trimmed = value.Trim('{', '}');
        foreach (string part in trimmed.Split(','))
        {
            // The key is the first quoted string of the pair; the colon after it may sit
            // inside a res:// path, so it cannot be used as the split point.
            if (FirstQuoted(part) is { Length: > 0 } key)
            {
                yield return key;
            }
        }
    }

    private static string Resolve(string project, string baseDirectory, string path)
    {
        if (path.StartsWith("res://", StringComparison.Ordinal))
        {
            return Path.Combine(project, path[6..]);
        }

        return Path.Combine(baseDirectory, path);
    }

    // -------------------------------------------------------------------------------------
    // Sources
    // -------------------------------------------------------------------------------------

    private static int CopyFromFolder(Gdextension extension, string directory)
    {
        var found = new List<string>();
        ListFiles(directory, 0, 3, found);

        int copied = 0;
        foreach (string target in extension.Missing())
        {
            string fileName = Path.GetFileName(target);
            string? source = found.FirstOrDefault(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));
            if (source is not null && Place(source, target))
            {
                copied++;
            }
        }

        return copied;
    }

    private static void ListFiles(string directory, int depth, int maxDepth, List<string> output)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string entry in entries)
        {
            if (Directory.Exists(entry))
            {
                if (depth < maxDepth)
                {
                    ListFiles(entry, depth + 1, maxDepth, output);
                }

                continue;
            }

            output.Add(entry);
        }
    }

    private static bool Place(string source, string target)
    {
        try
        {
            string? parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.Copy(source, target, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug($"cannot copy '{source}' to '{target}': {ex.Message}");
            return false;
        }
    }

    private static KnownPlugin Identify(Gdextension extension) => extension.EntrySymbol switch
    {
        "fmod_library_init" => KnownPlugin.Fmod,
        "spine_godot_library_init" => KnownPlugin.Spine,
        "sentry_gdextension_init" => KnownPlugin.Sentry,
        _ => KnownPlugin.None,
    };

    private static async Task<(int Copied, string From)?> FetchUpstreamAsync(
        Gdextension extension,
        string project,
        string? gameDirectory,
        string? engineVersion,
        CancellationToken cancellationToken)
    {
        KnownPlugin kind = Identify(extension);
        if (kind == KnownPlugin.None)
        {
            return null;
        }

        (string url, string cacheName) = kind switch
        {
            KnownPlugin.Fmod => await FmodReleaseAsync(engineVersion, cancellationToken).ConfigureAwait(false),
            KnownPlugin.Sentry => await SentryReleaseAsync(gameDirectory, cancellationToken).ConfigureAwait(false),
            KnownPlugin.Spine => await SpineReleaseAsync(project, engineVersion, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(),
        };

        Log.Info($"{extension.Name}: downloading {url}");
        string archive = await Http.FetchCachedAsync(url, cacheName, cancellationToken).ConfigureAwait(false);
        int copied = CopyFromZip(extension, archive);
        return (copied, url);
    }

    private static string MajorMinor(string version)
    {
        string[] parts = version.Split('.');
        return string.Join('.', parts.Take(2));
    }

    /// <summary>Newest fmod-gdextension release built for this Godot <c>major.minor</c>.</summary>
    private static async Task<(string Url, string CacheName)> FmodReleaseAsync(string? engineVersion, CancellationToken cancellationToken)
    {
        using JsonDocument document = await Http.GetJsonAsync(
            "https://api.github.com/repos/utopia-rise/fmod-gdextension/releases?per_page=100", cancellationToken).ConfigureAwait(false);

        string? wanted = engineVersion is null ? null : MajorMinor(engineVersion);
        (int Major, int Minor)? wantedVersion = ParseMajorMinor(wanted);

        (string Tag, JsonElement Release)? best = null;
        (int Major, int Minor)? bestGodot = null;

        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            string? tag = release.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() : null;
            if (tag is null)
            {
                continue;
            }

            int dash = tag.IndexOf('-');
            if (dash < 0)
            {
                continue;
            }

            (int Major, int Minor)? godot = ParseMajorMinor(tag[(dash + 1)..]);
            if (godot is null)
            {
                continue;
            }

            if (wantedVersion is { } want && (godot.Value.Major > want.Major ||
                                              (godot.Value.Major == want.Major && godot.Value.Minor > want.Minor)))
            {
                continue;
            }

            bool exact = wantedVersion is { } w && godot.Value == w;
            bool better = best is null || (exact && bestGodot != wantedVersion);
            if (better)
            {
                best = (tag, release);
                bestGodot = godot;
            }

            if (exact)
            {
                break;
            }
        }

        if (best is not { } chosen)
        {
            throw new IOException($"no fmod-gdextension release fits Godot {wanted ?? "?"}");
        }

        if (!chosen.Release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new IOException($"fmod-gdextension {chosen.Tag} has no assets");
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            if (name == "addons.zip")
            {
                string? url = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                if (url is null)
                {
                    break;
                }

                return (url, $"fmod-gdextension-{chosen.Tag}.zip");
            }
        }

        throw new IOException($"fmod-gdextension {chosen.Tag} has no addons.zip");
    }

    private static (int Major, int Minor)? ParseMajorMinor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] parts = value.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out int major) || !int.TryParse(parts[1], out int minor))
        {
            return null;
        }

        return (major, minor);
    }

    /// <summary>sentry-godot: the version the game shipped, otherwise the latest release.</summary>
    private static async Task<(string Url, string CacheName)> SentryReleaseAsync(string? gameDirectory, CancellationToken cancellationToken)
    {
        string? version = gameDirectory is null ? null : SentryVersionIn(gameDirectory);
        string url = version is null
            ? "https://api.github.com/repos/getsentry/sentry-godot/releases/latest"
            : $"https://api.github.com/repos/getsentry/sentry-godot/releases/tags/{version.Split('+')[0]}";

        JsonDocument document;
        try
        {
            document = await Http.GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            document = await Http.GetJsonAsync("https://api.github.com/repos/getsentry/sentry-godot/releases/latest", cancellationToken).ConfigureAwait(false);
        }

        using (document)
        {
            string tag = document.RootElement.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? "unknown"
                : "unknown";

            if (document.RootElement.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                    bool usable = name.StartsWith("sentry-godot-", StringComparison.Ordinal) &&
                                  name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                                  !name.Contains("debug-symbols", StringComparison.OrdinalIgnoreCase) &&
                                  !name.Contains("demo", StringComparison.OrdinalIgnoreCase);
                    if (!usable)
                    {
                        continue;
                    }

                    string? download = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                    if (download is not null)
                    {
                        return (download, $"sentry-godot-{tag}.zip");
                    }
                }
            }

            throw new IOException($"sentry-godot {tag} has no plugin zip");
        }
    }

    private static string? SentryVersionIn(string directory)
    {
        var files = new List<string>();
        ListFiles(directory, 0, 3, files);
        string? library = files.FirstOrDefault(path =>
            Path.GetFileName(path).StartsWith("libsentry.", StringComparison.OrdinalIgnoreCase));

        return library is null ? null : FindVersionWithHash(File.ReadAllBytes(library));
    }

    /// <summary>Finds <c>X.Y.Z+abcdef1</c> (a version followed by a short commit hash) in a binary.</summary>
    internal static string? FindVersionWithHash(ReadOnlySpan<byte> data)
    {
        int index = 0;
        while (index < data.Length)
        {
            if (!char.IsAsciiDigit((char)data[index]) ||
                (index > 0 && (char.IsAsciiDigit((char)data[index - 1]) || data[index - 1] == '.')))
            {
                index++;
                continue;
            }

            int start = index;
            int dots = 0;
            int cursor = index;
            while (cursor < data.Length && (char.IsAsciiDigit((char)data[cursor]) || data[cursor] == '.'))
            {
                if (data[cursor] == '.')
                {
                    dots++;
                }

                cursor++;
            }

            if (dots == 2 && cursor < data.Length && data[cursor] == '+')
            {
                int hashEnd = cursor + 1;
                while (hashEnd < data.Length && char.IsAsciiHexDigit((char)data[hashEnd]))
                {
                    hashEnd++;
                }

                int hashLength = hashEnd - cursor - 1;
                bool endsClean = hashEnd >= data.Length || data[hashEnd] == 0;
                if (hashLength is >= 7 and <= 12 && endsClean)
                {
                    return Encoding.ASCII.GetString(data[start..hashEnd]);
                }
            }

            index = Math.Max(cursor, index + 1);
        }

        return null;
    }

    /// <summary>spine-godot publishes builds per Spine runtime branch and Godot tag.</summary>
    private static async Task<(string Url, string CacheName)> SpineReleaseAsync(string project, string? engineVersion, CancellationToken cancellationToken)
    {
        const string BaseUrl = "https://spine-godot.s3.eu-central-1.amazonaws.com";
        if (string.IsNullOrWhiteSpace(engineVersion))
        {
            throw new IOException("the Godot version is unknown, cannot pick a spine-godot build");
        }

        var branches = new List<string>();
        if (SpineRuntimeIn(project) is { } detected)
        {
            branches.Add(detected);
        }

        foreach (string fallback in new[] { "4.2", "4.3", "4.1" })
        {
            if (!branches.Contains(fallback))
            {
                branches.Add(fallback);
            }
        }

        string tag = engineVersion + "-stable";
        foreach (string branch in branches)
        {
            string name = $"spine-godot-extension-{branch}-{tag}.zip";
            string url = $"{BaseUrl}/{branch}/{tag}/{name}";
            if (await Http.ExistsAsync(url, cancellationToken).ConfigureAwait(false))
            {
                return (url, name);
            }
        }

        throw new IOException($"no spine-godot GDExtension build found for Godot {engineVersion} (tried Spine {string.Join(", ", branches)})");
    }

    /// <summary>Spine runtime branch (<c>4.2</c>) from the first skeleton file of the project.</summary>
    private static string? SpineRuntimeIn(string project)
    {
        var stack = new Stack<string>();
        stack.Push(project);
        int checkedFiles = 0;

        while (stack.Count > 0)
        {
            string directory = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
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
                    if (!name.StartsWith('.'))
                    {
                        stack.Push(entry);
                    }

                    continue;
                }

                if (!name.EndsWith(".skel", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".spine-json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (++checkedFiles > 20)
                {
                    return null;
                }

                byte[] data;
                try
                {
                    data = File.ReadAllBytes(entry);
                }
                catch (IOException)
                {
                    continue;
                }

                string head = Encoding.UTF8.GetString(data, 0, Math.Min(256, data.Length));
                int position = head.IndexOf("4.", StringComparison.Ordinal);
                if (position < 0)
                {
                    continue;
                }

                int end = position;
                while (end < head.Length && (char.IsAsciiDigit(head[end]) || head[end] == '.'))
                {
                    end++;
                }

                string version = MajorMinor(head[position..end]);
                if (version.Length >= 3)
                {
                    return version;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the missing files out of a plugin archive, matching by file name and preferring
    /// the entry whose path shares the longest tail with the target
    /// (<c>bin/linux/x86_64/crashpad_handler</c> over <c>bin/linux/arm64/crashpad_handler</c>).
    /// </summary>
    private static int CopyFromZip(Gdextension extension, string archive)
    {
        using ZipArchive zip = ZipFile.OpenRead(PathUtil.FullPath(archive));
        List<string> names = zip.Entries.Select(entry => entry.FullName).ToList();

        int copied = 0;
        foreach (string target in extension.Missing())
        {
            string[] targetParts = target.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string targetName = targetParts[^1];

            string? best = null;
            int bestScore = -1;
            foreach (string name in names)
            {
                if (name.EndsWith('/'))
                {
                    continue;
                }

                string[] parts = name.Split('/');
                if (parts[^1] != targetName)
                {
                    continue;
                }

                int score = 0;
                for (int i = parts.Length - 1, j = targetParts.Length - 1; i >= 0 && j >= 0; i--, j--)
                {
                    if (!string.Equals(parts[i], targetParts[j], StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    score++;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = name;
                }
            }

            if (best is null)
            {
                continue;
            }

            ZipArchiveEntry? entry = zip.GetEntry(best);
            if (entry is null)
            {
                continue;
            }

            try
            {
                string? parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }

                entry.ExtractToFile(PathUtil.WithLongPathPrefix(target), overwrite: true);
                copied++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"cannot copy '{best}' to '{target}': {ex.Message}");
            }
        }

        return copied;
    }
}