using System.Diagnostics;

namespace UnGodot.Dump;

/// <summary>The byte signatures the carver looks for.</summary>
public static class MemorySignatures
{
    public static readonly byte[] Pack = "GDPC"u8.ToArray();
    public static readonly byte[] Resource = "RSRC"u8.ToArray();
    public static readonly byte[] CompressedResource = "RSCC"u8.ToArray();
    public static readonly byte[] Script = "GDSC"u8.ToArray();
    public static readonly byte[] StreamTexture = "GDST"u8.ToArray();
    public static readonly byte[] ProjectSettings = "ECFG"u8.ToArray();

    public static readonly IReadOnlyList<byte[]> All =
    [
        Pack, Resource, CompressedResource, Script, StreamTexture, ProjectSettings,
    ];

    public static string NameOf(ReadOnlySpan<byte> magic) => magic switch
    {
        _ when magic.SequenceEqual(Pack) => "GDPC",
        _ when magic.SequenceEqual(Resource) => "RSRC",
        _ when magic.SequenceEqual(CompressedResource) => "RSCC",
        _ when magic.SequenceEqual(Script) => "GDSC",
        _ when magic.SequenceEqual(StreamTexture) => "GDST",
        _ when magic.SequenceEqual(ProjectSettings) => "ECFG",
        _ => "?",
    };
}

/// <summary>
/// Finds signatures across a memory source. Reads fixed chunks with a small overlap so a
/// signature straddling a chunk boundary is not missed.
/// </summary>
public static class MemoryScanner
{
    public static long Scan(
        IMemorySource source,
        IReadOnlyList<byte[]> signatures,
        Action<string, long> onHit,
        int chunkSize = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        if (signatures.Count == 0)
        {
            return 0;
        }

        int maxMagic = signatures.Max(s => s.Length);
        var buffer = new byte[chunkSize + maxMagic];
        long scanned = 0;
        var watch = Stopwatch.StartNew();

        foreach ((long start, long size) in source.Segments)
        {
            long offset = 0;
            while (offset < size)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int want = (int)Math.Min((long)buffer.Length, size - offset);
                int read = source.Read(start + offset, buffer.AsSpan(0, want));
                if (read <= 0)
                {
                    break;
                }

                int core = Math.Min(chunkSize, read);
                ReadOnlySpan<byte> span = buffer.AsSpan(0, core);
                foreach (byte[] magic in signatures)
                {
                    int from = 0;
                    while (from <= core - magic.Length)
                    {
                        int at = span[from..].IndexOf(magic);
                        if (at < 0)
                        {
                            break;
                        }

                        onHit(MemorySignatures.NameOf(magic), start + offset + from + at);
                        from += at + 1;
                    }
                }

                scanned += core;
                offset += core;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(30))
            {
                watch.Restart();
            }
        }

        return scanned;
    }
}