using System.Buffers.Binary;
using UnGodot.Recovery;
using UnGodot.Recovery.Stages;
using UnGodot.Textures;
using Xunit;

namespace UnGodot.Tests;

public class TextureTests
{
    private static byte[] BuildStreamTexture(int width, int height, Godot3ImageFormat format, byte[] data, bool png = false, bool mipmaps = false)
    {
        var bytes = new List<byte>();
        bytes.AddRange("GDST"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes((ushort)width));
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes((ushort)height));
        bytes.AddRange(BitConverter.GetBytes((ushort)0));
        bytes.AddRange(BitConverter.GetBytes(0x1Fu));
        uint dataFormat = (uint)format;
        if (png)
        {
            dataFormat |= StreamTexture.FormatBitPng;
        }

        if (mipmaps)
        {
            dataFormat |= StreamTexture.FormatBitMipmaps;
        }

        bytes.AddRange(BitConverter.GetBytes(dataFormat));
        bytes.AddRange(data);
        return bytes.ToArray();
    }

    private static byte[] Bc1SolidBlock(ushort color0, ushort color1, uint indices)
    {
        var block = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(block, color0);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(2), color1);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), indices);
        return block;
    }

    [Fact]
    public void DecodesBc1SolidColour()
    {
        // 565 red, all indices 0 -> every pixel red.
        byte[] block = Bc1SolidBlock(0xF800, 0x001F, 0);
        DecodedImage image = ImageDecoder.Decode(block, 4, 4, Godot3ImageFormat.Dxt1);

        Assert.Equal(4, image.Width);
        Assert.Equal(4, image.Height);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, image.Rgba[i * 4]);
            Assert.Equal(0, image.Rgba[i * 4 + 1]);
            Assert.Equal(0, image.Rgba[i * 4 + 2]);
            Assert.Equal(255, image.Rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void DecodesBc3Alpha()
    {
        // Alpha: a0=255, a1=0, all indices 0 -> fully opaque; colour block red.
        var block = new byte[16];
        block[0] = 255;
        block[1] = 0;
        Bc1SolidBlock(0xF800, 0x001F, 0).CopyTo(block, 8);

        DecodedImage image = ImageDecoder.Decode(block, 4, 4, Godot3ImageFormat.Dxt5);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(255, image.Rgba[i * 4]);
            Assert.Equal(255, image.Rgba[i * 4 + 3]);
        }
    }

    [Fact]
    public void DecodesRgba8AndWritesPng()
    {
        var pixels = new byte[2 * 2 * 4];
        for (int i = 0; i < 4; i++)
        {
            pixels[i * 4] = (byte)(10 * i);
            pixels[i * 4 + 3] = 255;
        }

        byte[] stex = BuildStreamTexture(2, 2, Godot3ImageFormat.RGBA8, pixels);
        byte[] png = StreamTexture.ToPng(stex);

        Assert.Equal<byte[]>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(6, png[25]); // RGBA colour type
    }

    [Fact]
    public void PassesEmbeddedPngThrough()
    {
        byte[] innerPng = PngWriter.Encode(1, 1, new byte[] { 1, 2, 3, 255 });
        var payload = new List<byte>();
        payload.AddRange(BitConverter.GetBytes(1u)); // mipmap count
        payload.AddRange(BitConverter.GetBytes((uint)innerPng.Length));
        payload.AddRange(innerPng);

        byte[] stex = BuildStreamTexture(1, 1, Godot3ImageFormat.RGBA8, payload.ToArray(), png: true);
        Assert.Equal(innerPng, StreamTexture.ToPng(stex));
    }

    [Fact]
    public void RejectsUnsupportedFormatsWithAClearMessage()
    {
        byte[] stex = BuildStreamTexture(4, 4, Godot3ImageFormat.Astc8x8, new byte[16]);
        TextureException error = Assert.Throws<TextureException>(() => StreamTexture.ToPng(stex));
        Assert.Contains("Astc8x8", error.Message);
    }

    [Fact]
    public void DecodesRgbe9995()
    {
        // value = mantissa * 2^(exponent - 24): exponent 15 with mantissa 256 is 0.5,
        // exponent 24 with a large mantissa is HDR and clamps to white.
        uint half = (15u << 27) | (256u << 18) | (256u << 9) | 256u;
        uint over = (24u << 27) | (511u << 18) | (511u << 9) | 511u;

        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, half);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), over);

        DecodedImage image = ImageDecoder.Decode(data, 2, 1, Godot3ImageFormat.RGBE9995);
        Assert.Equal(128, image.Rgba[0]);
        Assert.Equal(128, image.Rgba[1]);
        Assert.Equal(128, image.Rgba[2]);
        Assert.Equal(255, image.Rgba[3]);
        Assert.Equal(255, image.Rgba[4]); // HDR values clamp to white
    }
    [Theory]
    [InlineData("02__foo.tga-b7bf2bd743f1e9cd07fa1b4c0ada05bf.s3tc", "02__foo.tga")]
    [InlineData("icon.png-0123456789abcdef0123456789abcdef", "icon.png")]
    [InlineData("icon.png-0123456789abcdef0123456789abcdef.astc-low", "icon.png")]
    public void ReconstructsTheSourceName(string input, string expected)
    {
        Assert.Equal(expected, TextureStage.SourceKey(input));
    }

    [Fact]
    public async Task TextureStageConvertsTheBestVariant()
    {
        string root = Path.Combine(Path.GetTempPath(), "ungodot-tests", Guid.NewGuid().ToString("N"));
        string importDir = Path.Combine(root, ".import");
        Directory.CreateDirectory(importDir);

        try
        {
            string hash = new string('a', 32);
            byte[] bc1 = Bc1SolidBlock(0xF800, 0x001F, 0);
            File.WriteAllBytes(Path.Combine(importDir, $"hero.png-{hash}.s3tc.stex"),
                BuildStreamTexture(4, 4, Godot3ImageFormat.Dxt1, bc1));
            // A mobile variant that cannot be decoded must not win over the S3TC one.
            File.WriteAllBytes(Path.Combine(importDir, $"hero.png-{hash}.astc.stex"),
                BuildStreamTexture(4, 4, Godot3ImageFormat.Astc8x8, new byte[16]));

            var context = new RecoveryContext
            {
                Root = root,
                Options = new RecoveryOptions(),
                Report = new RecoveryReport { Root = root },
            };

            RecoveryStageResult result = new() { Stage = "textures" };
            await new TextureStage().RunAsync(context, result, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(root, "hero.png")));
            Assert.Equal(1, result.Counters.TryGetValue("converted", out int converted) ? converted : 0);
            Assert.True(new FileInfo(Path.Combine(root, "hero.png")).Length > 60);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}