using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Win32.SafeHandles;

namespace UnGodot.Core.IO;

/// <summary>
/// Random access over a file-like byte range.
///
/// A pack can legally be larger than 2 GiB (Godot's own export limit is far higher), so
/// nothing here ever loads a whole pack into a single array: callers ask for the slices
/// they need. Implementations are safe to share between threads.
/// </summary>
public interface IRandomAccessSource : IDisposable
{
    string DisplayName { get; }

    long Length { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes; returns the count read.</summary>
    int ReadAt(long offset, Span<byte> buffer);

    /// <summary>Reads exactly the buffer, throwing when the source is truncated.</summary>
    void ReadExactly(long offset, Span<byte> buffer);

    byte[] ReadAt(long offset, int count);
}

public sealed class TruncatedSourceException : IOException
{
    public TruncatedSourceException(string message) : base(message)
    {
    }
}

/// <summary>A file opened for concurrent random reads (the same file the game may still use).</summary>
public sealed class FileSource : IRandomAccessSource
{
    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private int _disposed;

    private FileSource(string displayName, SafeFileHandle handle, long length)
    {
        DisplayName = displayName;
        _handle = handle;
        _length = length;
    }

    public string DisplayName { get; }

    public long Length => _length;

    /// <summary>
    /// Opens a file for reading. <see cref="FileShare.ReadWrite"/> plus
    /// <see cref="FileShare.Delete"/> keeps working while the game (or an antivirus) holds
    /// the file, which is the common case on a live install and on network shares.
    /// </summary>
    public static FileSource Open(string path, int retries = 3)
    {
        string full = PathUtil.FullPath(path);
        Exception? last = null;

        for (int attempt = 0; attempt <= retries; attempt++)
        {
            try
            {
                var handle = File.OpenHandle(
                    full,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    FileOptions.RandomAccess);

                long length = RandomAccess.GetLength(handle);
                return new FileSource(path, handle, length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < retries)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }

        throw new IOException($"cannot open '{path}': {last?.Message}", last);
    }

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset > _length)
        {
            return 0;
        }

        int want = (int)Math.Min(buffer.Length, _length - offset);
        if (want <= 0)
        {
            return 0;
        }

        Span<byte> target = buffer[..want];
        int total = 0;
        int failures = 0;

        while (total < target.Length)
        {
            try
            {
                int read = RandomAccess.Read(_handle, target[total..], offset + total);
                if (read <= 0)
                {
                    break;
                }

                total += read;
                failures = 0;
            }
            catch (IOException) when (failures < 3)
            {
                // Transient network hiccup: back off and retry the same slice.
                failures++;
                Thread.Sleep(20 * failures);
            }
        }

        return total;
    }

    public void ReadExactly(long offset, Span<byte> buffer)
    {
        int read = ReadAt(offset, buffer);
        if (read != buffer.Length)
        {
            throw new TruncatedSourceException(
                $"'{DisplayName}': wanted {buffer.Length} bytes at offset {offset}, got {read} (file size {_length})");
        }
    }

    public byte[] ReadAt(long offset, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        long available = Math.Max(0, _length - offset);
        int size = (int)Math.Min(count, available);
        var result = new byte[size];
        ReadExactly(offset, result);
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _handle.Dispose();
        }
    }
}

/// <summary>In-memory source used by tests and for decrypted buffers.</summary>
public sealed class MemorySource : IRandomAccessSource
{
    private readonly ReadOnlyMemory<byte> _data;

    public MemorySource(ReadOnlyMemory<byte> data, string displayName = "<memory>")
    {
        _data = data;
        DisplayName = displayName;
    }

    public string DisplayName { get; }

    public long Length => _data.Length;

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _data.Length)
        {
            return 0;
        }

        int available = (int)Math.Min(buffer.Length, _data.Length - offset);
        _data.Span.Slice((int)offset, available).CopyTo(buffer);
        return available;
    }

    public void ReadExactly(long offset, Span<byte> buffer)
    {
        int read = ReadAt(offset, buffer);
        if (read != buffer.Length)
        {
            throw new TruncatedSourceException($"memory source truncated at {offset}");
        }
    }

    public byte[] ReadAt(long offset, int count)
    {
        long available = Math.Max(0, _data.Length - offset);
        int size = (int)Math.Min(count, available);
        return size <= 0 ? [] : _data.Slice((int)offset, size).ToArray();
    }

    public void Dispose()
    {
    }
}

