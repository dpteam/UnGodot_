using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace UnGodot.Core.Logging;

/// <summary>Severity used by the whole toolkit. Ordered from most to least verbose.</summary>
public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    Off = 6,
}

/// <summary>How the log is emitted. Everything is optional so the library can stay silent.</summary>
public sealed class LogOptions
{
    public LogLevel MinimumLevel { get; init; } = LogLevel.Info;

    /// <summary>Write to stderr (never stdout, so piped output stays clean).</summary>
    public bool Console { get; init; } = true;

    /// <summary>Optional file sink (rolling, size capped).</summary>
    public string? FilePath { get; init; }

    public long MaxFileBytes { get; init; } = 8L * 1024 * 1024;
    public int MaxRolls { get; init; } = 4;

    /// <summary>ANSI colours on interactive terminals. Auto-disabled when redirected.</summary>
    public bool Colors { get; init; } = true;

    public bool Timestamps { get; init; } = true;
}

/// <summary>
/// Process wide logging built on <see cref="System.Diagnostics.Trace"/>.
///
/// No <c>app.config</c>/<c>appsettings.json</c> is involved: every listener is created
/// programmatically, and the default <see cref="DefaultTraceListener"/> is removed so a
/// message is never emitted twice (once by us, once by the debugger bridge).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static TraceSource? _source;
    private static LogLevel _minimum = LogLevel.Info;
    private static int _indent;

    public static LogLevel MinimumLevel => _minimum;

    public static bool IsEnabled(LogLevel level) => level >= _minimum && _minimum != LogLevel.Off;

    /// <summary>Installs the sinks. Safe to call more than once (the previous set is replaced).</summary>
    public static void Initialize(LogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (Gate)
        {
            Shutdown_NoLock();

            _minimum = options.MinimumLevel;
            _indent = 0;

            var source = new TraceSource("UnGodot", SourceLevels.All);
            // A TraceSource created in code has no listeners; drop any inherited one as well.
            source.Listeners.Clear();

            // The DefaultTraceListener duplicates messages into the debugger/output window
            // and calls the Win32 output API. It is not needed and is removed explicitly.
            for (var i = global::System.Diagnostics.Trace.Listeners.Count - 1; i >= 0; i--)
            {
                if (global::System.Diagnostics.Trace.Listeners[i] is DefaultTraceListener)
                {
                    global::System.Diagnostics.Trace.Listeners.RemoveAt(i);
                }
            }

            global::System.Diagnostics.Trace.AutoFlush = true;

            if (options.Console)
            {
                source.Listeners.Add(new ConsoleLogListener(
                    options.Colors && !Console.IsErrorRedirected && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")),
                    options.Timestamps));
            }

            if (!string.IsNullOrWhiteSpace(options.FilePath))
            {
                source.Listeners.Add(new RollingFileLogListener(
                    options.FilePath!,
                    options.MaxFileBytes,
                    options.MaxRolls,
                    options.Timestamps));
            }

            _source = source;
        }
    }

    /// <summary>Convenience: a console+file setup rooted at <paramref name="directory"/>.</summary>
    public static void Initialize(string? logDirectory, LogLevel minimumLevel = LogLevel.Info, bool console = true, bool colors = true)
    {
        string? file = null;
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                file = Path.Combine(logDirectory, "ungodot.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                file = null;
            }
        }

        Initialize(new LogOptions
        {
            MinimumLevel = minimumLevel,
            Console = console,
            FilePath = file,
            Colors = colors,
        });
    }

    public static void Shutdown()
    {
        lock (Gate)
        {
            Shutdown_NoLock();
        }
    }

    private static void Shutdown_NoLock()
    {
        if (_source is not null)
        {
            foreach (TraceListener listener in _source.Listeners)
            {
                try
                {
                    listener.Flush();
                    listener.Dispose();
                }
                catch
                {
                    // A broken sink must never break shutdown.
                }
            }

            _source.Listeners.Clear();
        }

        _source = null;
        _indent = 0;
    }

    /// <summary>Adds an indent for the lifetime of the returned scope (log output only).</summary>
    public static IndentScope Indent(string? label = null) => new(label);

    public readonly struct IndentScope : IDisposable
    {
        private readonly bool _owned;

        internal IndentScope(string? label)
        {
            Interlocked.Increment(ref _indent);
            if (!string.IsNullOrEmpty(label))
            {
                Log.Debug(label);
            }

            _owned = true;
        }

        public void Dispose()
        {
            if (_owned)
            {
                Log.IndentOnce();
            }
        }
    }

    private static void IndentOnce()
    {
        int value = Interlocked.Decrement(ref _indent);
        if (value < 0)
        {
            Interlocked.Exchange(ref _indent, 0);
        }
    }

    public static void Verbose(string message) => Write(LogLevel.Trace, message, null);
    public static void Debug(string message) => Write(LogLevel.Debug, message, null);
    public static void Info(string message) => Write(LogLevel.Info, message, null);
    public static void Warn(string message) => Write(LogLevel.Warning, message, null);
    public static void Error(string message) => Write(LogLevel.Error, message, null);
    public static void Error(string message, Exception exception) => Write(LogLevel.Error, message, exception);
    public static void Critical(string message, Exception? exception = null) => Write(LogLevel.Critical, message, exception);

    /// <summary>Emits pre-formatted child-process output (GDRE, dotnet, ...) exactly once.</summary>
    public static void Child(string line) => Write(LogLevel.Info, line, null, alreadyFormatted: true);

    public static void Write(LogLevel level, string message, Exception? exception = null, bool alreadyFormatted = false)
    {
        if (level < _minimum || _minimum == LogLevel.Off)
        {
            return;
        }

        var source = _source;
        string text = alreadyFormatted
            ? message
            : BuildMessage(level, message, exception);

        if (source is null)
        {
            // Before Initialize (very early startup, unit tests) fall back to plain stderr,
            // but never when the level is Off, so a library consumer stays silent by choice.
            if (level >= LogLevel.Warning)
            {
                Console.Error.WriteLine(text);
            }

            return;
        }

        try
        {
            source.TraceEvent(ToTraceType(level), (int)level, text);
        }
        catch
        {
            // Logging must never throw into the caller.
        }
    }

    internal static string BuildMessage(LogLevel level, string message, Exception? exception)
    {
        int indent = Volatile.Read(ref _indent);
        var sb = new StringBuilder(message.Length + 64 + indent * 2);
        if (indent > 0)
        {
            sb.Append(' ', Math.Min(indent, 16) * 2);
        }

        sb.Append(message);
        if (exception is not null)
        {
            sb.AppendLine();
            sb.Append(exception);
        }

        return sb.ToString();
    }

    internal static TraceEventType ToTraceType(LogLevel level) => level switch
    {
        LogLevel.Trace => TraceEventType.Verbose,
        LogLevel.Debug => TraceEventType.Verbose,
        LogLevel.Info => TraceEventType.Information,
        LogLevel.Warning => TraceEventType.Warning,
        LogLevel.Error => TraceEventType.Error,
        _ => TraceEventType.Critical,
    };
}

