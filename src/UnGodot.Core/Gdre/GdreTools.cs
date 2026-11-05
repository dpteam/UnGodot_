using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnGodot.Core.Logging;
using UnGodot.Core.Net;

namespace UnGodot.Core.Gdre;

/// <summary>
/// Integration with GDRE Tools (https://github.com/GDRETools/gdsdecomp), the reference
/// implementation for full project recovery.
/// </summary>
public static class GdreTools
{
    private const string ReleasesApi = "https://api.github.com/repos/GDRETools/gdsdecomp/releases/latest";

    public static IReadOnlyList<string> BinaryNames => OperatingSystem.IsWindows()
        ? ["gdre_tools.exe"]
        : ["gdre_tools", "gdre_tools.x86_64", "gdre_tools.aarch64", "gdre_tools.arm64"];

    /// <summary>Looks for the GDRE Tools binary: explicit path, env var, PATH, common folders.</summary>
    public static string? Locate(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("UNGODOT_GDRE") ?? Environment.GetEnvironmentVariable("GORE_GDRE");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (string directory in SearchDirectories())
        {
            foreach (string name in BinaryNames)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                yield return directory;
            }
        }

        foreach (string variable in new[] { "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            yield return Path.Combine(value, ".local", "share", "gdre_tools");
            yield return Path.Combine(value, ".local", "share", "ungodot");
            yield return Path.Combine(value, "bin");
            yield return Path.Combine(value, "Applications");
        }

        yield return Http.DataDirectory;
        yield return Path.Combine(Http.DataDirectory, "gdre");
    }

    /// <summary>Runs GDRE Tools, rendering its progress bars without mangling the output.</summary>
    public static async Task RunAsync(string binary, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new IOException($"cannot start {binary}");
        }

        var renderer = new GdreOutputRenderer(!Console.IsErrorRedirected);
        Task pumpOut = PumpAsync(process.StandardOutput, renderer, cancellationToken);
        Task pumpErr = PumpAsync(process.StandardError, renderer, cancellationToken);

        using (cancellationToken.Register(() =>
               {
                   try
                   {
                       if (!process.HasExited)
                       {
                           process.Kill(entireProcessTree: true);
                       }
                   }
                   catch
                   {
                       // the process may have exited between the check and the kill
                   }
               }))
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        await Task.WhenAll(pumpOut, pumpErr).ConfigureAwait(false);
        renderer.Finish();

        if (process.ExitCode != 0)
        {
            throw new GdreException($"GDRE Tools exited with code {process.ExitCode}");
        }
    }

    private static async Task PumpAsync(StreamReader reader, GdreOutputRenderer renderer, CancellationToken cancellationToken)
    {
        char[] buffer = new char[8192];
        var pending = new StringBuilder();
        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                char c = buffer[i];
                if (c is '\r' or '\n')
                {
                    renderer.Segment(pending.ToString());
                    pending.Clear();
                }
                else
                {
                    pending.Append(c);
                }
            }
        }

        if (pending.Length > 0)
        {
            renderer.Segment(pending.ToString());
        }
    }

    /// <summary>Downloads the latest GDRE Tools release for this platform into <paramref name="destination"/>.</summary>
    public static async Task<string> DownloadLatestAsync(string destination, CancellationToken cancellationToken)
    {
        using JsonDocument release = await Http.GetJsonAsync(ReleasesApi, cancellationToken);
        JsonElement root = release.RootElement;
        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? "unknown" : "unknown";

        string wanted = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        JsonElement assets = root.TryGetProperty("assets", out JsonElement assetsElement) && assetsElement.ValueKind == JsonValueKind.Array
            ? assetsElement
            : default;

        JsonElement? chosen = null;
        if (assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                if (name.Contains(wanted, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    chosen = asset;
                    break;
                }
            }

            if (chosen is null)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = asset;
                        break;
                    }
                }
            }
        }

        if (chosen is null)
        {
            throw new IOException($"no downloadable asset found in GDRE Tools release {tag}");
        }

        string url = chosen.Value.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
        string assetName = chosen.Value.TryGetProperty("name", out JsonElement a) ? a.GetString() ?? "gdre.zip" : "gdre.zip";
        if (string.IsNullOrEmpty(url))
        {
            throw new IOException($"GDRE Tools release {tag} has an asset without a download URL");
        }

        Log.Info($"downloading GDRE Tools {tag} ({assetName})");
        Directory.CreateDirectory(destination);
        string archive = Path.Combine(destination, "gdre_tools.zip");
        await Http.DownloadAsync(url, archive, cancellationToken).ConfigureAwait(false);

        Http.ExtractZip(archive, destination);
        try
        {
            File.Delete(archive);
        }
        catch
        {
            // harmless
        }

        string? installed = BinaryNames
            .Select(name => Path.Combine(destination, name))
            .FirstOrDefault(File.Exists);

        if (installed is null)
        {
            // The archive may nest the binary in a folder.
            installed = Directory.EnumerateFiles(destination, "gdre_tools*", SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.EndsWith(".pck", StringComparison.OrdinalIgnoreCase) &&
                                        !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                                        !path.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
                                        !path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        if (installed is null)
        {
            throw new IOException("the GDRE Tools archive does not contain the gdre_tools binary");
        }

        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(installed, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch
            {
                // best effort
            }
        }

        return installed;
    }
}

