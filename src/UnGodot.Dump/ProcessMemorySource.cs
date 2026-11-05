using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UnGodot.Dump;

/// <summary>
/// Reads the address space of a running process. Windows only: a game has to be running for
/// its decrypted buffers, loaded packs and script bytecode to exist at all, and this is the
/// cheapest way to get at them (no driver, no injection).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProcessMemorySource : IMemorySource
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public ulong BaseAddress;
        public ulong AllocationBase;
        public uint AllocationProtect;
        public ulong RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint VirtualQueryEx(IntPtr process, ulong address, out MemoryBasicInformation info, nuint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, ulong address, byte[] buffer, nuint size, out nuint read);

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr process, int pid, IntPtr file, uint type, IntPtr exception, IntPtr user, IntPtr callback);

    private readonly IntPtr _handle;

    private ProcessMemorySource(int pid, string name, IntPtr handle)
    {
        Pid = pid;
        DisplayName = name;
        _handle = handle;
    }

    public int Pid { get; }

    public string DisplayName { get; }

    public IEnumerable<(long Start, long Size)> Segments =>
        ReadableRegions().Select(region => ((long)region.Start, (long)region.Size));

    public static ProcessMemorySource Open(string processName)
    {
        Process? process = Process.GetProcessesByName(processName)
            .OrderByDescending(p => p.WorkingSet64)
            .FirstOrDefault();

        if (process is null)
        {
            throw new DumpException($"process '{processName}' is not running");
        }

        return Open(process.Id, processName);
    }

    public static ProcessMemorySource Open(int pid, string? name = null)
    {
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (handle == IntPtr.Zero)
        {
            throw new DumpException(
                $"cannot open process {pid} (error {Marshal.GetLastWin32Error()}); " +
                "run elevated or dump the process with another tool and use --from-dump");
        }

        return new ProcessMemorySource(pid, name ?? $"pid {pid}", handle);
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || buffer.Length == 0)
        {
            return 0;
        }

        // ReadProcessMemory needs a byte[]; rent one only for the requested length.
        byte[] scratch = new byte[buffer.Length];
        if (!ReadProcessMemory(_handle, (ulong)offset, scratch, (nuint)buffer.Length, out nuint read) || read == 0)
        {
            return 0;
        }

        scratch.AsSpan(0, (int)read).CopyTo(buffer);
        return (int)read;
    }

    /// <summary>
    /// Enumerates the committed, readable regions so the scanner never probes unmapped pages.
    /// </summary>
    public IEnumerable<(ulong Start, ulong Size)> ReadableRegions()
    {
        ulong address = 0;
        while (VirtualQueryEx(_handle, address, out MemoryBasicInformation region, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) != 0)
        {
            ulong next = region.BaseAddress + region.RegionSize;
            bool readable = region.State == MemCommit
                && (region.Protect & PageNoAccess) == 0
                && (region.Protect & PageGuard) == 0
                && region.RegionSize > 0
                && region.RegionSize < 2UL * 1024 * 1024 * 1024;

            if (readable)
            {
                yield return (region.BaseAddress, region.RegionSize);
            }

            if (next <= address)
            {
                yield break;
            }

            address = next;
        }
    }

    /// <summary>Writes a full-memory minidump; the result can be carved offline with --from-dump.</summary>
    public void WriteMiniDump(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        // MiniDumpWithFullMemory | MiniDumpWithHandleData | MiniDumpWithUnloadedModules
        const uint type = 0x00000002 | 0x00000004 | 0x00000020;
        if (!MiniDumpWriteDump(_handle, Pid, file.SafeFileHandle.DangerousGetHandle(), type, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero))
        {
            throw new DumpException($"MiniDumpWriteDump failed with error {Marshal.GetLastWin32Error()}");
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
        }
    }
}

public sealed class DumpException : Exception
{
    public DumpException(string message) : base(message)
    {
    }
}