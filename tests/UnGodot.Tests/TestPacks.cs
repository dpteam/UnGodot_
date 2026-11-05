using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using UnGodot.Core.Crypto;
using UnGodot.Core.Pck;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Tests;

/// <summary>Builders for synthetic Godot packs, one per format generation.</summary>
public static class TestPacks
{
    public sealed record FileSpec(string Path, byte[] Content, uint Flags = 0);

    /// <summary>Builds a v3/v4 style pack (magic, flags, file base, dir offset).</summary>
    public static byte[] BuildV4(IReadOnlyList<FileSpec> files, byte[]? key = null, uint formatVersion = 4,
        uint engineMajor = 4, uint engineMinor = 3, uint enginePatch = 0)
    {
        var data = new List<byte>();
        var table = new List<byte>();

        foreach (FileSpec file in files)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(file.Path);
            int padded = (pathBytes.Length + 3) & ~3;

            long offset = data.Count;
            byte[] stored = file.Content;
            uint flags = file.Flags;
            if (key is not null && (flags & PckFlags.FileEncrypted) != 0)
            {
                stored = EncryptedBlob.Seal(file.Content, key, Enumerable.Repeat((byte)0x7B, 16).ToArray());
            }

            data.AddRange(stored);

            table.AddRange(BitConverter.GetBytes((uint)padded));
            table.AddRange(pathBytes);
            table.AddRange(new byte[padded - pathBytes.Length]);
            table.AddRange(BitConverter.GetBytes((ulong)offset));
            table.AddRange(BitConverter.GetBytes((ulong)file.Content.Length));
            table.AddRange(MD5.HashData(file.Content));
            table.AddRange(BitConverter.GetBytes(flags));
        }

        const int headerSize = 112;
        long dirOffset = headerSize + data.Count;
        var pack = new List<byte>();
        pack.AddRange(BitConverter.GetBytes(PckHeader.Magic));
        pack.AddRange(BitConverter.GetBytes(formatVersion));
        pack.AddRange(BitConverter.GetBytes(engineMajor));
        pack.AddRange(BitConverter.GetBytes(engineMinor));
        pack.AddRange(BitConverter.GetBytes(enginePatch));

        uint packFlags = key is not null ? PckFlags.PackDirEncrypted : 0;
        pack.AddRange(BitConverter.GetBytes(packFlags));
        pack.AddRange(BitConverter.GetBytes((ulong)headerSize));
        pack.AddRange(BitConverter.GetBytes((ulong)dirOffset));
        while (pack.Count < headerSize)
        {
            pack.Add(0);
        }

        pack.AddRange(data);
        pack.AddRange(BitConverter.GetBytes((uint)files.Count));
        if (key is not null)
        {
            byte[] tableBytes = table.ToArray();
            pack.AddRange(EncryptedBlob.Seal(tableBytes, key, Enumerable.Repeat((byte)0x33, 16).ToArray()));
        }
        else
        {
            pack.AddRange(table);
        }