public sealed class GdreException : Exception
{
    public GdreException(string message) : base(message)
    {
    }
}

/// <summary>One running GDRE task and how far it got.</summary>
internal sealed class GdreTask
{
    public required string Name { get; init; }
    public int Percent { get; set; }
    public DateTime Seen { get; set; }
}

/// <summary>
/// Keeps every GDRE progress bar on a single line and prints ordinary messages above it.
///
/// GDRE runs tasks in parallel (exporting resources while compiling C# and generating the
/// filesystem cache) and each one redraws its own bar on the same line, so the line used to
/// flicker between them; this renderer tracks them all and draws them side by side.
/// </summary>
internal sealed class GdreOutputRenderer
{
    private static readonly TimeSpan TaskStale = TimeSpan.FromMilliseconds(2500);
    private readonly bool _tty;
    private readonly object _gate = new();
    private string? _progress;
    private readonly List<GdreTask> _tasks = [];
    private string _drawn = string.Empty;
    private string? _partial;

    public GdreOutputRenderer(bool tty)
    {
        _tty = tty;
    }

    public void Segment(string raw)
    {
        lock (_gate)
        {
            SegmentLocked(raw);
        }
    }

    private void SegmentLocked(string raw)
    {
        string text = StripAnsi(raw).TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_partial is not null)
        {
            string joined = _partial + text;
            _partial = null;
            if (IsProgress(joined))
            {
                text = joined;
            }
        }

        if (!IsProgress(text))
        {
            (string head, string tail)? split = SplitBarPrefix(text, _progress);
            if (split is not null)
            {
                _partial = split.Value.head;
                Line(split.Value.tail);
                return;
            }
        }

        (string head, string tail)? glued = SplitGlued(text);
        if (glued is not null)
        {
            _partial = glued.Value.head;
            Line(glued.Value.tail);
            return;
        }

