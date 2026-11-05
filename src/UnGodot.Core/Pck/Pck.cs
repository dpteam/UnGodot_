using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using UnGodot.Core.Crypto;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;

namespace UnGodot.Core.Pck;

/// <summary>Header flags and entry flags of Godot's pack format.</summary>
public static class PckFlags
{
    public const uint PackDirEncrypted = 1 << 0;
    public const uint PackRelFileBase = 1 << 1;
    public const uint PackSparseBundle = 1 << 2;

    public const uint FileEncrypted = 1 << 0;
    public const uint FileRemoval = 1 << 1;
    public const uint FileDelta = 1 << 2;
}

/// <summary>Parsed pack header (the <c>GDPC</c> block).</summary>
public sealed class PckHeader
{
    /// <summary>"GDPC" as a little endian u32.</summary>
    public const uint Magic = 0x4350_4447;

    public const uint FormatV0 = 0;
    public const uint FormatV1 = 1;
    public const uint FormatV2 = 2;
    public const uint FormatV3 = 3;
    public const uint FormatV4 = 4;

    public long Start { get; init; }
    public uint FormatVersion { get; init; }
    public uint EngineMajor { get; init; }
    public uint EngineMinor { get; init; }
    public uint EnginePatch { get; init; }
    public uint Flags { get; init; }

    /// <summary>Base offset of file data, already absolute inside the container.</summary>
    public long FileBase { get; init; }

    /// <summary>Absolute offset of the file table (the u32 file count lives here).</summary>
    public long DirOffset { get; init; }

    public bool DirEncrypted => (Flags & PckFlags.PackDirEncrypted) != 0;
    public bool RelFileBase => (Flags & PckFlags.PackRelFileBase) != 0;
    public bool SparseBundle => (Flags & PckFlags.PackSparseBundle) != 0;

    public bool IsLegacy => FormatVersion <= FormatV1;

    public string EngineVersion => FormatVersion == FormatV0
        ? "2.x (legacy)"
        : $"{EngineMajor}.{EngineMinor}.{EnginePatch}";

    /// <summary>Absolute offset of the byte a file entry's <c>offset</c> is relative to.</summary>
    public long DataBase => FormatVersion <= FormatV1 ? Start : FileBase;

    public static bool TryParse(IRandomAccessSource source, long start, bool checkMagic, out PckHeader? header, out string? error)
    {
        header = null;
        error = null;

        if (start < 0 || start + 40 > source.Length)
        {
            error = $"pack header truncated at offset {start}";
            return false;
        }

        Span<byte> raw = stackalloc byte[40];
        if (source.ReadAt(start, raw) != 40)
        {
            error = $"pack header truncated at offset {start}";
            return false;
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        if (checkMagic && magic != Magic)
        {
            error = $"bad pack magic 0x{magic:x8} at offset {start}";
            return false;
        }

        uint format = BinaryPrimitives.ReadUInt32LittleEndian(raw[4..]);
        uint major = BinaryPrimitives.ReadUInt32LittleEndian(raw[8..]);
        uint minor = BinaryPrimitives.ReadUInt32LittleEndian(raw[12..]);
        uint patch = BinaryPrimitives.ReadUInt32LittleEndian(raw[16..]);
        long fileLength = source.Length;

        if (format <= FormatV1)
        {
            long dirOffset = start + 20 + 64;
            if (dirOffset + 4 > fileLength)
            {
                error = $"directory offset {dirOffset} is outside the file (size {fileLength})";
                return false;
            }

            header = new PckHeader
            {
                Start = start,
                FormatVersion = format,
                EngineMajor = major,
                EngineMinor = minor,
                EnginePatch = patch,
                Flags = 0,
                FileBase = 0,
                DirOffset = dirOffset,
            };
            return true;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[20..]);
        ulong rawFileBase = BinaryPrimitives.ReadUInt64LittleEndian(raw[24..]);

        long fileBase = (long)rawFileBase;
        if (format >= FormatV3 || (format == FormatV2 && (flags & PckFlags.PackRelFileBase) != 0))
        {
            fileBase += start;
        }
        else if (format == FormatV2 && fileBase == 0)
        {
            // Some third party writers leave the file base empty in v2 and store absolute
            // offsets; use the pack start so entries still resolve.
            fileBase = start;
        }

        long dir;
        if (format >= FormatV3)
        {
            ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(raw[32..]);
            dir = (long)stored + start;
        }
        else
        {
            // v2: the directory directly follows the header plus 64 reserved bytes.
            dir = start + 32 + 64;
        }

        if (dir + 4 > fileLength)
        {
            error = $"directory offset {dir} is outside the file (size {fileLength})";
            return false;
        }

        if (fileBase > fileLength)
        {
            error = $"file base {fileBase} is outside the file (size {fileLength})";
            return false;
        }

        header = new PckHeader
        {
            Start = start,
            FormatVersion = format,
            EngineMajor = major,
            EngineMinor = minor,
            EnginePatch = patch,
            Flags = flags,
            FileBase = fileBase,
            DirOffset = dir,
        };
        return true;
    }

    /// <summary>Structural plausibility used while scanning for embedded packs.</summary>
    public bool LooksValid(long fileLength)
    {
        if (FormatVersion > FormatV4)
        {
            return false;
        }

        if (FormatVersion == FormatV0)
        {
            return DirOffset + 4 <= fileLength;
        }

        if (EngineMajor == 0 || EngineMajor > 6)
        {
            return false;
        }

        return DirOffset + 4 <= fileLength && FileBase <= fileLength;
    }
}

/// <summary>One entry of the pack's file table.</summary>
public sealed class PckEntry
{
    public required string Path { get; init; }

