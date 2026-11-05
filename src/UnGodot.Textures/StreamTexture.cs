using System.Buffers.Binary;

namespace UnGodot.Textures;

/// <summary>Godot 3 image formats (the low 20 bits of a stream texture's data format).</summary>
public enum Godot3ImageFormat
{
    L8 = 0,
    LA8 = 1,
    R8 = 2,
    RG8 = 3,
    RGB8 = 4,
    RGBA8 = 5,
    RGBA4444 = 6,
    RGBA5551 = 7,
    RF = 8,
    RGF = 9,
    RGBF = 10,
    RGBAF = 11,
    RH = 12,
    RGH = 13,
    RGBH = 14,
    RGBAH = 15,
    RGBE9995 = 16,
    Dxt1 = 17,
    Dxt3 = 18,
    Dxt5 = 19,
    RgtcR = 20,
    RgtcRG = 21,
    BptcRgba = 22,
    BptcRgbF = 23,
    BptcRgbFu = 24,
    Pvrtc2 = 25,
    Pvrtc2A = 26,
    Pvrtc4 = 27,
    Pvrtc4A = 28,
    Etc = 29,
    Etc2R11 = 30,
    Etc2R11S = 31,
    Etc2Rg11 = 32,
    Etc2Rg11S = 33,
    Etc2Rgb8 = 34,
    Etc2Rgba8 = 35,
    Etc2Rgb8A1 = 36,
    Astc8x8 = 37,
}

public sealed class TextureException : Exception
{
    public TextureException(string message) : base(message)
    {
    }
}

/// <summary>An RGBA8 image produced by the decoders.</summary>
public sealed class DecodedImage
{
    public DecodedImage(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
}

/// <summary>
/// Decoder for Godot 3 <c>.stex</c> stream textures: header, embedded PNG, or a compressed
/// texture (S3TC/BCn, raw formats) with optional mipmaps.
///
/// Format reference: <c>StreamTexture::load</c> in Godot 3 and GDRE's
/// <c>texture_loader_compat.cpp</c>.
/// </summary>
public static class StreamTexture
{
    public const uint FormatBitPng = 1u << 20;
    public const uint FormatBitWebp = 1u << 21;
    public const uint FormatBitMipmaps = 1u << 23;
    public const uint FormatMaskImageFormat = (1u << 20) - 1;

    private static ReadOnlySpan<byte> Magic => "GDST"u8;

    public sealed class Header
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required uint Flags { get; init; }
        public required uint DataFormat { get; init; }
        public Godot3ImageFormat Format => (Godot3ImageFormat)(DataFormat & FormatMaskImageFormat);
        public bool EmbeddedPng => (DataFormat & FormatBitPng) != 0;
        public bool EmbeddedWebp => (DataFormat & FormatBitWebp) != 0;
        public bool Mipmaps => (DataFormat & FormatBitMipmaps) != 0;
        public int DataOffset => 20;
    }

    public static bool LooksLikeStreamTexture(ReadOnlySpan<byte> data) =>
        data.Length >= 20 && data[..4].SequenceEqual(Magic);

