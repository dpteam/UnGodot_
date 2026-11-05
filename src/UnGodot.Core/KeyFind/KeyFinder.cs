using System.Buffers;
using System.Diagnostics;
using UnGodot.Core.Binfmt;
using UnGodot.Core.Crypto;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Pck;

namespace UnGodot.Core.KeyFind;

/// <summary>How strictly the decrypted file table header is checked.</summary>
public enum Tier
{
    /// <summary>The first path must look like a real path (path characters, no leading separator).</summary>
    Strict,

    /// <summary>The first path must use path characters only.</summary>
    Loose,

    /// <summary>The first path must be printable ASCII.</summary>
    Weak,

    /// <summary>Any non-control byte, including UTF-8 (a first file with an accented name).</summary>
    Utf8,
}

public sealed class KeyHit
{
    public required long Offset { get; init; }
    public required byte[] Key { get; init; }
    public required int Score { get; init; }
}

/// <summary>
/// Knows what a decrypted file table header must look like: a u32 path length followed by
/// the path itself. A 32 byte window that turns this into plausible text is a key
/// candidate; the caller then verifies it against the whole table (MD5).
/// </summary>
public sealed class Oracle
{
    private readonly byte[] _iv;
    private readonly byte[] _ciphertext;

    public Oracle(byte[] iv, byte[] ciphertext)
    {
        if (iv.Length != GodotCrypto.IvLength)
        {
            throw new ArgumentException("the IV must be 16 bytes", nameof(iv));
        }

        _iv = iv;
        _ciphertext = ciphertext;
    }

    /// <summary>Builds the oracle from the encrypted file table data (starting at its blob header).</summary>
    public static Oracle? TryCreate(IRandomAccessSource source, long dirBlobOffset, out string? error)
    {
        EncryptedBlob? blob = EncryptedBlob.ReadPrefix(source, dirBlobOffset, 64, out error);
        if (blob is null)
        {
            return null;
        }

        if (blob.Ciphertext.Length < 32)
        {
            error = "the encrypted file table is too short to test keys against";
            return null;
        }

        return new Oracle(blob.Iv, blob.Ciphertext);
    }

    /// <summary>
    /// Decrypts the first blocks with <paramref name="key"/> and scores the structure.
    /// Returns -1 when the plaintext cannot be a file table header.
    /// </summary>
    public int Test(ReadOnlySpan<byte> key, Tier tier)
    {
        if (_ciphertext.Length < 32)
        {
            return -1;
        }

        var schedule = new Aes256Schedule(key);
        Span<byte> plain = stackalloc byte[16];
        schedule.EncryptBlock(_iv, plain);
        for (int i = 0; i < 16; i++)
        {
            plain[i] ^= _ciphertext[i];
        }

        uint length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(plain);
        if (length < 4 || length > 1 << 20 || (length & 3) != 0)
        {
            return -1;
        }

        int pathBytes = (int)Math.Min(length - 4, 12);
        ReadOnlySpan<byte> path = plain.Slice(4, pathBytes);

        bool ByteOk(byte b) => tier == Tier.Utf8 ? b >= 0x20 : IsPathByte(b);

        foreach (byte b in path)
        {
            if (!ByteOk(b))
            {
                return -1;
            }
        }

        switch (tier)
        {
            case Tier.Strict:
                return IsPathStart(plain[4]) ? 4 : -1;
            case Tier.Loose:
                return 3;
            case Tier.Weak:
                foreach (byte b in path)
                {
                    if (b is < 0x20 or > 0x7E)
                    {
                        return -1;
                    }
                }

                return 2;
            default:
                return 1;
        }
    }

    private static bool IsPathStart(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'0' && b <= (byte)'9') ||
        b == (byte)'.' || b == (byte)'_';

    private static bool IsPathByte(byte b) =>
        (b >= (byte)'a' && b <= (byte)'z') ||
        (b >= (byte)'A' && b <= (byte)'Z') ||
        (b >= (byte)'0' && b <= (byte)'9') ||
        b == (byte)'.' || b == (byte)'_' || b == (byte)'/' || b == (byte)':' ||
        b == (byte)'-' || b == (byte)' ';
}

/// <summary>Parallel, early-exit key search over byte ranges of a container.</summary>
public static class KeyScanner
{
    /// <summary>Work unit size; every CPU core gets whole chunks of this size.</summary>
    public const int ChunkSize = 1 << 20;

    /// <summary>Windows with fewer distinct byte values are skipped by the cheap pre-filter.</summary>
    private const int MinDistinctBytes = 12;