    /// <summary>Offset relative to <see cref="PckHeader.DataBase"/>.</summary>
    public long Offset { get; init; }

    public long Size { get; init; }
    public byte[] Md5 { get; init; } = new byte[16];
    public uint Flags { get; init; }

    public bool Encrypted => (Flags & PckFlags.FileEncrypted) != 0;
    public bool Removed => (Flags & PckFlags.FileRemoval) != 0;
    public bool Delta => (Flags & PckFlags.FileDelta) != 0;
    public bool HasMd5 => Md5.AsSpan().IndexOfAnyExcept((byte)0) >= 0;

    public override string ToString() => $"{Path} ({Size} bytes)";
}

public sealed class PckException : Exception
{
    public PckException(string message) : base(message)
    {
    }
}

/// <summary>A parsed pack: header plus its (decrypted) file table.</summary>
public sealed class Pck
{
    private Pck(PckHeader header, List<PckEntry> entries)
    {
        Header = header;
        Entries = entries;
    }

    public PckHeader Header { get; }

    public List<PckEntry> Entries { get; }

    public long DataOffset(PckEntry entry) => Header.DataBase + entry.Offset;

    /// <summary>Reads the file table. A key is required when the directory is encrypted.</summary>
    public static Pck Open(IRandomAccessSource source, PckHeader header, byte[]? key = null)
    {
        if (header.SparseBundle)
        {
            throw new PckException(
                "this pack uses the sparse bundle format (Android/iOS), which is not supported yet: recover it with GDRE Tools directly");
        }

        long dir = header.DirOffset;
        if (dir + 4 > source.Length)
        {
            throw new PckException("the directory offset is out of range");
        }

        Span<byte> countRaw = stackalloc byte[4];
        source.ReadExactly(dir, countRaw);
        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(countRaw);

        if (fileCount > 1_000_000)
        {
            throw new PckException($"implausible file count {fileCount} in the pack directory");
        }

        if (header.DirEncrypted)
        {
            if (key is null)
            {
                throw new PckException("the pack directory is encrypted but no key was supplied");
            }

            EncryptedBlob blob = EncryptedBlob.Read(source, dir + 4, out string? blobError)
                ?? throw new PckException($"cannot parse the encrypted directory header: {blobError}");
            byte[]? plain = blob.TryDecrypt(key)
                ?? throw new PckException("cannot decrypt the directory: wrong key or corrupted pack (MD5 mismatch)");
            using var decrypted = new MemorySource(plain, "<decrypted directory>");
            using var tableReader = new SourceReader(decrypted, 0, 256 * 1024);
            return ParseTable(tableReader, fileCount, header, source.Length, plain.Length);
        }

        using var reader = new SourceReader(source, dir + 4, 256 * 1024);
        return ParseTable(reader, fileCount, header, source.Length, source.Length - (dir + 4));
    }

