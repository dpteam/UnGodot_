namespace UnGodot.Dump;

/// <summary>A random-access byte source: a process image, a minidump or a plain file.</summary>
public interface IMemorySource : IDisposable
{
    string DisplayName { get; }

    /// <summary>The ranges worth scanning. A file is one range; a process is its mapped regions.</summary>
    IEnumerable<(long Start, long Size)> Segments { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes; returns 0 when unreadable.</summary>
    int Read(long offset, Span<byte> buffer);
}

/// <summary>Reads a <c>.dmp</c>/raw memory image from disk, so carving works offline.</summary>
public sealed class FileMemorySource : IMemorySource
{
    private readonly FileStream _stream;

    public FileMemorySource(string path)
    {
        _stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.RandomAccess);
        DisplayName = path;
    }

    public string DisplayName { get; }

    public long Length => _stream.Length;

    public IEnumerable<(long Start, long Size)> Segments
    {
        get
        {
            yield return (0, _stream.Length);
        }
    }

    public int Read(long offset, Span<byte> buffer)
    {
        if (offset < 0 || offset >= _stream.Length)
        {
            return 0;
        }

        _stream.Position = offset;
        return _stream.Read(buffer);
    }

    public void Dispose() => _stream.Dispose();
}