/// <summary>Sequential reader with an internal buffer, used to parse pack headers and tables.</summary>
public sealed class SourceReader : IDisposable
{
    private readonly IRandomAccessSource _source;
    private readonly byte[] _buffer;
    private long _bufferStart;
    private int _bufferLength;
    private int _bufferPos;

    public SourceReader(IRandomAccessSource source, long position, int bufferSize = 64 * 1024)
    {
        _source = source;
        Position = position;
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(4096, bufferSize));
        _bufferStart = position;
        _bufferLength = 0;
        _bufferPos = 0;
    }

    public long Position { get; private set; }

    public long Remaining => _source.Length - Position;

    private bool Ensure(int count)
    {
        if (_bufferPos + count <= _bufferLength)
        {
            return true;
        }

        // Move the unread tail to the front, then top the buffer up.
        int tail = _bufferLength - _bufferPos;
        if (tail > 0 && _bufferPos > 0)
        {
            Buffer.BlockCopy(_buffer, _bufferPos, _buffer, 0, tail);
        }

        _bufferStart += _bufferPos;
        _bufferPos = 0;
        _bufferLength = tail;

        while (_bufferLength < count)
        {
            int want = _buffer.Length - _bufferLength;
            if (want <= 0)
            {
                return false;
            }

            int read = _source.ReadAt(_bufferStart + _bufferLength, _buffer.AsSpan(_bufferLength, want));
            if (read <= 0)
            {
                return false;
            }

            _bufferLength += read;
        }

        return true;
    }

    public bool TryReadByte(out byte value)
    {
        if (!Ensure(1))
        {
            value = 0;
            return false;
        }

        value = _buffer[_bufferPos++];
        Position++;
        return true;
    }

    public bool TryReadUInt32(out uint value)
    {
        value = 0;
        if (!Ensure(4))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_bufferPos, 4));
        _bufferPos += 4;
        Position += 4;
        return true;
    }

    public bool TryReadUInt64(out ulong value)
    {
        value = 0;
        if (!Ensure(8))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(_bufferPos, 8));
        _bufferPos += 8;
        Position += 8;
        return true;
    }

    public bool TryReadInt64(out long value)
    {
        value = 0;
        if (!TryReadUInt64(out ulong raw))
        {
            return false;
        }

        value = unchecked((long)raw);
        return true;
    }

    public bool TryReadBytes(Span<byte> destination)
    {
        int written = 0;
        while (written < destination.Length)
        {
            if (!Ensure(1))
            {
                return false;
            }

            int chunk = Math.Min(destination.Length - written, _bufferLength - _bufferPos);
            _buffer.AsSpan(_bufferPos, chunk).CopyTo(destination[written..]);
            _bufferPos += chunk;
            Position += chunk;
            written += chunk;
        }

        return true;
    }

    public bool TrySkip(long count)
    {
        if (count < 0)
        {
            return false;
        }

        while (count > 0)
        {
            if (!Ensure(1))
            {
                return false;
            }

            int chunk = (int)Math.Min(count, _bufferLength - _bufferPos);
            _bufferPos += chunk;
            Position += chunk;
            count -= chunk;
        }

        return true;
    }

    public void Dispose() => ArrayPool<byte>.Shared.Return(_buffer);
}

/// <summary>Windows long-path aware path helpers.</summary>
public static class PathUtil
{
    /// <summary>Absolute path, prefixed with <c>\\?\</c> when Windows would otherwise complain.</summary>
    public static string FullPath(string path)
    {
        string full = Path.GetFullPath(path);
        return WithLongPathPrefix(full);
    }

    public static string WithLongPathPrefix(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || fullPath.Length < 240 || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        try
        {
            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return @"\\?\UNC\" + fullPath[2..];
            }

            return @"\\?\" + fullPath;
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>Removes a <c>\\?\</c> prefix for display.</summary>
    public static string ForDisplay(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    public static bool IsNetworkPath(string path)
    {
        try
        {
            return Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal);
        }
        catch
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal);
        }
    }

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LooksLikePath(string text)
    {
        foreach (char c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}