    private static Pck ParseTable(
        SourceReader reader,
        uint fileCount,
        PckHeader header,
        long containerLength,
        long tableLength)
    {
        bool legacy = header.IsLegacy;
        var entries = new List<PckEntry>((int)Math.Min(fileCount, 65536));

        for (uint i = 0; i < fileCount; i++)
        {
            string where = $"entry {i}";
            if (!reader.TryReadUInt32(out uint pathLength))
            {
                throw new PckException($"the file table is truncated after {entries.Count} entries");
            }

            if (pathLength == 0 || pathLength > 4 * 1024 * 1024)
            {
                throw new PckException($"implausible path length {pathLength} at {where}");
            }

            var pathBytes = new byte[pathLength];
            if (!reader.TryReadBytes(pathBytes))
            {
                throw new PckException($"the path of {where} is truncated");
            }

            int end = pathBytes.Length;
            while (end > 0 && pathBytes[end - 1] == 0)
            {
                end--;
            }

            string path = Encoding.UTF8.GetString(pathBytes, 0, end);

            if (!reader.TryReadInt64(out long offset) ||
                !reader.TryReadInt64(out long size))
            {
                throw new PckException($"the metadata of {where} is truncated");
            }

            var md5 = new byte[16];
            if (!reader.TryReadBytes(md5))
            {
                throw new PckException($"the MD5 of {where} is truncated");
            }

            uint flags = 0;
            if (!legacy && !reader.TryReadUInt32(out flags))
            {
                throw new PckException($"the flags of {where} are truncated");
            }

            entries.Add(new PckEntry
            {
                Path = path,
                Offset = offset,
                Size = size,
                Md5 = md5,
                Flags = flags,
            });
        }

        // Sanity: entries must point inside the container. A wrong key can still produce a
        // checksum-valid table in theory, but offsets are a second cheap guard.
        long tableEnd = header.DirOffset + 4 + tableLength;
        foreach (PckEntry entry in entries)
        {
            if (entry.Removed || entry.Size < 0)
            {
                continue;
            }

            long dataOffset = header.DataBase + entry.Offset;
            long storedSize = entry.Encrypted
                ? EncryptedBlob.HeaderSize + EncryptedBlob.Pad16(entry.Size)
                : entry.Size;

            bool inside = dataOffset >= 0 && dataOffset <= containerLength;
            bool fits = storedSize >= 0 && dataOffset + storedSize <= Math.Max(containerLength, tableEnd);
            if (!inside || !fits)
            {
                Log.Debug($"pack entry '{entry.Path}' points outside the container (offset {dataOffset}, size {storedSize}, container {containerLength})");
            }
        }

        return new Pck(header, entries);
    }
}

/// <summary>Finds the offset of a pack inside a container (standalone, appended, embedded, renamed magic).</summary>
public static class PckLocator
{
    public static bool TryLocate(IRandomAccessSource source, [NotNullWhen(true)] out PckHeader? header, out string? error)
    {
        header = null;
        error = null;
        long length = source.Length;
        if (length < 40)
        {
            error = "the file is too small to contain a Godot pack";
            return false;
        }

        // 1) Standalone pack.
        Span<byte> magicRaw = stackalloc byte[4];
        if (source.ReadAt(0, magicRaw) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(magicRaw) == PckHeader.Magic)
        {
            if (PckHeader.TryParse(source, 0, checkMagic: true, out PckHeader? candidate, out _) && Accept(source, candidate))
            {
                header = candidate!;
                return true;
            }
        }

        // 2) Self-contained executable: the pack is appended and its header is referenced
        //    from a small pointer at the very end of the file.
        Span<byte> tail = stackalloc byte[16];
        if (length > 32 && source.ReadAt(length - 16, tail) == 16)
        {
            uint tailMagic = BinaryPrimitives.ReadUInt32LittleEndian(tail[12..]);
            ulong ds = BinaryPrimitives.ReadUInt64LittleEndian(tail);
            if (tailMagic == PckHeader.Magic && length >= 16 + (long)ds + 8)
            {
                long head = length - 16 - (long)ds - 8;
                if (PckHeader.TryParse(source, head, checkMagic: true, out PckHeader? candidate, out _) && Accept(source, candidate))
                {
                    header = candidate!;
                    return true;
                }
            }
        }

        // 3) Generic scan for the magic (embedded packs, patched binaries).
        if (TryScanForMagic(source, out long found) &&
            PckHeader.TryParse(source, found, checkMagic: true, out PckHeader? scanned, out _) &&
            Accept(source, scanned))
        {
            header = scanned!;
            return true;
        }

        // 4) Magic-agnostic fallback: a renamed GDPC signature, detected by structure.
        if (TryLocateRenamedMagic(source, out PckHeader? renamed))
        {
            header = renamed!;
            return true;
        }

        error = "no Godot pack (GDPC) found in this file";
        return false;
    }

