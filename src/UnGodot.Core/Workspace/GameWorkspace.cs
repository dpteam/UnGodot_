using System.IO.Compression;
using UnGodot.Core.Binfmt;
using UnGodot.Core.Discovery;
using UnGodot.Core.Extract;
using UnGodot.Core.IO;
using UnGodot.Core.KeyFind;
using UnGodot.Core.Logging;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Core.Workspace;

public sealed class WorkspaceOptions
{
    public DiscoveryOptions Discovery { get; init; } = new();

    /// <summary>A key supplied by the user (hex string is parsed by the caller).</summary>
    public byte[]? Key { get; init; }

    public bool RecoverKeyAutomatically { get; init; } = true;

    /// <summary>Extra files (memory dumps, binaries) to scan for the key.</summary>
    public IReadOnlyList<string> ExtraKeySources { get; init; } = [];

    public string? Wordlist { get; init; }
}

/// <summary>An opened pack plus the source it lives in.</summary>
public sealed class OpenedPack : IDisposable
{
    private string? _temporaryFile;

    public required PackCandidate Candidate { get; init; }
    public required IRandomAccessSource Source { get; init; }
    public required Pck.PckHeader Header { get; init; }
    public PckFile? Pack { get; set; }
    public byte[]? Key { get; set; }
    public string? Error { get; set; }

    public string Name => Path.GetFileName(Candidate.Path);

    public bool NeedsKey => Header.DirEncrypted && Pack is null;

    internal void UseTemporaryFile(string path) => _temporaryFile = path;

    public void Dispose()
    {
        Source.Dispose();
        if (_temporaryFile is not null)
        {
            try
            {
                File.Delete(_temporaryFile);
            }
            catch
            {
                // best effort
            }

            _temporaryFile = null;
        }
    }
}

/// <summary>
/// The orchestrator: discovery -> pack opening -> key recovery -> extraction.
///
/// Every step degrades instead of aborting: a pack that cannot be opened is reported and
/// the remaining ones continue, and a missing executable only downgrades key recovery.
/// </summary>
public sealed class GameWorkspace
{
    private readonly WorkspaceOptions _options;

    public GameWorkspace(WorkspaceOptions options)
    {
        _options = options;
    }

    public DiscoveryResult Discover(string input) => GameDiscovery.Discover(input, _options.Discovery);

    /// <summary>
    /// Opens a candidate, using the provided key or recovering it from the binaries around
    /// the pack. Returns an <see cref="OpenedPack"/> even when the key could not be
    /// recovered (so `info` can still show what is known).
    /// </summary>
    public OpenedPack Open(PackCandidate candidate, CancellationToken cancellationToken = default)
    {
        (IRandomAccessSource source, string? temporary) = Materialize(candidate);
        PackCandidate described = candidate;

        if (candidate.Header is null)
        {
            // Pack inside a ZIP/APK: locate it in the materialised file.
            if (!Pck.PckLocator.TryLocate(source, out Pck.PckHeader? header, out string? error))
            {
                source.Dispose();
                throw new Pck.PckException($"no Godot pack found inside '{candidate.Path}': {error}");
            }

            described = new PackCandidate
            {
                Path = candidate.Path,
                Kind = candidate.Kind,
                Length = source.Length,
                PackOffset = header!.Start,
                Header = header,
                Encrypted = header.DirEncrypted,
                Sparse = header.SparseBundle,
                ZipEntry = candidate.ZipEntry,
            };
        }

        var opened = new OpenedPack
        {
            Candidate = described,
            Source = source,
            Header = described.Header!,
        };

        if (temporary is not null)
        {
            opened.UseTemporaryFile(temporary);
        }

        try
        {
            opened.Pack = PckFile.Open(source, opened.Header, _options.Key);
            opened.Key = _options.Key;
            return opened;
        }
        catch (Pck.PckException ex) when (opened.Header.DirEncrypted && !opened.Header.SparseBundle)
        {
            opened.Error = ex.Message;
        }

        if (!opened.Header.DirEncrypted || !_options.RecoverKeyAutomatically)
        {
            return opened;
        }

        Log.Info("the pack directory is encrypted, recovering the key...");
        List<string> keySources = FindKeySources(described);
        byte[]? key = KeyRecovery.Recover(source, opened.Header, keySources, _options.Wordlist, cancellationToken);
        if (key is null)
        {
            opened.Error ??= "the pack directory is encrypted and the key could not be recovered";
            return opened;
        }

        opened.Key = key;
        try
        {
            opened.Pack = PckFile.Open(source, opened.Header, key);
            opened.Error = null;
            Log.Info($"key: {Crypto.GodotCrypto.ToHex(key)}");
        }
        catch (Pck.PckException ex)
        {
            opened.Error = ex.Message;
        }

        return opened;
    }

    /// <summary>Files that may hold the encryption key, from nearest to farthest.</summary>
    public List<string> FindKeySources(PackCandidate candidate)
    {
        var sources = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return;
            }

