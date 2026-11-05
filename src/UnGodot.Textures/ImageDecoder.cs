using System.Buffers.Binary;

namespace UnGodot.Textures;

/// <summary>
/// Converts the image data of a Godot 3 texture into RGBA8. Implements the raw formats and
/// the S3TC/RGTC block formats (BC1, BC2, BC3, BC4, BC5); the remaining mobile formats
/// (ETC, PVRTC, ASTC, BPTC) fail with an explicit message instead of producing garbage.
/// </summary>
public static class ImageDecoder
{
    public static DecodedImage Decode(ReadOnlySpan<byte> data, int width, int height, Godot3ImageFormat format)
    {
        return format switch
        {
            Godot3ImageFormat.L8 => DecodeL8(data, width, height),
            Godot3ImageFormat.LA8 => DecodeLa8(data, width, height),
            Godot3ImageFormat.R8 => DecodeR8(data, width, height),
            Godot3ImageFormat.RG8 => DecodeRg8(data, width, height),
            Godot3ImageFormat.RGB8 => DecodeRgb8(data, width, height),
            Godot3ImageFormat.RGBA8 => DecodeRgba8(data, width, height),
            Godot3ImageFormat.RGBA4444 => DecodeRgba4444(data, width, height),
            Godot3ImageFormat.RGBA5551 => DecodeRgba5551(data, width, height),
            Godot3ImageFormat.RGBE9995 => DecodeRgbe9995(data, width, height),
            Godot3ImageFormat.RF => DecodeFloat(data, width, height, 1),
            Godot3ImageFormat.RGF => DecodeFloat(data, width, height, 2),
            Godot3ImageFormat.RGBF => DecodeFloat(data, width, height, 3),
            Godot3ImageFormat.RGBAF => DecodeFloat(data, width, height, 4),
            Godot3ImageFormat.RH => DecodeHalf(data, width, height, 1),
            Godot3ImageFormat.RGH => DecodeHalf(data, width, height, 2),
            Godot3ImageFormat.RGBH => DecodeHalf(data, width, height, 3),
            Godot3ImageFormat.RGBAH => DecodeHalf(data, width, height, 4),
            Godot3ImageFormat.Dxt1 => DecodeBc1(data, width, height),
            Godot3ImageFormat.Dxt3 => DecodeBc2(data, width, height),
            Godot3ImageFormat.Dxt5 => DecodeBc3(data, width, height),
            Godot3ImageFormat.RgtcR => DecodeBc4(data, width, height),
            Godot3ImageFormat.RgtcRG => DecodeBc5(data, width, height),
            _ => throw new TextureException(
                $"image format {format} is not supported yet ({width}x{height}); the texture is kept as is"),
        };
    }