    private static bool TryScanForMagic(IRandomAccessSource source, out long offset)
    {
        offset = -1;
        ReadOnlySpan<byte> needle = "GDPC"u8;
        const int Chunk = 4 * 1024 * 1024;
        const int Overlap = 3;
        byte[] buffer = new byte[Chunk + Overlap];
        long position = 0;

        while (position < source.Length)
        {
            int want = (int)Math.Min(buffer.Length, source.Length - position);
            int read = source.ReadAt(position, buffer.AsSpan(0, want));
            if (read <= 0)
            {
                break;
            }

            ReadOnlySpan<byte> span = buffer.AsSpan(0, read);
            int start = 0;
            while (true)
            {
                int index = span[start..].IndexOf(needle);
                if (index < 0)
                {
                    break;
                }

                int absolute = start + index;
                long candidateOffset = position + absolute;
                if (PckHeader.TryParse(source, candidateOffset, checkMagic: true, out PckHeader? candidate, out _) &&
                    Accept(source, candidate))
                {
                    offset = candidateOffset;
                    return true;
                }

                start = absolute + 1;
            }

            if (read < buffer.Length)
            {
                break;
            }

            position += read - Overlap;
        }

        return false;
    }

    private static bool TryLocateRenamedMagic(IRandomAccessSource source, [NotNullWhen(true)] out PckHeader? header)
    {
        header = null;
        long length = source.Length;

        static bool Strong(IRandomAccessSource source, PckHeader? candidate) =>
            candidate is not null &&
            candidate.FormatVersion is >= PckHeader.FormatV2 and <= PckHeader.FormatV4 &&
            candidate.EngineMajor is >= 1 and <= 6 &&
            Accept(source, candidate);

        if (length > 32)
        {
            Span<byte> tail = stackalloc byte[16];
            if (source.ReadAt(length - 16, tail) == 16)
            {
                ulong ds = BinaryPrimitives.ReadUInt64LittleEndian(tail);
                ReadOnlySpan<byte> magic = tail[12..];
                if (length >= 16 + (long)ds + 8)
                {
                    long head = length - 16 - (long)ds - 8;
                    Span<byte> headMagic = stackalloc byte[4];
                    if (source.ReadAt(head, headMagic) == 4 && headMagic.SequenceEqual(magic) &&
                        PckHeader.TryParse(source, head, checkMagic: false, out PckHeader? candidate, out _) &&
                        Strong(source, candidate))
                    {
                        header = candidate!;
                        return true;
                    }
                }
            }
        }

        if (PckHeader.TryParse(source, 0, checkMagic: false, out PckHeader? relaxed, out _) && Strong(source, relaxed))
        {
            header = relaxed!;
            return true;
        }

        return false;
    }

    /// <summary>Plausible header and a file table that begins with a sane file count.</summary>
    private static bool Accept(IRandomAccessSource source, PckHeader? header)
    {
        if (header is null || !header.LooksValid(source.Length))
        {
            return false;
        }

        long dir = header.DirOffset;
        if (dir + 4 > source.Length)
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[4];
        if (source.ReadAt(dir, raw) != 4)
        {
            return false;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(raw);
        // A v2+ entry is at least 36 bytes, a legacy one 32.
        return count > 0 && count < 1_000_000 && dir + 4 + (long)count * 32 <= source.Length;
    }
}