using System.Text;
using UnGodot.Core.Logging;
using UnGodot.Core.Net;

namespace UnGodot.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch
        {
            // Some consoles refuse the change; the default encoding still works.
        }

        if (!CommandLine.TryParse(args, out CliOptions options, out string? error))
        {
            Console.Error.WriteLine($"ungodot: {error}");
            Console.Error.WriteLine("Run `ungodot help` for usage.");
            return 2;
        }

        if (options.Help)
        {
            Console.Out.WriteLine(CommandLine.Usage);
            return 0;
        }

        ConfigureLogging(options);
        Log.Debug($"ungodot {typeof(Program).Assembly.GetName().Version} on {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            if (cancellation.IsCancellationRequested)
            {
                // Second Ctrl+C: let the runtime terminate us.
                return;
            }

            eventArgs.Cancel = true;
            Log.Warn("cancelling, press Ctrl+C again to force");
            cancellation.Cancel();
        };

        Console.CancelKeyPress += handler;

        try
        {
            return await Commands.RunAsync(options, cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log.Warn("cancelled");
            return 130;
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"ungodot: {ex.Message}");
            Console.Error.WriteLine("Run `ungodot help` for usage.");
            return 2;
        }
        catch (Exception ex)
        {
            Log.Critical("unexpected failure", ex);
            Console.Error.WriteLine($"ungodot: {ex.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            Log.Shutdown();
        }
    }

    private static void ConfigureLogging(CliOptions options)
    {
        LogLevel level = options.Quiet
            ? LogLevel.Warning
            : options.Verbose
                ? LogLevel.Debug
                : options.Level;

        string? file = options.LogFile;
        if (file is null && !options.NoLogFile)
        {
            file = Path.Combine(Http.DataDirectory, "logs", "ungodot.log");
        }

        Log.Initialize(new LogOptions
        {
            MinimumLevel = level,
            Console = true,
            FilePath = file,
            // Trace is configured in code (no app.config), and the only console sink is the
            // one installed here: a message is never printed twice.
            Colors = !options.NoColor,
        });
    }
}