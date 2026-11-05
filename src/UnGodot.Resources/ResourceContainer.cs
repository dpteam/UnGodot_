using System.Buffers.Binary;
using System.IO.Compression;
using ZstdSharp;

namespace UnGodot.Resources;

public sealed class ResourceFormatException : Exception
{
    public ResourceFormatException(string message) : base(message)
    {
    }
}

/// <summary>
/// Unwraps Godot's binary resource containers.
///
/// <c>RSRC</c> files are the raw resource stream. <c>RSCC</c> files are the same stream inside
/// Godot's <c>FileAccessCompressed</c> container: magic, compression mode, block size, total
/// size, a table of block sizes, then the concatenated blocks, and the magic again at the end
/// (see <c>core/io/file_access_compressed.cpp</c>). Blocks are compressed with Zstandard,
/// Deflate or the legacy FastLZ.
/// </summary>
public static class ResourceContainer
{
    private const uint RsrcMagic = 0x43525352; // "RSRC"
    private const uint RsccMagic = 0x43435352; // "RSCC"
    private const int MaxBlockSize = 1 << 24;
    private const int MaxTotalSize = 1 << 30;

    public static bool LooksLikeResource(ReadOnlySpan<byte> data) =>
        data.Length >= 4 &&
        (BinaryPrimitives.ReadUInt32LittleEndian(data) == RsrcMagic ||
         BinaryPrimitives.ReadUInt32LittleEndian(data) == RsccMagic);

    /// <summary>Returns the <c>RSRC</c> body (starting at big_endian), decompressing RSCC.</summary>
    public static byte[] ReadBody(ReadOnlySpan<byte> data) => ReadBody(data, out _);

    /// <summary>
    /// Returns the <c>RSRC</c> body (starting at big_endian), decompressing RSCC.
    /// <paramref name="offsetBase"/> is the value that object offsets in the header are
    /// relative to inside the original file: uncompressed <c>RSRC</c> offsets count from the
    /// file start (so they include the magic, base 4), while <c>RSCC</c> offsets count from
    /// the decompressed stream, which does not contain the magic (base 0).
    /// </summary>
    public static byte[] ReadBody(ReadOnlySpan<byte> data, out int offsetBase)
    {
        if (data.Length < 4)
        {
            throw new ResourceFormatException("the resource file is truncated");
        }

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic == RsrcMagic)
        {
            offsetBase = 4;
            return data[4..].ToArray();
        }

        offsetBase = 0;

        if (magic != RsccMagic)
        {
            throw new ResourceFormatException("not a Godot binary resource (missing RSRC/RSCC magic)");
        }

        if (data.Length < 20)
        {
            throw new ResourceFormatException("the RSCC header is truncated");
        }

        uint mode = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        int blockSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        int totalSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (blockSize <= 0 || blockSize > MaxBlockSize || totalSize < 0 || totalSize > MaxTotalSize)
        {
            throw new ResourceFormatException("implausible RSCC block header");
        }

        // Godot always writes (total / blockSize) + 1 entries: the final block holds the
        // remainder and is empty when the total is an exact multiple of the block size.
        int blockCount = totalSize / blockSize + 1;
        long tableEnd = 16L + (long)blockCount * 4;
        if (tableEnd > data.Length)
        {
            throw new ResourceFormatException("the RSCC block table is truncated");
        }

        var sizes = new int[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            sizes[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(16 + i * 4)..]);
        }

        var body = new byte[totalSize];
        int written = 0;
        int position = (int)tableEnd;
        using var zstd = new Decompressor();

        for (int i = 0; i < blockCount; i++)
        {
            int compressedSize = sizes[i];
            if (compressedSize < 0 || position + compressedSize > data.Length)
            {
                throw new ResourceFormatException($"RSCC block {i} is truncated");
            }

            ReadOnlySpan<byte> block = data.Slice(position, compressedSize);
            position += compressedSize;

            int expected = blockCount == 1
                ? totalSize
                : (i == blockCount - 1 ? totalSize % blockSize : blockSize);
            if (expected == 0)
            {
                continue;
            }

            switch (mode)
            {
                case 0:
                    throw new ResourceFormatException("legacy FastLZ-compressed resources are not supported yet");

                case 1:
                {
                    using var input = new MemoryStream(block.ToArray(), writable: false);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    int read = ReadAtMost(zlib, body.AsSpan(written, expected));
                    if (read != expected)
                    {
                        throw new ResourceFormatException(
                            $"RSCC block {i} decompressed to {read} bytes, expected {expected}");
                    }

                    written += read;
                    break;
                }

                case 2:
                {
                    // Some Sonic frames have no content size in the header, so stream the
                    // frame instead of relying on its metadata.
                    using var input = new MemoryStream(block.ToArray(), writable: false);
                    using var zstdStream = new DecompressionStream(
                        input,
                        zstd,
                        bufferSize: 64 * 1024,
                        checkEndOfStream: false,
                        preserveDecompressor: true);
                    int read = ReadAtMost(zstdStream, body.AsSpan(written, expected));
                    if (read != expected)
                    {
                        throw new ResourceFormatException(
                            $"RSCC block {i} decompressed to {read} bytes, expected {expected}");
                    }

                    written += read;
                    break;
                }

                default:
                    throw new ResourceFormatException($"unknown RSCC compression mode {mode}");
            }
        }

        if (written != totalSize)
        {
            throw new ResourceFormatException($"RSCC decompressed to {written} bytes, expected {totalSize}");
        }

        return body;
    }

    private static int ReadAtMost(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}