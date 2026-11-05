using System.IO.Compression;
using UnGodot.Core.Binfmt;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Pck;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Core.Discovery;

public sealed record DiscoveryOptions
{
    public bool Recursive { get; init; } = true;
    public int MaxDepth { get; init; } = 3;
    public int MaxFiles { get; init; } = 4000;

    /// <summary>Look inside executables of the scanned folders for an appended pack.</summary>
    public bool ScanExecutables { get; init; } = true;

    /// <summary>Cap the number of executables that get a full magic scan (they can be huge).</summary>
    public int MaxExecutableScans { get; init; } = 6;

    /// <summary>Inspect ZIP/APK archives that may carry a pack.</summary>
    public bool InspectArchives { get; init; } = true;
}

/// <summary>One container found on disk that either holds a Godot pack or could hold one.</summary>
public sealed class PackCandidate
{
    public required string Path { get; init; }
    public required ContainerKind Kind { get; init; }
    public long Length { get; init; }

    /// <summary>Offset of the <c>GDPC</c> header inside the container, when found.</summary>
    public long PackOffset { get; init; } = -1;

    public PckHeader? Header { get; init; }
    public int? FileCount { get; init; }
    public bool Encrypted { get; init; }
    public bool Sparse { get; init; }
    public bool HasProjectSettings { get; init; }
    public int ScriptCount { get; init; }
    public string? ZipEntry { get; init; }
    public string? Error { get; init; }

    public bool IsPack => Header is not null;

    /// <summary>The candidate is a pack stored inside an archive and still has to be extracted.</summary>
    public bool IsArchivedPack => Header is null && ZipEntry is not null;

    public bool IsUsablePack => IsPack || IsArchivedPack;

    public string EngineVersion => Header?.EngineVersion ?? "?";

    public string PackLabel
    {
        get
        {
            if (Header is null)
            {
                return "no pack";
            }

            string sparse = Header.SparseBundle ? ", sparse bundle" : string.Empty;
            string encryption = Encrypted ? ", encrypted" : string.Empty;
            return $"v{Header.FormatVersion}, Godot {Header.EngineVersion}{encryption}{sparse}";
        }
    }

    public override string ToString() => $"{Path} [{PackLabel}]";
}

public sealed class DiscoveryResult
{
    public required string Input { get; init; }
    public List<PackCandidate> Candidates { get; } = [];

    /// <summary>Files that were opened while looking (useful with --verbose).</summary>
    public List<string> Inspected { get; } = [];

    /// <summary>Human readable remarks that are not errors (e.g. "this is a CRI CPK").</summary>
    public List<string> Notes { get; } = [];

    /// <summary>Whether the input itself was a directory.</summary>
    public bool InputIsDirectory { get; init; }

    public bool HasPacks => Candidates.Any(c => c.IsPack);

    public IEnumerable<PackCandidate> Packs => Candidates.Where(c => c.IsPack);

    /// <summary>The most likely base pack: the one carrying project.binary, else the largest.</summary>
    public PackCandidate? Primary =>
        Packs
            .OrderByDescending(c => c.HasProjectSettings)
            .ThenByDescending(c => c.FileCount ?? 0)
            .ThenByDescending(c => c.Length)
            .FirstOrDefault();

