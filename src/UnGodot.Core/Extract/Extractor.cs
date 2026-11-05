using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using UnGodot.Core.Crypto;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Pck;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Core.Extract;

public sealed class ExtractOptions
{
    public bool Overwrite { get; init; } = true;

    /// <summary>Optional glob (or several separated by <c>;</c>) applied to the relative path.</summary>
    public string? Filter { get; init; }

    public bool VerifyMd5 { get; init; } = true;

    /// <summary>Keep going after a broken entry instead of failing the whole run.</summary>
    public bool ContinueOnError { get; init; } = true;

    public int? MaxDegree { get; init; }

    /// <summary>Write a report of every extracted file next to the output (used for diffs).</summary>
    public bool WriteManifest { get; init; } = true;
}

public sealed class ExtractStats
{
    public int Written;
    public int Skipped;
    public int Decrypted;
    public int Failed;
    public long BytesWritten;
    public TimeSpan Elapsed;

    public List<string> ChecksumErrors { get; } = [];
    public List<string> Errors { get; } = [];

    public override string ToString() =>
        $"{Written} written, {Decrypted} decrypted, {Skipped} skipped, {Failed} failed, {PathUtil.HumanSize(BytesWritten)} in {Elapsed.TotalSeconds:0.0}s";
}

public static class SafePath
{
    /// <summary>Turns a <c>res://</c> style path into a safe relative path (or null when unsafe).</summary>
    public static string? Relative(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parts = new List<string>();
        string rest = raw;

        if (raw.StartsWith("res://", StringComparison.Ordinal))
        {
            rest = raw[6..];
        }
        else if (raw.StartsWith("user://", StringComparison.Ordinal))
        {
            rest = raw[7..];
            parts.Add("@@user@@");
        }

        rest = rest.Replace('\\', '/');
        foreach (string part in rest.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "." || part == ".." || part.EndsWith(':'))
            {
                return null;
            }

            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return null;
            }

            parts.Add(part);
        }

        return parts.Count == 0 ? null : Path.Combine(parts.ToArray());
    }
}

/// <summary>Writes the pack contents to disk, decrypting and verifying as it goes.</summary>
public static class Extractor
{
    private sealed class Job
    {
        public required PckEntry Entry { get; init; }
        public required string Target { get; init; }
        public required string Relative { get; init; }
    }

    public static ExtractStats Extract(
        IRandomAccessSource source,
        PckFile pck,
        string outputDirectory,
        byte[]? key,
        ExtractOptions options,
        CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        var stats = new ExtractStats();
        string root = PathUtil.FullPath(outputDirectory);
        Directory.CreateDirectory(root);

        string[] filters = (options.Filter ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var jobs = new List<Job>(pck.Entries.Count);
        foreach (PckEntry entry in pck.Entries)
        {
            if (entry.Removed)
            {
                stats.Skipped++;
                continue;
            }

            string? relative = SafePath.Relative(entry.Path);
            if (relative is null)
            {
                Log.Warn($"skipping unsafe path '{entry.Path}'");
                stats.Skipped++;
                continue;
            }

            if (filters.Length > 0 && !filters.Any(f => GlobMatch(relative, f) || GlobMatch(entry.Path, f)))
            {
                stats.Skipped++;
                continue;
            }

            jobs.Add(new Job
            {
                Entry = entry,
                Target = Path.Combine(root, relative),
                Relative = relative,
            });
        }

        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (Job job in jobs)
        {
            string? parent = Path.GetDirectoryName(job.Target);
            if (parent is not null)
            {
                directories.Add(parent);
            }
        }

        foreach (string directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        long total = jobs.Count;
        long done = 0;
        long lastReport = Stopwatch.GetTimestamp();
        var manifest = options.WriteManifest ? new List<string>(jobs.Count) : null;
        object manifestGate = new();

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.MaxDegree ?? Environment.ProcessorCount,
        };

        try
        {
            Parallel.ForEach(jobs, parallelOptions, job =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!options.Overwrite && File.Exists(job.Target))
                    {
                        long existing = new FileInfo(job.Target).Length;
                        if (!job.Entry.Encrypted && existing == job.Entry.Size)
                        {
                            Interlocked.Increment(ref stats.Skipped);
                            return;
                        }
                    }

                    (byte[] content, bool decrypted) = Decode(source, pck, job.Entry, key);
                    Interlocked.Add(ref stats.BytesWritten, content.Length);
                    if (decrypted)
                    {
                        Interlocked.Increment(ref stats.Decrypted);
                    }

                    if (options.VerifyMd5 && job.Entry.HasMd5)
                    {
                        bool ok = content.Length == job.Entry.Size && GodotCrypto.Md5Equals(content, job.Entry.Md5);
                        if (!ok)
                        {
                            lock (stats.ChecksumErrors)
                            {
                                stats.ChecksumErrors.Add(job.Entry.Path);
                            }
                        }
                    }

                    WriteAtomic(job.Target, content);
                    Interlocked.Increment(ref stats.Written);

                    if (manifest is not null)
                    {
                        lock (manifestGate)
                        {
                            manifest.Add($"{GodotCrypto.ToHex(job.Entry.Md5)}  {job.Relative.Replace('\\', '/')}  {content.Length}");
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or PckException or UnauthorizedAccessException or TruncatedSourceException)
                {
                    Interlocked.Increment(ref stats.Failed);
                    lock (stats.Errors)
                    {
                        stats.Errors.Add($"{job.Entry.Path}: {ex.Message}");
                    }

                    Log.Warn($"cannot extract '{job.Entry.Path}': {ex.Message}");
                    if (!options.ContinueOnError)
                    {
                        throw new IOException($"extraction aborted at '{job.Entry.Path}'", ex);
                    }
                }

                long current = Interlocked.Increment(ref done);
                long now = Stopwatch.GetTimestamp();
                if (now - lastReport > Stopwatch.Frequency * 3L || current == total)
                {
                    lastReport = now;
                    Log.Debug($"extracted {current}/{total} files");
                }
            });
        }
        catch (OperationCanceledException)
        {
            Log.Warn("extraction cancelled");
        }

        stats.Elapsed = watch.Elapsed;
        if (manifest is not null)
        {
            try
            {
                string manifestPath = Path.Combine(root, "ungodot_manifest.txt");
                File.WriteAllLines(manifestPath, manifest.Order(StringComparer.Ordinal), new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"cannot write the manifest: {ex.Message}");
            }
        }

        if (stats.ChecksumErrors.Count > 0)
        {
            Log.Warn($"{stats.ChecksumErrors.Count} file(s) failed the MD5 check (first: {stats.ChecksumErrors[0]})");
        }

        return stats;
    }