            if (seen.Add(Path.GetFullPath(path)))
            {
                sources.Add(path);
            }
        }

        // The pack holds no key: only scan the container when it is not a standalone pack.
        bool isStandalonePack = candidate.Kind == ContainerKind.GodotPack && candidate.PackOffset == 0;
        if (!isStandalonePack)
        {
            Add(candidate.Path);
        }

        string? directory = Path.GetDirectoryName(candidate.Path);
        if (directory is null)
        {
            return sources;
        }

        // Executables and native libraries next to the pack, then one level up (exec/ vs
        // content/ layouts), then the --scan extras.
        AddRange(Add, directory, 1, 512);
        string? parent = Path.GetDirectoryName(directory);
        if (parent is not null)
        {
            AddRange(Add, parent, 2, 512);
            string? grandParent = Path.GetDirectoryName(parent);
            if (grandParent is not null && Directory.GetParent(grandParent) is not null)
            {
                // A game split into exec/ and content/<name>/ (Sonic Colors Ultimate,
                // Unreal-style layouts) keeps the key in the executable two folders away.
                AddRange(Add, grandParent, 1, 256);
            }
        }

        foreach (string extra in _options.ExtraKeySources)
        {
            if (Directory.Exists(extra))
            {
                AddRange(Add, extra, 2, 512);
                continue;
            }

            Add(extra);
        }

        return sources;
    }

    private static void AddRange(Action<string> add, string directory, int depth, int maxEntries)
    {
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((directory, 0));
        int budget = maxEntries;

        while (queue.Count > 0 && budget-- > 0)
        {
            (string current, int level) = queue.Dequeue();
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
                if (budget-- <= 0)
                {
                    return;
                }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        if (level < depth && !Path.GetFileName(entry).StartsWith('.'))
                        {
                            queue.Enqueue((entry, level + 1));
                        }

                        continue;
                    }

                    string extension = Path.GetExtension(entry).ToLowerInvariant();
                    if (extension is ".exe" or ".x86_64" or ".x86_32" or ".dll" or ".so" or ".dylib" or ".bin" or ".dmp")
                    {
                        add(entry);
                    }
                    else if (extension.Length == 0 && new FileInfo(entry).Length > 1 << 20)
                    {
                        add(entry);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Ignore a single unreadable entry.
                }
            }
        }
    }

    /// <summary>Result of extracting one or several packs into a single directory.</summary>
    public sealed class MultiExtractResult
    {
        public List<(PackCandidate Candidate, ExtractStats Stats)> PerPack { get; } = [];
        public List<string> Failures { get; } = [];
        public int TotalWritten { get; set; }
        public int TotalSkipped { get; set; }
        public int TotalFailed { get; set; }
        public int TotalDecrypted { get; set; }
        public int OverwrittenEntries { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    /// <summary>
    /// Extracts every pack found around the input into one directory. Packs without
    /// <c>project.binary</c> are written first so the base pack wins on conflicts (DLC and
    /// patch layering, the same order Godot itself mounts them in).
    /// </summary>
    public MultiExtractResult ExtractAll(
        DiscoveryResult discovery,
        string outputDirectory,
        ExtractOptions options,
        IEnumerable<PackCandidate>? only = null,
        CancellationToken cancellationToken = default)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = new MultiExtractResult();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<PackCandidate> packs = (only ?? discovery.Candidates)
            .Where(c => c.IsUsablePack)
            .OrderBy(c => c.HasProjectSettings ? 1 : 0)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ZipEntry ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (packs.Count == 0)
        {
            throw new InvalidOperationException("no Godot pack was found to extract");
        }

        Log.Info($"extracting {packs.Count} pack(s) into {outputDirectory}");
        int index = 0;
        foreach (PackCandidate candidate in packs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            string label = candidate.ZipEntry is null
                ? Path.GetFileName(candidate.Path)
                : $"{Path.GetFileName(candidate.Path)}:{candidate.ZipEntry}";
            Log.Info($"[{index}/{packs.Count}] {label}");

            OpenedPack? opened = null;
            try
            {
                opened = Open(candidate, cancellationToken);
                if (opened.Pack is null)
                {
                    result.Failures.Add($"{label}: {opened.Error ?? "cannot open the pack"}");
                    continue;
                }

                foreach (Pck.PckEntry entry in opened.Pack.Entries)
                {
                    if (entry.Removed)
                    {
                        continue;
                    }

                    if (!seenPaths.Add(entry.Path))
                    {
                        result.OverwrittenEntries++;
                    }
                }

                ExtractStats stats = Extractor.Extract(opened.Source, opened.Pack, outputDirectory, opened.Key, options, cancellationToken);
                result.PerPack.Add((candidate, stats));
                result.TotalWritten += stats.Written;
                result.TotalSkipped += stats.Skipped;
                result.TotalFailed += stats.Failed;
                result.TotalDecrypted += stats.Decrypted;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is Pck.PckException or IOException or InvalidOperationException)
            {
                Log.Error($"{label}: {ex.Message}");
                result.Failures.Add($"{label}: {ex.Message}");
            }
            finally
            {
                opened?.Dispose();
            }
        }

        result.Elapsed = watch.Elapsed;
        return result;
    }

    private static (IRandomAccessSource Source, string? TemporaryFile) Materialize(PackCandidate candidate)
    {
        if (candidate.ZipEntry is null)
        {
            return (FileSource.Open(candidate.Path), null);
        }

        string tempDirectory = Path.Combine(Path.GetTempPath(), "ungodot");
        Directory.CreateDirectory(tempDirectory);
        string temp = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".pck");

        using ZipArchive archive = ZipFile.OpenRead(PathUtil.FullPath(candidate.Path));
        ZipArchiveEntry entry = archive.GetEntry(candidate.ZipEntry)
            ?? throw new Pck.PckException($"'{candidate.ZipEntry}' vanished from '{candidate.Path}'");

        Log.Info($"extracting the pack out of {Path.GetFileName(candidate.Path)} ({PathUtil.HumanSize(entry.Length)})");
        entry.ExtractToFile(PathUtil.FullPath(temp), overwrite: true);
        return (FileSource.Open(temp), temp);
    }
}