    private static DecodedImage DecodeL8(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte l = data[i];
            int o = i * 4;
            rgba[o] = l;
            rgba[o + 1] = l;
            rgba[o + 2] = l;
            rgba[o + 3] = 255;
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeLa8(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            byte l = data[i * 2];
            int o = i * 4;
            rgba[o] = l;
            rgba[o + 1] = l;
            rgba[o + 2] = l;
            rgba[o + 3] = data[i * 2 + 1];
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeR8(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4] = data[i];
            rgba[i * 4 + 3] = 255;
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeRg8(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4] = data[i * 2];
            rgba[i * 4 + 1] = data[i * 2 + 1];
            rgba[i * 4 + 3] = 255;
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeRgb8(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4] = data[i * 3];
            rgba[i * 4 + 1] = data[i * 3 + 1];
            rgba[i * 4 + 2] = data[i * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeRgba8(ReadOnlySpan<byte> data, int width, int height)
    {
        return new DecodedImage(width, height, data[..(width * height * 4)].ToArray());
    }

    private static DecodedImage DecodeRgba4444(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            rgba[i * 4] = Expand4((value >> 12) & 0xF);
            rgba[i * 4 + 1] = Expand4((value >> 8) & 0xF);
            rgba[i * 4 + 2] = Expand4((value >> 4) & 0xF);
            rgba[i * 4 + 3] = Expand4(value & 0xF);
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeRgba5551(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * 2)..]);
            rgba[i * 4] = Expand5((value >> 11) & 0x1F);
            rgba[i * 4 + 1] = Expand5((value >> 6) & 0x1F);
            rgba[i * 4 + 2] = Expand5((value >> 1) & 0x1F);
            rgba[i * 4 + 3] = (value & 1) != 0 ? (byte)255 : (byte)0;
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeRgbe9995(ReadOnlySpan<byte> data, int width, int height)
    {
        // Shared-exponent RGB (9 bits per channel, 5 bit exponent): value = mantissa * 2^(E-24)
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            int exponent = (int)(value >> 27);
            double scale = exponent == 0 ? 0 : Math.ScaleB(1.0, exponent - 24);

            int o = i * 4;
            // The stored value is a linear float; 8-bit output clamps HDR to white.
            rgba[o] = ToByte((float)((value & 0x1FF) * scale));
            rgba[o + 1] = ToByte((float)(((value >> 9) & 0x1FF) * scale));
            rgba[o + 2] = ToByte((float)(((value >> 18) & 0x1FF) * scale));
            rgba[o + 3] = 255;
        }

        return new DecodedImage(width, height, rgba);
    }
    private static DecodedImage DecodeFloat(ReadOnlySpan<byte> data, int width, int height, int channels)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            for (int c = 0; c < channels; c++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(data[(i * channels * 4 + c * 4)..]);
                rgba[o + c] = ToByte(value);
            }

            if (channels == 4)
            {
                // Already written.
            }
            else
            {
                rgba[o + 3] = 255;
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeHalf(ReadOnlySpan<byte> data, int width, int height, int channels)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            int o = i * 4;
            for (int c = 0; c < channels; c++)
            {
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(data[(i * channels * 2 + c * 2)..]);
                rgba[o + c] = ToByte((float)BitConverter.UInt16BitsToHalf(bits));
            }

            if (channels != 4)
            {
                rgba[o + 3] = 255;
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static byte ToByte(float value)
    {
        if (float.IsNaN(value))
        {
            return 0;
        }

        return (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }

    private static byte Expand4(int value) => (byte)((value << 4) | value);

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    // -------------------------------------------------------------------------------------
    // S3TC / RGTC
    // -------------------------------------------------------------------------------------

    private static DecodedImage DecodeBc1(ReadOnlySpan<byte> data, int width, int height, bool forceFourColor = false)
    {
        var rgba = new byte[width * height * 4];
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * blocksWide + bx) * 8;
                DecodeBc1Block(data.Slice(offset, 8), rgba, width, height, bx * 4, by * 4, forceFourColor);
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static void DecodeBc1Block(
        ReadOnlySpan<byte> block,
        byte[] rgba,
        int width,
        int height,
        int x,
        int y,
        bool forceFourColor)
    {
        ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(block);
        ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(block[2..]);
        Span<byte> palette = stackalloc byte[16];
        bool transparent = false;

        DecodeRgb565(c0, palette[..4]);
        DecodeRgb565(c1, palette.Slice(4, 4));

        if (c0 > c1 || forceFourColor)
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((2 * palette[i] + palette[4 + i]) / 3);
                palette[12 + i] = (byte)((palette[i] + 2 * palette[4 + i]) / 3);
            }

            palette[11] = 255;
            palette[15] = 255;
        }
        else
        {
            for (int i = 0; i < 3; i++)
            {
                palette[8 + i] = (byte)((palette[i] + palette[4 + i]) / 2);
            }

            palette[11] = 255;
            // palette[12..16] stays zero: transparent black.
            transparent = true;
        }

        uint indices = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        for (int py = 0; py < 4; py++)
        {
            for (int px = 0; px < 4; px++)
            {
                int pixel = py * 4 + px;
                int index = (int)((indices >> (pixel * 2)) & 3);
                int targetX = x + px;
                int targetY = y + py;
                if (targetX >= width || targetY >= height)
                {
                    continue;
                }

                int o = (targetY * width + targetX) * 4;
                if (transparent && index == 3)
                {
                    rgba[o] = 0;
                    rgba[o + 1] = 0;
                    rgba[o + 2] = 0;
                    rgba[o + 3] = 0;
                    continue;
                }

                rgba[o] = palette[index * 4];
                rgba[o + 1] = palette[index * 4 + 1];
                rgba[o + 2] = palette[index * 4 + 2];
                rgba[o + 3] = palette[index * 4 + 3];
            }
        }
    }

    private static void DecodeRgb565(ushort value, Span<byte> target)
    {
        target[0] = Expand5((value >> 11) & 0x1F);
        target[1] = Expand6((value >> 5) & 0x3F);
        target[2] = Expand5(value & 0x1F);
        target[3] = 255;
    }

    private static byte Expand6(int value) => (byte)((value << 2) | (value >> 4));

    private static DecodedImage DecodeBc2(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * blocksWide + bx) * 16;
                ReadOnlySpan<byte> block = data.Slice(offset, 16);

                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int pixel = py * 4 + px;
                        int alphaNibble = (block[pixel / 2] >> ((pixel & 1) * 4)) & 0xF;
                        int targetX = bx * 4 + px;
                        int targetY = by * 4 + py;
                        if (targetX >= width || targetY >= height)
                        {
                            continue;
                        }

                        int o = (targetY * width + targetX) * 4;
                        rgba[o + 3] = Expand4(alphaNibble);
                    }
                }