        if (IsProgress(text))
        {
            if (!_tty || _progress == text)
            {
                return;
            }

            _progress = text;
            if (ParseProgress(text) is { } parsed)
            {
                UpdateTask(parsed.Name, parsed.Percent);
            }

            DrawProgress();
        }
        else
        {
            Line(text);
        }
    }

    private void Line(string text)
    {
        if (IsBareVersion(text))
        {
            return;
        }

        lock (_gate)
        {
            bool shown = _tty && _drawn.Length > 0;
            if (shown)
            {
                Console.Error.Write("\r\x1b[2K");
            }

            Console.Error.WriteLine(text);

            if (shown)
            {
                _drawn = string.Empty;
                DrawProgressLocked();
            }
        }
    }

    private void UpdateTask(string name, int percent)
    {
        DateTime now = DateTime.UtcNow;
        _tasks.RemoveAll(task => task.Name != name && now - task.Seen >= TaskStale);

        GdreTask? existing = _tasks.FirstOrDefault(task => task.Name == name);
        if (existing is not null)
        {
            existing.Percent = percent;
            existing.Seen = now;
        }
        else
        {
            _tasks.Add(new GdreTask { Name = name, Percent = percent, Seen = now });
        }
    }

    private void DrawProgress()
    {
        lock (_gate)
        {
            DrawProgressLocked();
        }
    }

    private void DrawProgressLocked()
    {
        if (_tasks.Count == 0)
        {
            return;
        }

        int width = Math.Max(10, TerminalWidth() - 1);
        string line = Compose(_tasks, width);
        if (line == _drawn)
        {
            return;
        }

        Console.Error.Write("\r\x1b[2K" + line);
        Console.Error.Flush();
        _drawn = line;
    }

    public void Finish()
    {
        lock (_gate)
        {
            _progress = null;
            _tasks.Clear();
            if (_tty && _drawn.Length > 0)
            {
                Console.Error.Write("\r\x1b[2K");
                Console.Error.Flush();
                _drawn = string.Empty;
            }
        }
    }

    /// <summary><c>Decompiling 10.0.114</c> → the bar prefix and the intruding text.</summary>
    private static (string head, string tail)? SplitBarPrefix(string text, string? progress)
    {
        if (progress is null)
        {
            return null;
        }

        int cut = 0;
        int count = Math.Min(text.Length, progress.Length);
        while (cut < count && text[cut] == progress[cut])
        {
            cut++;
        }

        if (cut == 0 || cut == text.Length)
        {
            return null;
        }

        string head = text[..cut];
        string tail = text[cut..];
        return IsBareVersion(tail) ? (head, tail) : null;
    }

    /// <summary><c>Doing something... [=====9.0.121</c> → bar and intruding text.</summary>
    private static (string head, string tail)? SplitGlued(string text)
    {
        int marker = text.IndexOf("... [", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        int start = marker + 5;
        int end = start;
        while (end < text.Length && (text[end] == '=' || text[end] == ' '))
        {
            end++;
        }

        string rest = text[end..];
        if (rest.Length == 0 || rest.StartsWith(']'))
        {
            return null;
        }

        return (text[..end], rest);
    }

    private static bool IsBareVersion(string text)
    {
        string trimmed = text.Trim();
        string[] parts = trimmed.Split('.');
        return parts.Length >= 2 && parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static (string Name, int Percent)? ParseProgress(string text)
    {
        string trimmed = text.Trim();
        int marker = trimmed.IndexOf("... [", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        string name = trimmed[..marker].Trim();
        string withoutPercent = trimmed.TrimEnd('%');
        int end = withoutPercent.Length;
        while (end > 0 && char.IsAsciiDigit(withoutPercent[end - 1]))
        {
            end--;
        }

        return int.TryParse(withoutPercent[end..], out int percent) ? (name, percent) : null;
    }

    private static bool IsProgress(string text)
    {
        string trimmed = text.Trim();
        return trimmed.EndsWith('%') && trimmed.Contains("... [") && trimmed.Contains(']');
    }

    private static string Compose(List<GdreTask> tasks, int width)
    {
        static string Shorten(string name)
        {
            const int Max = 28;
            return name.Length <= Max ? name : name[..(Max - 1)] + "…";
        }

        string Render(int bar)
        {
            return string.Join(" │ ", tasks.Select(task =>
            {
                string name = Shorten(task.Name);
                if (bar == 0)
                {
                    return $"{name} {task.Percent}%";
                }

                int filled = Math.Min(100, task.Percent) * bar / 100;
                return $"{name} [{new string('=', filled)}{new string(' ', bar - filled)}] {task.Percent}%";
            }));
        }

        foreach (int bar in new[] { 30, 20, 12, 8, 0 })
        {
            string line = Render(bar);
            if (line.Length <= width)
            {
                return line;
            }
        }

        string fallback = Render(0);
        return fallback.Length <= width ? fallback : fallback[..width];
    }

    private static int TerminalWidth()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Math.Max(20, Console.WindowWidth);
            }
            catch
            {
                return 80;
            }
        }

        string? columns = Environment.GetEnvironmentVariable("COLUMNS");
        return int.TryParse(columns, out int width) && width > 0 ? width : 80;
    }

    /// <summary>Removes ANSI escape sequences (<c>dotnet</c> emits some even through a pipe).</summary>
    internal static string StripAnsi(string value)
    {
        var output = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\x1b')
            {
                if (i + 1 < value.Length && value[i + 1] == '[')
                {
                    i += 2;
                    while (i < value.Length && (value[i] < '@' || value[i] > '~'))
                    {
                        i++;
                    }
                }
                else if (i + 1 < value.Length)
                {
                    i++;
                }

                continue;
            }

            if (c != '\a')
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }
}