    /// <summary>Reads one entry, decrypting it when needed. Throws on truncated or corrupt data.</summary>
    public static (byte[] Content, bool Decrypted) Decode(IRandomAccessSource source, PckFile pck, PckEntry entry, byte[]? key)
    {
        long start = pck.DataOffset(entry);
        if (start < 0 || start > source.Length)
        {
            throw new PckException($"the entry points outside the container (offset {start})");
        }

        if (entry.Encrypted)
        {
            if (key is null)
            {
                throw new PckException("the file is encrypted but no key is available");
            }

            EncryptedBlob blob = EncryptedBlob.Read(source, start, out string? error)
                ?? throw new PckException($"cannot parse the encrypted file: {error}");
            byte[] plain = blob.TryDecrypt(key)
                ?? throw new PckException("cannot decrypt: wrong key or corrupted data (MD5 mismatch)");
            return (plain, true);
        }

        if (entry.Size > source.Length - start)
        {
            throw new PckException($"the file lies outside the container (offset {start}, size {entry.Size})");
        }

        if (entry.Size == 0)
        {
            return ([], false);
        }

        return (source.ReadAt(start, (int)entry.Size), false);
    }

    private static void WriteAtomic(string target, byte[] content)
    {
        string full = PathUtil.WithLongPathPrefix(target);
        string part = full + ".part";
        using (var stream = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: false);
        }

        File.Move(part, full, overwrite: true);
    }

    /// <summary>Minimal glob matcher: <c>*</c>, <c>?</c>, <c>**</c>, case insensitive.</summary>
    public static bool GlobMatch(string text, string pattern)
    {
        text = text.Replace('\\', '/');
        pattern = pattern.Replace('\\', '/');
        return MatchSpan(text.AsSpan(), pattern.AsSpan());
    }

    private static bool MatchSpan(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        int ti = 0;
        int pi = 0;
        int starText = -1;
        int starPattern = -1;

        while (ti < text.Length)
        {
            if (pi < pattern.Length && (pattern[pi] == '?' || char.ToLowerInvariant(pattern[pi]) == char.ToLowerInvariant(text[ti])))
            {
                ti++;
                pi++;
            }
            else if (pi < pattern.Length && pattern[pi] == '*')
            {
                starPattern = pi++;
                starText = ti;
            }
            else if (starPattern >= 0)
            {
                pi = starPattern + 1;
                ti = ++starText;
            }
            else
            {
                return false;
            }
        }

        while (pi < pattern.Length && pattern[pi] == '*')
        {
            pi++;
        }

        return pi == pattern.Length;
    }
}