/// <summary>Formats one event per line on stderr, with optional ANSI colours.</summary>
internal sealed class ConsoleLogListener : TraceListener
{
    private const string Reset = "\x1b[0m";
    private readonly TextWriter _writer;
    private readonly bool _colors;
    private readonly bool _timestamps;
    private readonly object _gate = new();

    public ConsoleLogListener(bool colors, bool timestamps)
    {
        _writer = Console.Error;
        _colors = colors;
        _timestamps = timestamps;
        if (colors)
        {
            EnableVirtualTerminal();
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        if (Filter is not null && !Filter.ShouldTrace(eventCache, source, eventType, id, message, null, null, null))
        {
            return;
        }

        WriteLine(message ?? string.Empty, eventType);
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
    {
        if (args is { Length: > 0 })
        {
            WriteLine(string.Format(format ?? string.Empty, args), eventType);
        }
        else
        {
            WriteLine(format ?? string.Empty, eventType);
        }
    }

    public override void Write(string? message) => Write(message, TraceEventType.Information);
    public override void WriteLine(string? message) => WriteLine(message ?? string.Empty, TraceEventType.Information);

    private void Write(string? message, TraceEventType type)
    {
        if (!string.IsNullOrEmpty(message))
        {
            Emit(message, type, newLine: false);
        }
    }

    private void WriteLine(string message, TraceEventType type) => Emit(message, type, newLine: true);

    private void Emit(string message, TraceEventType type, bool newLine)
    {
        string stamp = _timestamps ? DateTime.Now.ToString("HH:mm:ss.fff") + " " : string.Empty;
        string tag = Tag(type);
        string color = _colors ? Color(type) : string.Empty;
        string reset = _colors ? Reset : string.Empty;

        lock (_gate)
        {
            var sb = new StringBuilder(message.Length + 32);
            sb.Append(stamp);
            if (!string.IsNullOrEmpty(color))
            {
                sb.Append(color);
            }

            sb.Append(tag);
            if (!string.IsNullOrEmpty(color))
            {
                sb.Append(reset);
            }

            sb.Append(' ').Append(message);
            if (newLine)
            {
                _writer.WriteLine(sb.ToString());
            }
            else
            {
                _writer.Write(sb.ToString());
            }

            _writer.Flush();
        }
    }

    private static string Tag(TraceEventType type) => type switch
    {
        TraceEventType.Critical => "crit",
        TraceEventType.Error => "err ",
        TraceEventType.Warning => "warn",
        TraceEventType.Information => "info",
        _ => "dbg ",
    };

    private static string Color(TraceEventType type) => type switch
    {
        TraceEventType.Critical => "\x1b[1;35m",
        TraceEventType.Error => "\x1b[1;31m",
        TraceEventType.Warning => "\x1b[1;33m",
        TraceEventType.Information => "\x1b[1;36m",
        _ => "\x1b[2m",
    };

    private static void EnableVirtualTerminal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            nint handle = GetStdHandle(-12); // STD_ERROR_HANDLE
            if (handle == 0 || handle == -1)
            {
                return;
            }

            if (GetConsoleMode(handle, out uint mode))
            {
                _ = SetConsoleMode(handle, mode | 0x0004); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
            }
        }
        catch
        {
            // Best effort only.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(nint hConsoleHandle, uint dwMode);
}

/// <summary>Size-capped, self-rolling UTF-8 log file. Never throws at the call site.</summary>
internal sealed class RollingFileLogListener : TraceListener
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxRolls;
    private readonly bool _timestamps;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public RollingFileLogListener(string path, long maxBytes, int maxRolls, bool timestamps)
    {
        _path = Path.GetFullPath(path);
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxRolls = Math.Max(1, maxRolls);
        _timestamps = timestamps;
        try
        {
            string? dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Open();
        }
        catch
        {
            _writer = null;
        }
    }

    private void Open()
    {
        var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 16 * 1024);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    private void RollIfNeeded()
    {
        try
        {
            if (_writer is null)
            {
                return;
            }

            if (_writer.BaseStream.Length < _maxBytes)
            {
                return;
            }

            _writer.Dispose();
            _writer = null;

            for (int i = _maxRolls - 1; i >= 1; i--)
            {
                string from = $"{_path}.{i}";
                string to = $"{_path}.{i + 1}";
                if (File.Exists(from))
                {
                    File.Move(from, to, overwrite: true);
                }
            }

            if (File.Exists(_path))
            {
                File.Move(_path, $"{_path}.1", overwrite: true);
            }

            Open();
        }
        catch
        {
            // Rotation failures must not stop logging: keep appending if possible.
            try
            {
                _writer ??= new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch
            {
                _writer = null;
            }
        }
    }

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
        => WriteLine(message, eventType);

    public override void TraceEvent(TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? format, params object?[]? args)
        => WriteLine(args is { Length: > 0 } ? string.Format(format ?? string.Empty, args) : format, eventType);

    public override void Write(string? message) => WriteLine(message, TraceEventType.Information);
    public override void WriteLine(string? message) => WriteLine(message ?? string.Empty, TraceEventType.Information);

    private void WriteLine(string? message, TraceEventType type)
    {
        if (message is null)
        {
            return;
        }

        string stamp = _timestamps ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " : string.Empty;
        string level = type switch
        {
            TraceEventType.Critical => "CRIT ",
            TraceEventType.Error => "ERROR",
            TraceEventType.Warning => "WARN ",
            TraceEventType.Information => "INFO ",
            _ => "DEBUG",
        };

        lock (_gate)
        {
            try
            {
                RollIfNeeded();
                _writer?.WriteLine(stamp + level + " " + message);
            }
            catch
            {
                // Ignore: a full or locked log file must not abort the operation.
            }
        }
    }

    public override void Flush()
    {
        lock (_gate)
        {
            try
            {
                _writer?.Flush();
            }
            catch
            {
                // ignore
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                    // ignore
                }

                _writer = null;
            }
        }

        base.Dispose(disposing);
    }
}