                DecodeBc1Block(block[8..], rgba, width, height, bx * 4, by * 4, forceFourColor: true);
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeBc3(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * blocksWide + bx) * 16;
                ReadOnlySpan<byte> block = data.Slice(offset, 16);
                DecodeAlphaBlock(block[..8], rgba, width, height, bx * 4, by * 4, alphaChannel: 3);
                DecodeBc1Block(block[8..], rgba, width, height, bx * 4, by * 4, forceFourColor: true);
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeBc4(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 3] = 255;
        }

        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * blocksWide + bx) * 8;
                DecodeAlphaBlock(data.Slice(offset, 8), rgba, width, height, bx * 4, by * 4, -1);
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    private static DecodedImage DecodeBc5(ReadOnlySpan<byte> data, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 2] = 0;
            rgba[i * 4 + 3] = 255;
        }

        int blocksWide = Math.Max(1, (width + 3) / 4);
        int blocksHigh = Math.Max(1, (height + 3) / 4);

        for (int by = 0; by < blocksHigh; by++)
        {
            for (int bx = 0; bx < blocksWide; bx++)
            {
                int offset = (by * blocksWide + bx) * 16;
                DecodeAlphaBlock(data.Slice(offset, 8), rgba, width, height, bx * 4, by * 4, 0);
                DecodeAlphaBlock(data.Slice(offset + 8, 8), rgba, width, height, bx * 4, by * 4, 1);
            }
        }

        return new DecodedImage(width, height, rgba);
    }

    /// <summary>Decodes a BC3/BC4 alpha block into one channel (-1 writes RGB as grey).</summary>
    private static void DecodeAlphaBlock(
        ReadOnlySpan<byte> block,
        byte[] rgba,
        int width,
        int height,
        int x,
        int y,
        int alphaChannel)
    {
        Span<byte> palette = stackalloc byte[8];
        byte a0 = block[0];
        byte a1 = block[1];
        palette[0] = a0;
        palette[1] = a1;

        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++)
            {
                palette[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
        }
        else
        {
            for (int i = 1; i <= 4; i++)
            {
                palette[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
            }

            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
        {
            bits |= (ulong)block[2 + i] << (i * 8);
        }

        for (int py = 0; py < 4; py++)
        {
            for (int px = 0; px < 4; px++)
            {
                int pixel = py * 4 + px;
                int index = (int)((bits >> (pixel * 3)) & 7);
                byte value = palette[index];
                int targetX = x + px;
                int targetY = y + py;
                if (targetX >= width || targetY >= height)
                {
                    continue;
                }

                int o = (targetY * width + targetX) * 4;
                if (alphaChannel < 0)
                {
                    rgba[o] = value;
                    rgba[o + 1] = value;
                    rgba[o + 2] = value;
                }
                else
                {
                    rgba[o + alphaChannel] = value;
                }
            }
        }
    }
}