        return pack.ToArray();
    }

    /// <summary>Builds a Godot 3 style pack (format v1).</summary>
    public static byte[] BuildV1(IReadOnlyList<FileSpec> files)
    {
        var pack = new List<byte>();
        pack.AddRange(BitConverter.GetBytes(PckHeader.Magic));
        pack.AddRange(BitConverter.GetBytes(1u));
        pack.AddRange(BitConverter.GetBytes(3u));
        pack.AddRange(BitConverter.GetBytes(5u));
        pack.AddRange(BitConverter.GetBytes(3u));
        for (int i = 0; i < 16; i++)
        {
            pack.AddRange(BitConverter.GetBytes(0u));
        }

        // Table first, then data (as Godot 3 exports it). Offsets are absolute inside the
        // pack: the header is 20 bytes, 64 reserved, the count, then the entries.
        var entries = new List<byte>();
        long tableSize = 0;
        foreach (FileSpec file in files)
        {
            byte[] path = Encoding.UTF8.GetBytes(file.Path);
            tableSize += 4 + path.Length + 8 + 8 + 16;
        }

        long dataOffset = 84 + 4 + tableSize;
        foreach (FileSpec file in files)
        {
            byte[] path = Encoding.UTF8.GetBytes(file.Path);
            entries.AddRange(BitConverter.GetBytes((uint)path.Length));
            entries.AddRange(path);
            entries.AddRange(BitConverter.GetBytes((ulong)dataOffset));
            entries.AddRange(BitConverter.GetBytes((ulong)file.Content.Length));
            entries.AddRange(new byte[16]); // Godot 3 leaves the MD5 empty
            dataOffset += file.Content.Length;
        }

        pack.AddRange(BitConverter.GetBytes((uint)files.Count));
        pack.AddRange(entries);
        foreach (FileSpec file in files)
        {
            pack.AddRange(file.Content);
        }

        return pack.ToArray();
    }

    /// <summary>Builds a Godot 2 style pack (format v0, empty engine version).</summary>
    public static byte[] BuildV0(IReadOnlyList<FileSpec> files) => BuildV1Core(files, format: 0, engine: (0, 0, 0));

    /// <summary>Builds a Godot 4.0 style pack (format v2, directory after 64 reserved bytes).</summary>
    public static byte[] BuildV2(IReadOnlyList<FileSpec> files)
    {
        var table = new List<byte>();
        var data = new List<byte>();

        foreach (FileSpec file in files)
        {
            byte[] path = Encoding.UTF8.GetBytes(file.Path);
            int padded = (path.Length + 3) & ~3;
            table.AddRange(BitConverter.GetBytes((uint)padded));
            table.AddRange(path);
            table.AddRange(new byte[padded - path.Length]);
            table.AddRange(BitConverter.GetBytes((ulong)data.Count));
            table.AddRange(BitConverter.GetBytes((ulong)file.Content.Length));
            table.AddRange(MD5.HashData(file.Content));
            table.AddRange(BitConverter.GetBytes(file.Flags));
            data.AddRange(file.Content);
        }

        // v2 always stores an explicit file base: header + reserved + count + table.
        const int headerSize = 96;
        int fileBase = headerSize + 4 + table.Count;

        var pack = new List<byte>();
        pack.AddRange(BitConverter.GetBytes(PckHeader.Magic));
        pack.AddRange(BitConverter.GetBytes(2u));
        pack.AddRange(BitConverter.GetBytes(4u));
        pack.AddRange(BitConverter.GetBytes(0u));
        pack.AddRange(BitConverter.GetBytes(1u));
        pack.AddRange(BitConverter.GetBytes(0u)); // flags
        pack.AddRange(BitConverter.GetBytes((ulong)fileBase));
        while (pack.Count < headerSize)
        {
            pack.Add(0);
        }

        pack.AddRange(BitConverter.GetBytes((uint)files.Count));
        pack.AddRange(table);
        while (pack.Count < fileBase)
        {
            pack.Add(0);
        }

        pack.AddRange(data);
        return pack.ToArray();
    }

    private static byte[] BuildV1Core(IReadOnlyList<FileSpec> files, uint format, (uint, uint, uint) engine)
    {
        var pack = new List<byte>();
        pack.AddRange(BitConverter.GetBytes(PckHeader.Magic));
        pack.AddRange(BitConverter.GetBytes(format));
        pack.AddRange(BitConverter.GetBytes(engine.Item1));
        pack.AddRange(BitConverter.GetBytes(engine.Item2));
        pack.AddRange(BitConverter.GetBytes(engine.Item3));
        for (int i = 0; i < 16; i++)
        {
            pack.AddRange(BitConverter.GetBytes(0u));
        }

        var entries = new List<byte>();
        long dataOffset = 84 + 4;
        long tableSize = 0;
        foreach (FileSpec file in files)
        {
            byte[] path = Encoding.UTF8.GetBytes(file.Path);
            tableSize += 4 + path.Length + 8 + 8 + 16;
        }

        dataOffset = 84 + 4 + tableSize;
        foreach (FileSpec file in files)
        {
            byte[] path = Encoding.UTF8.GetBytes(file.Path);
            entries.AddRange(BitConverter.GetBytes((uint)path.Length));
            entries.AddRange(path);
            entries.AddRange(BitConverter.GetBytes((ulong)dataOffset));
            entries.AddRange(BitConverter.GetBytes((ulong)file.Content.Length));
            entries.AddRange(new byte[16]);
            dataOffset += file.Content.Length;
        }

        pack.AddRange(BitConverter.GetBytes((uint)files.Count));
        pack.AddRange(entries);
        foreach (FileSpec file in files)
        {
            pack.AddRange(file.Content);
        }

        return pack.ToArray();
    }

    /// <summary>Appends a pack to a fake executable the way a self-contained export does.</summary>
    public static byte[] AppendToExecutable(byte[] pack, byte[]? magicOverride = null)
    {
        var blob = new List<byte>();
        blob.AddRange(Enumerable.Repeat((byte)0xCD, 2048));
        long head = blob.Count;
        byte[] body = (byte[])pack.Clone();
        if (magicOverride is not null)
        {
            magicOverride.CopyTo(body, 0);
        }

        blob.AddRange(body);

        long ds = blob.Count + 16 - 24 - head;
        var tail = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(tail, (ulong)ds);
        (magicOverride ?? BitConverter.GetBytes(PckHeader.Magic)).CopyTo(tail, 12);
        blob.AddRange(tail);
        return blob.ToArray();
    }

    public static (byte[] Pack, PckHeader Header, PckFile Opened) Open(byte[] pack, byte[]? key = null)
    {
        using var source = new Core.IO.MemorySource(pack);
        if (!PckLocator.TryLocate(source, out PckHeader? header, out string? error))
        {
            throw new InvalidOperationException(error);
        }

        PckFile opened = PckFile.Open(source, header!, key);
        return (pack, header!, opened);
    }
}