    public static Header ReadHeader(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeStreamTexture(data))
        {
            throw new TextureException("not a Godot 3 stream texture (missing GDST header)");
        }

        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[8..]);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint dataFormat = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);

        if (width <= 0 || height <= 0 || width > 32768 || height > 32768)
        {
            throw new TextureException($"implausible stream texture size {width}x{height}");
        }

        return new Header { Width = width, Height = height, Flags = flags, DataFormat = dataFormat };
    }

    /// <summary>
    /// Converts a <c>.stex</c> file to PNG bytes. Embedded PNGs are passed through untouched,
    /// everything else is decoded to RGBA8 and re-encoded.
    /// </summary>
    public static byte[] ToPng(ReadOnlySpan<byte> data)
    {
        Header header = ReadHeader(data);

        if (header.EmbeddedPng)
        {
            // [u32 mipmap count][u32 size][PNG bytes][mipmaps…]
            if (data.Length < header.DataOffset + 8)
            {
                throw new TextureException("truncated stream texture (missing PNG header)");
            }

            uint pngSize = BinaryPrimitives.ReadUInt32LittleEndian(data[(header.DataOffset + 4)..]);
            int start = header.DataOffset + 8;
            if (pngSize == 0 || start + pngSize > data.Length)
            {
                throw new TextureException("truncated embedded PNG");
            }

            return data.Slice(start, (int)pngSize).ToArray();
        }

        if (header.EmbeddedWebp)
        {
            throw new TextureException("WebP stream textures are not supported yet");
        }

        DecodedImage image = DecodeImage(data, header);
        return PngWriter.Encode(image.Width, image.Height, image.Rgba);
    }

    public static DecodedImage DecodeImage(ReadOnlySpan<byte> data, Header header)
    {
        Godot3ImageFormat format = header.Format;
        int width = header.Width;
        int height = header.Height;

        int dataSize = GetDataSize(width, height, format);
        ReadOnlySpan<byte> source = data[header.DataOffset..];
        if (source.Length < dataSize)
        {
            throw new TextureException(
                $"stream texture data is truncated ({source.Length} bytes, need {dataSize} for {width}x{height} {format})");
        }

        return ImageDecoder.Decode(source[..dataSize], width, height, format);
    }

    /// <summary>Size of the base mipmap level in bytes.</summary>
    public static int GetDataSize(int width, int height, Godot3ImageFormat format)
    {
        long pixels = (long)width * height;
        return format switch
        {
            Godot3ImageFormat.L8 => checked((int)pixels),
            Godot3ImageFormat.LA8 => checked((int)(pixels * 2)),
            Godot3ImageFormat.R8 => checked((int)pixels),
            Godot3ImageFormat.RG8 => checked((int)(pixels * 2)),
            Godot3ImageFormat.RGB8 => checked((int)(pixels * 3)),
            Godot3ImageFormat.RGBA8 => checked((int)(pixels * 4)),
            Godot3ImageFormat.RGBA4444 or Godot3ImageFormat.RGBA5551 => checked((int)(pixels * 2)),
            Godot3ImageFormat.RF => checked((int)(pixels * 4)),
            Godot3ImageFormat.RGF => checked((int)(pixels * 8)),
            Godot3ImageFormat.RGBF => checked((int)(pixels * 12)),
            Godot3ImageFormat.RGBAF => checked((int)(pixels * 16)),
            Godot3ImageFormat.RH => checked((int)(pixels * 2)),
            Godot3ImageFormat.RGH => checked((int)(pixels * 4)),
            Godot3ImageFormat.RGBH => checked((int)(pixels * 6)),
            Godot3ImageFormat.RGBAH => checked((int)(pixels * 8)),
            Godot3ImageFormat.RGBE9995 => checked((int)(pixels * 4)),
            Godot3ImageFormat.Dxt1 or Godot3ImageFormat.RgtcR => Blocks(width) * Blocks(height) * 8,
            Godot3ImageFormat.Dxt3 or Godot3ImageFormat.Dxt5 or Godot3ImageFormat.RgtcRG => Blocks(width) * Blocks(height) * 16,
            Godot3ImageFormat.BptcRgba or Godot3ImageFormat.BptcRgbF or Godot3ImageFormat.BptcRgbFu =>
                Blocks(width) * Blocks(height) * 16,
            Godot3ImageFormat.Pvrtc2 or Godot3ImageFormat.Pvrtc2A => PvrtcSize(width, height, 2),
            Godot3ImageFormat.Pvrtc4 or Godot3ImageFormat.Pvrtc4A => PvrtcSize(width, height, 4),
            Godot3ImageFormat.Etc or Godot3ImageFormat.Etc2Rgb8 => Blocks(width) * Blocks(height) * 8,
            Godot3ImageFormat.Etc2Rgba8 or Godot3ImageFormat.Etc2Rgb8A1 or Godot3ImageFormat.Astc8x8 =>
                Blocks(width) * Blocks(height) * 16,
            _ => throw new TextureException($"unsupported image format {format} ({(int)format})"),
        };
    }

    private static int Blocks(int size) => Math.Max(1, (size + 3) / 4);

    private static int PvrtcSize(int width, int height, int blockSize) =>
        Math.Max(2, width) * Math.Max(2, height) * blockSize / 8;
}