    /// <summary>The pack the user pointed at, when the input itself is a pack file.</summary>
    public PackCandidate? Explicit
    {
        get
        {
            if (InputIsDirectory)
            {
                return null;
            }

            string full = Path.GetFullPath(Input);
            return Candidates.FirstOrDefault(c =>
                c.IsUsablePack &&
                string.Equals(Path.GetFullPath(c.Path), full, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// What a single-pack command (`info`, `key`) should look at: the file the user named,
    /// and only when the input is a folder (or something unreadable) the best pack around it.
    /// </summary>
    public PackCandidate? Target => Explicit ?? Primary;
}

/// <summary>
/// Dynamic input resolution: whatever the user points at (a directory, a .pck, a game .exe,
/// a .zip/.apk, or a path that no longer exists), find every Godot pack reachable around it.
///
/// This is the "never give up on the first failure" layer: an unreadable executable does not
/// stop the discovery of the packs next to it, and the pack list is ordered so that merging
/// them later produces the same project the game runs.
/// </summary>
public static class GameDiscovery
{
    private static readonly string[] PackExtensions = [".pck", ".dat"];

    private static readonly string[] ContainerExtensions =
        [".pck", ".exe", ".x86_64", ".x86_32", ".bin", ".dat", ".cpk", ".zip", ".apk"];

    public static DiscoveryResult Discover(string input, DiscoveryOptions? options = null)
    {
        options ??= new DiscoveryOptions();
        string resolved = ResolveInputPath(input);
        bool isDirectory = Directory.Exists(resolved);
        var result = new DiscoveryResult { Input = resolved, InputIsDirectory = isDirectory };

        if (isDirectory)
        {
            DiscoverInDirectory(resolved, options, result);
            if (Directory.Exists(System.IO.Path.Combine(resolved, ".godot")) ||
                File.Exists(System.IO.Path.Combine(resolved, "project.godot")))
            {
                result.Notes.Add("this directory already looks like an extracted Godot project");
            }

            return result;
        }

        if (!File.Exists(resolved))
        {
            result.Notes.Add($"'{input}' does not exist");

            // Last resort: walk up until a folder exists and treat that as the subject.
            string? parent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(input));
            for (int depth = 0; depth < 4 && parent is not null; depth++)
            {
                if (Directory.Exists(parent))
                {
                    result.Notes.Add($"falling back to the parent folder {parent}");
                    DiscoverInDirectory(parent, options, result);
                    break;
                }

                parent = System.IO.Path.GetDirectoryName(parent);
            }

            return result;
        }

        InspectFile(resolved, options, result, forceDeepScan: true);

        // Look around the file as well: packs usually live next to the executable, or in a
        // sibling folder (exec/ + content/acorn/*.pck is a common layout).
        if (options.Recursive)
        {
            string? directory = System.IO.Path.GetDirectoryName(resolved);
            string? parent = directory is null ? null : System.IO.Path.GetDirectoryName(directory);
            var seen = new HashSet<string>(result.Candidates.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);

            if (directory is not null && Directory.Exists(directory))
            {
                DiscoverInDirectory(directory, options with { MaxDepth = 2 }, result, seen, includeSelf: false);
            }

            if (parent is not null && Directory.Exists(parent) && !string.Equals(parent, directory, StringComparison.OrdinalIgnoreCase))
            {
                DiscoverInDirectory(parent, options with { MaxDepth = 3 }, result, seen, includeSelf: true);
            }
        }

        return result;
    }

    private static string ResolveInputPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        if (Directory.Exists(input) || File.Exists(input))
        {
            return input;
        }

        // A missing extension is a common mistake (`game` instead of `game.pck`).
        foreach (string extension in ContainerExtensions)
        {
            if (File.Exists(input + extension))
            {
                return input + extension;
            }
        }

        return input;
    }

    private static void DiscoverInDirectory(
        string directory,
        DiscoveryOptions options,
        DiscoveryResult result,
        HashSet<string>? alreadyAdded = null,
        bool includeSelf = true)
    {
        alreadyAdded ??= new HashSet<string>(result.Candidates.Select(c => c.Path), StringComparer.OrdinalIgnoreCase);
        List<string> files = EnumerateCandidates(directory, options);
        if (!includeSelf)
        {
            files = files.Where(f => !string.Equals(f, result.Input, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Inspect cheap standalone packs first, so a directory full of packs does not pay for
        // scanning every executable next to them.
        var ordered = files
            .OrderByDescending(f => f.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int executableScans = 0;
        bool havePacks = result.HasPacks;

        foreach (string file in ordered)
        {
            if (!alreadyAdded.Add(file))
            {
                continue;
            }

            string extension = System.IO.Path.GetExtension(file).ToLowerInvariant();
            bool isExecutable = extension is ".exe" or ".x86_64" or ".x86_32" or ".bin" or "" ||
                                extension == ".dat";

            if (isExecutable && havePacks && !options.ScanExecutables)
            {
                continue;
            }

            if (isExecutable && havePacks)
            {
                // Only worth scanning a binary when it is the input itself; otherwise the
                // packs we already found are the better answer and scanning is expensive.
                continue;
            }

            if (isExecutable && executableScans >= options.MaxExecutableScans)
            {
                Log.Debug($"skipping executable scan of '{file}' (limit {options.MaxExecutableScans} reached)");
                continue;
            }

            bool deep = isExecutable;
            InspectFile(file, options, result, forceDeepScan: deep);
            if (isExecutable)
            {
                executableScans++;
            }

            havePacks = result.HasPacks;
        }
    }

    private static List<string> EnumerateCandidates(string directory, DiscoveryOptions options)
    {
        var files = new List<string>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((directory, 0));

        while (queue.Count > 0 && files.Count < options.MaxFiles)
        {
            (string current, int depth) = queue.Dequeue();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"cannot list '{current}': {ex.Message}");
                continue;
            }

            foreach (string entry in entries)
            {
                if (files.Count >= options.MaxFiles)
                {
                    Log.Warn($"stopping the directory scan at {options.MaxFiles} entries");
                    break;
                }

                string name = System.IO.Path.GetFileName(entry);
                if (name.StartsWith('.') || name is "System Volume Information" or "$RECYCLE.BIN")
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (options.Recursive && depth < options.MaxDepth)
                        {
                            queue.Enqueue((entry, depth + 1));
                        }

                        continue;
                    }

                    string extension = System.IO.Path.GetExtension(entry).ToLowerInvariant();
                    if (ContainerExtensions.Contains(extension))
                    {
                        files.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Debug($"cannot inspect '{entry}': {ex.Message}");
                }
            }
        }

        return files;
    }

    /// <summary>Opens one file and classifies it, adding a candidate when it holds a pack.</summary>
    public static PackCandidate? InspectFile(
        string path,
        DiscoveryOptions options,
        DiscoveryResult result,
        bool forceDeepScan)
    {
        FileSource? source = null;
        try
        {
            source = FileSource.Open(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string note = $"cannot open '{path}': {ex.Message}";
            Log.Debug(note);
            result.Notes.Add(note);
            result.Candidates.Add(new PackCandidate
            {
                Path = path,
                Kind = ContainerKind.Unknown,
                Length = 0,
                Error = ex.Message,
            });
            return null;
        }

        using (source)
        {
            result.Inspected.Add(path);
            Span<byte> head = stackalloc byte[16];
            int read = source.ReadAt(0, head);
            ContainerKind kind = BinaryFormat.DetectContainer(head[..Math.Max(read, 0)], path);

            if (kind == ContainerKind.CriCpk)
            {
                result.Notes.Add($"'{System.IO.Path.GetFileName(path)}' is a CRI CPK container (audio/movies), not a Godot pack; skipped");
                return null;
            }

            if (kind is ContainerKind.ZipArchive or ContainerKind.AndroidPackage)
            {
                if (options.InspectArchives)
                {
                    InspectArchive(path, kind, result);
                }

                return null;
            }

            if (kind == ContainerKind.Unknown && !forceDeepScan && !LooksLikePackExtension(path))
            {
                return null;
            }

            if (!PckLocator.TryLocate(source, out PckHeader? header, out string? locateError))
            {
                if (kind is ContainerKind.PeExecutable or ContainerKind.ElfExecutable or ContainerKind.MachO)
                {
                    result.Notes.Add($"'{System.IO.Path.GetFileName(path)}' is an executable without an embedded Godot pack");
                }
                else
                {
                    Log.Debug($"'{path}': {locateError}");
                }

                return null;
            }

            var candidate = DescribePack(source, path, kind, header!, result);
            result.Candidates.Add(candidate);
            return candidate;
        }
    }

    private static PackCandidate DescribePack(
        FileSource source,
        string path,
        ContainerKind kind,
        PckHeader header,
        DiscoveryResult result)
    {
        bool encrypted = header.DirEncrypted;
        int? fileCount = null;
        bool hasProject = false;
        int scriptCount = 0;
        string? error = null;

        try
        {
            PckFile pck = PckFile.Open(source, header, key: null);
            fileCount = pck.Entries.Count;
            hasProject = pck.Entries.Any(e => e.Path is "res://project.binary" or "res://project.godot");
            scriptCount = pck.Entries.Count(e =>
                e.Path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
                e.Path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
                e.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        }
        catch (PckException ex)
        {
            error = ex.Message;
            if (encrypted)
            {
                result.Notes.Add($"'{System.IO.Path.GetFileName(path)}' is encrypted; the key is needed to list its files");
            }
            else
            {
                Log.Debug($"cannot read the table of '{path}': {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is IOException or TruncatedSourceException)
        {
            error = ex.Message;
        }

        var candidate = new PackCandidate
        {
            Path = path,
            Kind = kind,
            Length = source.Length,
            PackOffset = header.Start,
            Header = header,
            FileCount = fileCount,
            Encrypted = encrypted,
            Sparse = header.SparseBundle,
            HasProjectSettings = hasProject,
            ScriptCount = scriptCount,
            Error = error,
        };

        Log.Debug($"found pack in '{path}' at offset {header.Start}: {candidate.PackLabel}, files={fileCount?.ToString() ?? "?"}");
        return candidate;
    }

    private static void InspectArchive(string path, ContainerKind kind, DiscoveryResult result)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(PathUtil.FullPath(path));
            var packs = archive.Entries
                .Where(e => e.FullName.EndsWith(".pck", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Length)
                .ToList();

            if (packs.Count == 0)
            {
                result.Notes.Add($"'{System.IO.Path.GetFileName(path)}' is a {kind.Describe()} without a .pck inside");
                return;
            }

            foreach (ZipArchiveEntry entry in packs)
            {
                result.Candidates.Add(new PackCandidate
                {
                    Path = path,
                    Kind = kind,
                    Length = entry.Length,
                    ZipEntry = entry.FullName,
                    Error = null,
                    Header = null, // the pack header needs the entry to be materialised first
                });
            }

            result.Notes.Add($"'{System.IO.Path.GetFileName(path)}' contains {packs.Count} pack(s): the largest is '{packs[0].FullName}'");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            result.Notes.Add($"cannot read the archive '{path}': {ex.Message}");
        }
    }

    private static bool LooksLikePackExtension(string path)
    {
        string extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        return PackExtensions.Contains(extension) || ContainerExtensions.Contains(extension);
    }
}