    public static KeyHit? ScanVerified(
        IRandomAccessSource source,
        Oracle oracle,
        IReadOnlyList<(long Start, long End)> ranges,
        int step,
        Tier tier,
        bool prefilter,
        Func<byte[], bool> verify,
        CancellationToken cancellationToken)
    {
        step = Math.Max(1, step);
        List<Segment> segments = BuildSegments(ranges, source.Length);
        if (segments.Count == 0)
        {
            return null;
        }

        KeyHit?[] hits = new KeyHit?[1];
        object gate = new();

        var options = new ParallelOptions { CancellationToken = cancellationToken };

        try
        {
            Parallel.ForEach(
                segments,
                options,
                () => ArrayPool<byte>.Shared.Rent(ChunkSize + 64),
                (segment, state, buffer) =>
                {
                    if (Volatile.Read(ref hits[0]) is not null)
                    {
                        state.Stop();
                        return buffer;
                    }

                    KeyHit? found = Sweep(source, oracle, segment, step, tier, prefilter, verify, buffer, cancellationToken);
                    if (found is not null)
                    {
                        lock (gate)
                        {
                            hits[0] ??= found;
                        }

                        state.Stop();
                    }

                    return buffer;
                },
                buffer => ArrayPool<byte>.Shared.Return(buffer));
        }
        catch (OperationCanceledException)
        {
        }

        return hits[0];
    }

    private readonly record struct Segment(long ReadStart, long WindowStart, long WindowEnd, long ReadEnd);

    private static List<Segment> BuildSegments(IReadOnlyList<(long Start, long End)> ranges, long length)
    {
        var segments = new List<Segment>();
        foreach ((long start, long end) in ranges)
        {
            long a = Math.Max(0, start);
            long b = Math.Min(length, end);
            if (b - a < GodotCrypto.KeyLength)
            {
                continue;
            }

            for (long offset = a; offset < b; offset += ChunkSize)
            {
                long windowEnd = Math.Min(offset + ChunkSize, b);
                if (windowEnd - offset < GodotCrypto.KeyLength)
                {
                    break;
                }

                // Start a little early so a key straddling the chunk boundary is covered by
                // both sides; the extra 31 bytes are cheap and remove a whole class of
                // "the key is there but was never tested" bugs.
                long windowStart = Math.Max(a, offset - (GodotCrypto.KeyLength - 1));
                long readEnd = Math.Min(windowEnd + GodotCrypto.KeyLength - 1, b);
                segments.Add(new Segment(windowStart, windowStart, windowEnd, readEnd));
            }
        }

        return segments;
    }

