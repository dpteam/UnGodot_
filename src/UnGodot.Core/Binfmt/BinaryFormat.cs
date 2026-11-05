using System.Buffers.Binary;
using System.Text;
using UnGodot.Core.IO;

namespace UnGodot.Core.Binfmt;

/// <summary>A named byte range inside a container file.</summary>
public readonly record struct BinarySection(string Name, long Start, long End)
{
    public long Length => End - Start;
}

/// <summary>What kind of file a candidate is, from its first bytes.</summary>
public enum ContainerKind
{
    Unknown,
    GodotPack,
    PeExecutable,
    ElfExecutable,
    MachO,
    ZipArchive,
    AndroidPackage,
    CriCpk,
    Directory,
}

public static class ContainerKindExtensions
{
    public static string Describe(this ContainerKind kind) => kind switch
    {
        ContainerKind.GodotPack => "PCK (standalone Godot pack)",
        ContainerKind.PeExecutable => "PE (Windows executable)",
        ContainerKind.ElfExecutable => "ELF (Linux/macOS executable)",
        ContainerKind.MachO => "Mach-O (macOS executable)",
        ContainerKind.ZipArchive => "ZIP archive",
        ContainerKind.AndroidPackage => "APK (Android package)",
        ContainerKind.CriCpk => "CRI CPK (CPK container, not a Godot pack)",
        ContainerKind.Directory => "directory",
        _ => "unknown",
    };
}

/// <summary>
/// Minimal PE / ELF / Mach-O readers. They narrow the key search to the sections that hold
/// initialised data, where Godot stores the encryption key blob.
/// </summary>
public static class BinaryFormat
{
    /// <summary>How much of the start of a file is enough to read the PE section table.</summary>
    private const int PeHeaderWindow = 1024 * 1024;

    public static ContainerKind DetectContainer(ReadOnlySpan<byte> head, string? path = null)
    {
        if (head.Length >= 2 && head[0] == 'M' && head[1] == 'Z')
        {
            return ContainerKind.PeExecutable;
        }

        if (head.Length >= 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F')
        {
            return ContainerKind.ElfExecutable;
        }

        if (head.Length >= 4 &&
            ((head[0] == 0xCF && head[1] == 0xFA && head[2] == 0xED && head[3] == 0xFE) ||
             (head[0] == 0xCE && head[1] == 0xFA && head[2] == 0xED && head[3] == 0xFE)))
        {
            return ContainerKind.MachO;
        }

        if (head.Length >= 4 && head[0] == 0xCA && head[1] == 0xFE && head[2] == 0xBA && head[3] == 0xBE)
        {
            return ContainerKind.MachO;
        }

        if (head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Pck.PckHeader.Magic)
        {
            return ContainerKind.GodotPack;
        }

        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' &&
            ((head[2] == 3 && head[3] == 4) || (head[2] == 5 && head[3] == 6)))
        {
            bool apk = path is not null && path.EndsWith(".apk", StringComparison.OrdinalIgnoreCase);
            return apk ? ContainerKind.AndroidPackage : ContainerKind.ZipArchive;
        }

        if (head.Length >= 4 && head[0] == 'C' && head[1] == 'P' && head[2] == 'K' && head[3] == ' ')
        {
            return ContainerKind.CriCpk;
        }

        return ContainerKind.Unknown;
    }

    /// <summary>Data sections of a PE binary. <paramref name="fileLength"/> bounds the ranges.</summary>
    public static List<BinarySection>? PeSections(ReadOnlySpan<byte> headerBytes, long fileLength)
    {
        if (headerBytes.Length < 0x40 || headerBytes[0] != 'M' || headerBytes[1] != 'Z')
        {
            return null;
        }

        long peOffset = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[0x3C..]);
        if (peOffset < 0 || peOffset + 24 > headerBytes.Length ||
            headerBytes[(int)peOffset] != 'P' || headerBytes[(int)peOffset + 1] != 'E' ||
            headerBytes[(int)peOffset + 2] != 0 || headerBytes[(int)peOffset + 3] != 0)
        {
            return null;
        }

