using UnGodot.Core.Logging;
using UnGodot.Dump;

namespace UnGodot.Cli;

internal static partial class Commands
{
    /// <summary>
    /// Carves decrypted resources, scripts, textures and project settings out of a running
    /// game or a minidump. The point is the pack key or the EXE protection: whatever the game
    /// has already decrypted is in RAM, and the parsers decide what is real.
    /// </summary>
    private static int DumpCommand(CliOptions options, CancellationToken cancellationToken)
    {
        string engine = options.EngineVersion ?? "3";
        int engineMajor = int.TryParse(engine.Split('.')[0], out int major) ? major : 3;
        string output = options.Out ?? Path.Combine(
            Environment.CurrentDirectory,
            (options.ProcessName
                ?? (options.FromDump is null ? "memory" : Path.GetFileNameWithoutExtension(options.FromDump))) + "_dump");

        try
        {
            if (options.FromDump is not null)
            {
                if (!File.Exists(options.FromDump))
                {
                    Console.Error.WriteLine($"ungodot: '{options.FromDump}' does not exist");
                    return 1;
                }

                using var source = new FileMemorySource(options.FromDump);
                DumpReport report = MemoryDump.Run(source, output, engineMajor, cancellationToken);
                PrintDumpReport(source.DisplayName, output, report);
                return report.Artifacts.Values.Sum() > 0 ? 0 : 4;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("ungodot: reading a live process needs Windows; use --from-dump on this platform");
                return 1;
            }

            using ProcessMemorySource process = options.Pid is { } pid
                ? ProcessMemorySource.Open(pid)
                : ProcessMemorySource.Open(RequireProcessName(options));

            Log.Info($"attached to {process.DisplayName} (pid {process.Pid})");

            if (options.MiniDump is not null)
            {
                Log.Info($"writing a full memory dump to {options.MiniDump}");
                process.WriteMiniDump(options.MiniDump);
                Log.Info($"dump written: {new FileInfo(options.MiniDump).Length / (1024 * 1024)} MB");
            }

            DumpReport result = MemoryDump.Run(process, output, engineMajor, cancellationToken);
            PrintDumpReport(process.DisplayName, output, result);
            return result.Artifacts.Values.Sum() > 0 ? 0 : 4;
        }
        catch (Exception ex) when (ex is DumpException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"ungodot: {ex.Message}");
            return 1;
        }
    }

    private static string RequireProcessName(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProcessName))
        {
            throw new DumpException("pass --name <process> or --pid <id> (or --from-dump <file>)");
        }

        return options.ProcessName;
    }

    private static void PrintDumpReport(string sourceName, string output, DumpReport report)
    {
        Console.Out.WriteLine($"source           {sourceName}");
        Console.Out.WriteLine($"output           {output}");
        Console.Out.WriteLine(
            $"scanned          {report.BytesScanned / (1024 * 1024)} MB in {report.Elapsed.TotalSeconds:0.0}s");

        if (report.Hits.Count > 0)
        {
            string hits = string.Join(", ", report.Hits.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));
            Console.Out.WriteLine($"hits             {hits}");
        }

        if (report.Artifacts.Count > 0)
        {
            string artifacts = string.Join(", ", report.Artifacts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Value} {pair.Key}"));
            Console.Out.WriteLine($"artifacts        {artifacts}");
        }
        else
        {
            Console.Out.WriteLine("artifacts        none (nothing in memory passed the parsers)");
        }

        if (report.Rejected > 0)
        {
            Console.Out.WriteLine($"rejected         {report.Rejected} magic-number candidate(s)");
        }

        foreach (string note in report.Notes.Take(10))
        {
            Log.Debug(note);
        }
    }
}