    private static KeyHit? Sweep(
        IRandomAccessSource source,
        Oracle oracle,
        Segment segment,
        int step,
        Tier tier,
        bool prefilter,
        Func<byte[], bool> verify,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        int want = (int)(segment.ReadEnd - segment.ReadStart);
        int read = source.ReadAt(segment.ReadStart, buffer.AsSpan(0, want));
        if (read < GodotCrypto.KeyLength)
        {
            return null;
        }

        long limit = Math.Min(segment.WindowEnd, segment.ReadStart + read) - GodotCrypto.KeyLength;
        int counter = 0;

        for (long offset = segment.WindowStart; offset <= limit; offset += step)
        {
            if ((++counter & 0xFFFF) == 0 && cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            int local = (int)(offset - segment.ReadStart);
            ReadOnlySpan<byte> window = buffer.AsSpan(local, GodotCrypto.KeyLength);
            if (prefilter && !PassesPrefilter(window))
            {
                continue;
            }

            int score = oracle.Test(window, tier);
            if (score < 0)
            {
                continue;
            }

            byte[] key = window.ToArray();
            if (verify(key))
            {
                return new KeyHit { Offset = offset, Key = key, Score = score };
            }
        }

        return null;
    }

    /// <summary>True when the 32 byte window has enough distinct values to plausibly be a key.</summary>
    public static bool PassesPrefilter(ReadOnlySpan<byte> window)
    {
        Span<ulong> seen = stackalloc ulong[4];
        int distinct = 0;
        foreach (byte b in window)
        {
            ref ulong slot = ref seen[b >> 6];
            ulong bit = 1UL << (b & 63);
            if ((slot & bit) != 0)
            {
                continue;
            }

            slot |= bit;
            if (++distinct >= MinDistinctBytes)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Tries the two ways Godot derives a key from a password, plus a verbatim hex password.</summary>
    public static KeyHit? TryPasswords(Oracle oracle, IEnumerable<string> passwords, CancellationToken cancellationToken = default)
    {
        foreach (string password in passwords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<byte[]>
            {
                GodotCrypto.KeyFromPassword(password),
            };

            var padded = new byte[GodotCrypto.KeyLength];
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(password);
            Array.Copy(raw, padded, Math.Min(raw.Length, padded.Length));
            candidates.Add(padded);

            if (GodotCrypto.TryParseHexKey(password, out byte[] hex))
            {
                candidates.Add(hex);
            }

            foreach (byte[] key in candidates)
            {
                foreach (Tier tier in new[] { Tier.Strict, Tier.Loose, Tier.Weak, Tier.Utf8 })
                {
                    int score = oracle.Test(key, tier);
                    if (score >= 0)
                    {
                        return new KeyHit { Offset = 0, Key = key, Score = score };
                    }
                }
            }
        }

        return null;
    }

    public static readonly string[] DefaultPasswords =
    [
        "godot",
        "godotengine",
        "godot4",
        "godot3",
        "encryption",
        "encrypt",
        "password",
        "secret",
        "test",
        "key",
        "admin",
        "123456",
        "12345678",
        "qwerty",
        "letmein",
        "abc123",
        "0000000000000000000000000000000000000000000000000000000000000000",
    ];
}

/// <summary>The pass ladder used by the automatic recovery: cheap first, exhaustive last.</summary>
public static class KeyRecovery
{
    private static readonly (Tier Tier, int Step, bool Narrowed, bool Prefilter)[] Passes =
    [
        (Tier.Strict, 1, true, true),
        (Tier.Loose, 1, true, true),
        (Tier.Strict, 8, false, true),
        (Tier.Strict, 1, false, true),
        (Tier.Loose, 8, false, true),
        (Tier.Loose, 1, false, true),
        (Tier.Weak, 8, false, true),
        (Tier.Weak, 1, false, false),
        (Tier.Utf8, 1, false, false),
    ];

    /// <summary>Searches every candidate file for the key that decrypts <paramref name="header"/>.</summary>
    public static byte[]? Recover(
        IRandomAccessSource packSource,
        PckHeader header,
        IEnumerable<string> candidateFiles,
        string? wordlistPath,
        CancellationToken cancellationToken)
    {
        if (!header.DirEncrypted)
        {
            Log.Debug("the pack directory is not encrypted; no key needed");
            return null;
        }

        Oracle? oracle = Oracle.TryCreate(packSource, header.DirOffset + 4, out string? oracleError);
        if (oracle is null)
        {
            Log.Warn($"cannot build the key oracle: {oracleError}");
            return null;
        }

        bool Verify(byte[] key)
        {
            try
            {
                _ = Pck.Pck.Open(packSource, header, key);
                return true;
            }
            catch (Exception ex)
            {
                Log.Verbose($"candidate key rejected: {ex.Message}");
                return false;
            }
        }

        foreach (string candidate in candidateFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileSource? source = null;
            try
            {
                source = FileSource.Open(candidate);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug($"cannot scan '{candidate}': {ex.Message}");
                continue;
            }

            using (source)
            {
                List<(long Start, long End)> ranges = BinaryFormat.CandidateRanges(source);
                long total = ranges.Sum(r => r.End - r.Start);
                Log.Info($"scanning {candidate} ({PathUtil.HumanSize(total)} of candidate data) for the key");

                var watch = Stopwatch.StartNew();
                foreach ((Tier tier, int step, bool narrowed, bool prefilter) in Passes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyList<(long Start, long End)> scanRanges = narrowed
                        ? ranges
                        : new List<(long, long)> { (0, source.Length) };

                    Log.Verbose($"pass tier={tier} step={step} narrowed={narrowed} prefilter={prefilter}");
                    KeyHit? hit = KeyScanner.ScanVerified(
                        source, oracle, scanRanges, step, tier, prefilter, Verify, cancellationToken);

                    if (hit is not null)
                    {
                        Log.Info($"key found at offset {hit.Offset} of {candidate} after {watch.Elapsed.TotalSeconds:0.0}s");
                        return hit.Key;
                    }
                }
            }
        }

        List<string> passwords = LoadWordlist(wordlistPath);
        if (passwords.Count > 0)
        {
            Log.Info($"trying {passwords.Count} password(s)");
            KeyHit? hit = KeyScanner.TryPasswords(oracle, passwords, cancellationToken);
            if (hit is not null)
            {
                Log.Info("key recovered from the wordlist");
                return hit.Key;
            }
        }

        PrintHelp(candidateFiles.Any());
        return null;
    }

    private static List<string> LoadWordlist(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return KeyScanner.DefaultPasswords.ToList();
        }

        try
        {
            return File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"cannot read the wordlist '{path}': {ex.Message}");
            return KeyScanner.DefaultPasswords.ToList();
        }
    }

    private static void PrintHelp(bool scannedAnything)
    {
        Log.Warn("could not recover the encryption key automatically. This usually means one of:");
        if (!scannedAnything)
        {
            Log.Warn("  - the key lives in the game executable, which was not found next to the pack");
            Log.Warn("    (pass it with --scan <path-to-executable>)");
        }
        else
        {
            Log.Warn("  - the key is not stored verbatim in the scanned files (hardened engines such as");
            Log.Warn("    Godot Secure derive it at runtime from a security token): dump the running");
            Log.Warn("    process and scan the dump with --scan <dump.bin>");
        }

        Log.Warn("  - the pack is not encrypted with a key at all (check `ungodot info`)");
        Log.Warn("  - or pass a known key directly with --key <64-hex>");
    }
}