        int coff = (int)peOffset + 4;
        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[(coff + 2)..]);
        int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(headerBytes[(coff + 16)..]);
        int sectionStart = coff + 20 + optionalSize;

        var sections = new List<BinarySection>(Math.Min(sectionCount, 96));
        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionStart + i * 40;
            if (header + 40 > headerBytes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> nameBytes = headerBytes.Slice(header, 8);
            int nameEnd = nameBytes.IndexOf((byte)0);
            if (nameEnd < 0)
            {
                nameEnd = 8;
            }

            string name = Encoding.UTF8.GetString(nameBytes[..nameEnd]);
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[(header + 16)..]);
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(headerBytes[(header + 20)..]);
            if (rawPointer == 0 || rawSize == 0)
            {
                continue;
            }

            long start = rawPointer;
            long end = Math.Min(start + rawSize, fileLength);
            if (start >= end)
            {
                continue;
            }

            sections.Add(new BinarySection(name, start, end));
        }

        return sections;
    }

    /// <summary>Section data ranges of a 64 bit ELF binary.</summary>
    public static List<BinarySection>? ElfSections(IRandomAccessSource source)
    {
        Span<byte> header = stackalloc byte[64];
        if (source.ReadAt(0, header) != 64 || header[0] != 0x7F || header[1] != (byte)'E' ||
            header[2] != (byte)'L' || header[3] != (byte)'F' || header[4] != 2)
        {
            return null;
        }

        bool little = header[5] == 1;
        ulong ReadU64(ReadOnlySpan<byte> raw) => little
            ? BinaryPrimitives.ReadUInt64LittleEndian(raw)
            : BinaryPrimitives.ReadUInt64BigEndian(raw);

        ushort ReadU16(ReadOnlySpan<byte> raw) => little
            ? BinaryPrimitives.ReadUInt16LittleEndian(raw)
            : BinaryPrimitives.ReadUInt16BigEndian(raw);

        long sectionOffset = (long)ReadU64(header[0x28..]);
        int entrySize = ReadU16(header[0x3A..]);
        int sectionCount = ReadU16(header[0x3C..]);
        int stringIndex = ReadU16(header[0x3E..]);
        if (sectionOffset <= 0 || sectionCount == 0 || entrySize < 64)
        {
            return null;
        }

        long tableSize = (long)sectionCount * entrySize;
        if (tableSize <= 0 || tableSize > 16 * 1024 * 1024 || sectionOffset + tableSize > source.Length)
        {
            return null;
        }

        byte[] table = source.ReadAt(sectionOffset, (int)tableSize);
        if (table.Length < tableSize)
        {
            return null;
        }

        long stringHeader = (long)stringIndex * entrySize;
        long stringOffset = 0;
        if (stringHeader + 40 <= table.Length)
        {
            stringOffset = (long)ReadU64(table.AsSpan((int)stringHeader + 24, 8));
        }

        byte[] names = stringOffset > 0 && stringOffset < source.Length
            ? source.ReadAt(stringOffset, (int)Math.Min(4 * 1024 * 1024, source.Length - stringOffset))
            : [];

        var sections = new List<BinarySection>(Math.Min(sectionCount, 96));
        for (int i = 0; i < sectionCount; i++)
        {
            int offset = i * entrySize;
            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset, 4));
            ulong type = ReadU64(table.AsSpan(offset + 4, 8));
            long start = (long)ReadU64(table.AsSpan(offset + 24, 8));
            long size = (long)ReadU64(table.AsSpan(offset + 32, 8));
            if (type == 8 || size <= 0 || start <= 0)
            {
                continue; // NOBITS or empty
            }

            long end = Math.Min(start + size, source.Length);
            if (start >= end)
            {
                continue;
            }

            string name = string.Empty;
            if (nameOffset < names.Length)
            {
                ReadOnlySpan<byte> rest = names.AsSpan((int)nameOffset);
                int zero = rest.IndexOf((byte)0);
                name = Encoding.UTF8.GetString(zero >= 0 ? rest[..zero] : rest);
            }

            sections.Add(new BinarySection(name, start, end));
        }

        return sections;
    }

    /// <summary>The regions worth scanning for the key: initialised data sections.</summary>
    public static List<(long Start, long End)> CandidateRanges(IRandomAccessSource source)
    {
        var ranges = new List<(long, long)>();
        try
        {
            Span<byte> head = stackalloc byte[4];
            if (source.ReadAt(0, head) == 4)
            {
                ContainerKind kind = DetectContainer(head, source.DisplayName);
                List<BinarySection>? sections = null;

                if (kind == ContainerKind.PeExecutable)
                {
                    int window = (int)Math.Min(PeHeaderWindow, source.Length);
                    byte[] header = source.ReadAt(0, window);
                    sections = PeSections(header, source.Length);
                }
                else if (kind == ContainerKind.ElfExecutable)
                {
                    sections = ElfSections(source);
                }

                if (sections is not null)
                {
                    foreach (BinarySection section in sections)
                    {
                        if (section.Name.Contains("data", StringComparison.OrdinalIgnoreCase) ||
                            section.Name.Contains("rdata", StringComparison.OrdinalIgnoreCase) ||
                            section.Name.Contains("rodata", StringComparison.OrdinalIgnoreCase))
                        {
                            ranges.Add((section.Start, section.End));
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or TruncatedSourceException)
        {
            ranges.Clear();
        }

        if (ranges.Count == 0)
        {
            ranges.Add((0, source.Length));
        }

        return ranges;
    }
}