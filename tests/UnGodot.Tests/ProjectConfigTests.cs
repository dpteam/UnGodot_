using System.Text;
using UnGodot.Recovery.Project;
using Xunit;

namespace UnGodot.Tests;

public class ProjectConfigTests
{
    internal static byte[] EncodeString(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        int padded = (utf8.Length + 3) & ~3;
        byte[] bytes = new byte[8 + padded];
        BitConverter.GetBytes(4u).CopyTo(bytes, 0);
        BitConverter.GetBytes(utf8.Length).CopyTo(bytes, 4);
        utf8.CopyTo(bytes, 8);
        return bytes;
    }

    private static byte[] EncodeInt(int value)
    {
        byte[] bytes = new byte[8];
        BitConverter.GetBytes(2u).CopyTo(bytes, 0);
        BitConverter.GetBytes(value).CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] EncodeBool(bool value)
    {
        byte[] bytes = new byte[8];
        BitConverter.GetBytes(1u).CopyTo(bytes, 0);
        BitConverter.GetBytes(value ? 1 : 0).CopyTo(bytes, 4);
        return bytes;
    }

    private static byte[] EncodePoolStringArray(params string[] values)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(23u));
        bytes.AddRange(BitConverter.GetBytes((uint)values.Length));
        foreach (string value in values)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(value);
            // Godot writes packed string array entries with the NUL terminator included.
            int length = utf8.Length + 1;
            bytes.AddRange(BitConverter.GetBytes((uint)length));
            bytes.AddRange(utf8);
            bytes.Add(0);
            int padding = (4 - (length % 4)) % 4;
            bytes.AddRange(new byte[padding]);
        }

        return bytes.ToArray();
    }

    internal static byte[] BuildEcFg(params (string Key, byte[] Value)[] settings)
    {
        var bytes = new List<byte>();
        bytes.AddRange("ECFG"u8.ToArray());
        bytes.AddRange(BitConverter.GetBytes((uint)settings.Length));
        foreach ((string key, byte[] value) in settings)
        {
            byte[] keyBytes = Encoding.UTF8.GetBytes(key);
            bytes.AddRange(BitConverter.GetBytes((uint)keyBytes.Length));
            bytes.AddRange(keyBytes);
            bytes.AddRange(BitConverter.GetBytes((uint)value.Length));
            bytes.AddRange(value);
        }

        return bytes.ToArray();
    }

    [Fact]
    public void ParsesAndWritesProjectSettings()
    {
        byte[] binary = BuildEcFg(
            ("application/config/name", EncodeString("My Game")),
            ("application/run/main_scene", EncodeString("res://main.tscn")),
            ("application/config/features", EncodePoolStringArray("3.1")),
            ("display/window/size/width", EncodeInt(1280)),
            ("display/window/vsync/use_vsync", EncodeBool(true)),
            ("_global_script_classes", EncodePoolStringArray()));

        ProjectConfig config = ProjectConfig.ParseEcFg(binary, engineMajor: 3, engineMinor: 1);

        Assert.Equal(6, config.Settings.Count);
        Assert.Equal(4, config.ConfigVersion);
        Assert.Equal("My Game", config.Get("application/config/name")!.AsString());

        string text = config.ToProjectGodotText();
        Assert.Contains("config_version=4", text);
        Assert.Contains("[application]\n\nconfig/name=\"My Game\"\nrun/main_scene=\"res://main.tscn\"\n", text);
        Assert.Contains("config/features=PoolStringArray( \"3.1\" )", text);
        Assert.Contains("[display]\n\nwindow/size/width=1280\nwindow/vsync/use_vsync=true\n", text);
        Assert.StartsWith("; Engine configuration file.\n", text);
    }

    [Fact]
    public void PackedStringArraysDoNotKeepTheirNulTerminators()
    {
        byte[] binary = BuildEcFg(("editor_plugins/enabled", EncodePoolStringArray("a", "b")));
        ProjectConfig config = ProjectConfig.ParseEcFg(binary, 3, 1);
        string text = config.ToProjectGodotText();
        Assert.Contains("enabled=PoolStringArray( \"a\", \"b\" )", text);
        Assert.DoesNotContain('\0', text);
    }

    [Fact]
    public void RejectsCorruptProjectBinary()
    {
        Assert.Throws<ProjectConfigException>(() => ProjectConfig.ParseEcFg("NOPE"u8.ToArray(), 3, 1));
    }
}