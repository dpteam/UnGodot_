using System.Text;
using UnGodot.Core.Logging;

namespace UnGodot.Cli;

internal sealed class CliOptions
{
    public string Command { get; set; } = string.Empty;
    public List<string> Positionals { get; } = [];
    public string? Key { get; set; }
    public string? Out { get; set; }
    public string? Gdre { get; set; }
    public List<string> Scan { get; } = [];
    public string? Wordlist { get; set; }
    public LogLevel Level { get; set; } = LogLevel.Info;
    public bool Quiet { get; set; }
    public bool Verbose { get; set; }
    public bool NoLogFile { get; set; }
    public string? LogFile { get; set; }
    public bool Json { get; set; }
    public bool NoColor { get; set; }
    public bool NoRecursive { get; set; }
    public bool DryRun { get; set; }
    public bool NoPlugins { get; set; }
    public bool All { get; set; }
    public bool NoMerge { get; set; }
    public bool NoOverwrite { get; set; }
    public bool NoVerify { get; set; }
    public bool NoManifest { get; set; }
    public string? Filter { get; set; }
    public int? Jobs { get; set; }
    public string? GodotVersion { get; set; }
    public string? EngineVersion { get; set; }
    public bool Spaces { get; set; }
    public int TabSize { get; set; } = 4;
    public bool NoScripts { get; set; }
    public bool KeepGdc { get; set; }
    public bool NoTextures { get; set; }

    /// <summary>Skip binary resource/scene conversion on <c>restore</c>.</summary>
    public bool NoResources { get; set; }

    /// <summary>Move converted binary originals to <c>.autoconverted/</c> on <c>restore</c>.</summary>
    public bool ArchiveBinary { get; set; }

    /// <summary>dump: process id to read.</summary>
    public int? Pid { get; set; }

    /// <summary>dump: process name to read.</summary>
    public string? ProcessName { get; set; }

    /// <summary>dump: carve this minidump/memory image instead of a live process.</summary>
    public string? FromDump { get; set; }

    /// <summary>dump: also write a full-memory minidump before carving.</summary>
    public string? MiniDump { get; set; }
    public bool? Mono { get; set; }
    public bool Templates { get; set; }
    public bool NoLaunch { get; set; }
    public bool Help { get; set; }
    public string? GameDirectory { get; set; }
    public string? SetupDirectory { get; set; }
}

internal static class CommandLine
{
    public const string Usage = """
        ungodot - Godot reverse engineering toolkit (C# port of gore)

        Usage:
          ungodot <command> [options] [path]
          ungodot <path>                (same as: ungodot detect <path>)

        Commands:
          detect   <path>     Find every Godot pack around a file or folder
          info     <path>     Show the pack header and its file table
          key      <path>     Recover the pack encryption key (prints it in hex)
          extract  <path>     Extract the contents (all packs found, merged)
          recover  <path>     Recover a full Godot project with GDRE Tools
          decompile <path>    Decompile .gdc bytecode to GDScript source (native)
          restore  <path>     Native recovery pipeline: extract, project.godot, scripts
          dump     [--name N] Carve resources, scripts and textures out of a running game (or --from-dump)
          plugins  <project>  Restore the native GDExtension libraries
          editor   [project]  Install and/or open the Godot editor the project needs
          setup               Download GDRE Tools
          selftest            Check the crypto and pack code on synthetic data
          help                Show this help

        Options:
          -k, --key <HEX>       Pack encryption key, 64 hex characters
          -o, --out <DIR>       Output directory
              --gdre <PATH>     GDRE Tools binary
              --scan <PATH>     Extra file to scan for the key (repeatable)
              --wordlist <FILE> Passwords to try when no key is embedded
              --filter <GLOB>   Only extract matching paths (e.g. "*.gd;*.tscn")
              --jobs <N>        Parallel workers (default: CPU count)
              --[no-]overwrite  Overwrite existing files (default: yes)
              --[no-]verify     Verify MD5 checksums (default: yes)
              --[no-]recursive  Scan folders recursively (default: yes)
              --[no-]manifest   Write ungodot_manifest.txt (default: yes)
              --all             Extract/list every pack, not only the one targeted
              --no-merge        recover: do not merge the packs found around the input
              --json            Machine readable output for detect/info
          -v, --verbose         Debug logging
          -q, --quiet           Only warnings and errors
              --log-level <L>   trace|debug|info|warn|error|off
              --log-file <PATH> Log file (default: <data>/logs/ungodot.log)
              --no-log-file     Do not write a log file
              --no-color        Disable ANSI colours
              --dry-run         recover: print the GDRE command without running it
              --no-plugins      recover: skip restoring GDExtension libraries
              --engine <VERSION> engine version hint (e.g. 3.1.2)
              --no-scripts      restore: skip script decompilation
              --keep-gdc        restore: keep compiled scripts next to the .gd
              --no-textures     restore: skip .stex -> PNG conversion
              --no-resources    restore: skip binary resource/scene -> text conversion
              --archive-binary  restore: move converted binaries to .autoconverted/
              --name <PROC>     dump: process name to read from memory
              --pid <ID>        dump: process id (instead of --name)
              --from-dump <F>   dump: carve a minidump/raw image instead of a live process
              --minidump <F>    dump: also write a full memory dump before carving
              --spaces          decompile with spaces instead of tabs
              --tab-size <N>    decompile indentation width (default 4)
              --godot <VERSION> editor: version to use instead of the detected one
              --mono/--no-mono  editor: force the .NET build or the regular one
              --templates       editor: also install the export templates
              --no-launch       editor: install only, print the editor path
              --game <DIR>      plugins: original game folder to copy libraries from
              --dir <DIR>       setup: installation directory
          -h, --help            Show this help

        Environment:
          UNGODOT_HOME  Where downloads and logs are kept (default: %LOCALAPPDATA%\\ungodot)
        """;

    public static bool TryParse(string[] args, out CliOptions options, out string? error)
    {
        options = new CliOptions();
        error = null;
        string? valueError = null;

        var commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "detect", "scan", "info", "key", "extract", "recover", "decompile", "restore", "dump", "plugins", "editor", "setup", "selftest", "help",
        };

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];

            if (arg is "-h" or "--help" or "-?")
            {
                options.Help = true;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                string name = arg;
                string? inline = null;
                int equals = arg.IndexOf('=');
                if (equals > 0)
                {
                    name = arg[..equals];
                    inline = arg[(equals + 1)..];
                }

                string? Value()
                {
                    if (inline is not null)
                    {
                        return inline;
                    }

                    if (i + 1 >= args.Length)
                    {
                        valueError = $"option {name} needs a value";
                        return null;
                    }

                    return args[++i];
                }

                switch (name)
                {
                    case "-k":
                    case "--key":
                        options.Key = Value();
                        break;
                    case "-o":
                    case "--out":
                        options.Out = Value();
                        break;
                    case "--gdre":
                        options.Gdre = Value();
                        break;
                    case "--scan":
                        options.Scan.Add(Value() ?? string.Empty);
                        break;
                    case "--wordlist":
                        options.Wordlist = Value();
                        break;
                    case "--filter":
                        options.Filter = Value();
                        break;
                    case "--jobs":
                        if (int.TryParse(Value(), out int jobs) && jobs > 0)
                        {
                            options.Jobs = jobs;
                        }
                        else
                        {
                            error = "--jobs needs a positive number";
                        }

                        break;
                    case "-v":
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "-q":
                    case "--quiet":
                        options.Quiet = true;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    case "--log-file":
                        options.LogFile = Value();
                        break;
                    case "--no-log-file":
                        options.NoLogFile = true;
                        break;
                    case "--log-level":
                        if (TryParseLevel(Value(), out LogLevel level))
                        {
                            options.Level = level;
                        }
                        else
                        {
                            error = "--log-level expects trace|debug|info|warn|error|off";
                        }

                        break;
                    case "--no-color":
                        options.NoColor = true;
                        break;
                    case "--no-recursive":
                        options.NoRecursive = true;
                        break;
                    case "--recursive":
                        options.NoRecursive = false;
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--no-plugins":
                        options.NoPlugins = true;
                        break;
                    case "--all":
                        options.All = true;
                        break;
                    case "--no-merge":
                        options.NoMerge = true;
                        break;
                    case "--overwrite":
                        options.NoOverwrite = false;
                        break;
                    case "--no-overwrite":
                        options.NoOverwrite = true;
                        break;
                    case "--verify":
                        options.NoVerify = false;
                        break;
                    case "--no-verify":
                        options.NoVerify = true;
                        break;
                    case "--manifest":
                        options.NoManifest = false;
                        break;
                    case "--no-manifest":
                        options.NoManifest = true;
                        break;
                    case "--godot":
                        options.GodotVersion = Value();
                        break;
                    case "--engine":
                        options.EngineVersion = Value();
                        break;
                    case "--no-scripts":
                        options.NoScripts = true;
                        break;
                    case "--no-textures":
                        options.NoTextures = true;
                        break;
                    case "--no-resources":
                        options.NoResources = true;
                        break;
                    case "--archive-binary":
                        options.ArchiveBinary = true;
                        break;
                    case "--pid":
                        if (int.TryParse(Value(), out int pid) && pid > 0)
                        {
                            options.Pid = pid;
                        }
                        else
                        {
                            error = "--pid needs a process id";
                        }

                        break;
                    case "--name":
                        options.ProcessName = Value();
                        break;
                    case "--from-dump":
                        options.FromDump = Value();
                        break;
                    case "--minidump":
                        options.MiniDump = Value();
                        break;
                    case "--keep-gdc":
                        options.KeepGdc = true;
                        break;
                    case "--spaces":
                        options.Spaces = true;
                        break;
                    case "--tab-size":
                        if (int.TryParse(Value(), out int tabSize) && tabSize > 0)
                        {
                            options.TabSize = tabSize;
                        }
                        else
                        {
                            error = "--tab-size needs a positive number";
                        }

                        break;
                    case "--mono":
                        options.Mono = true;
                        break;
                    case "--no-mono":
                        options.Mono = false;
                        break;
                    case "--templates":
                        options.Templates = true;
                        break;
                    case "--no-launch":
                        options.NoLaunch = true;
                        break;
                    case "--game":
                        options.GameDirectory = Value();
                        break;
                    case "--dir":
                        options.SetupDirectory = Value();
                        break;
                    default:
                        error = $"unknown option {name}";
                        return false;
                }

                if (valueError is not null)
                {
                    error = valueError;
                    return false;
                }

                if (error is not null)
                {
                    return false;
                }

                continue;
            }

            if (options.Command.Length == 0 && commands.Contains(arg))
            {
                options.Command = arg.ToLowerInvariant();
            }
            else if (options.Command.Length == 0 && (File.Exists(arg) || Directory.Exists(arg) ||
                                                    arg.Contains(Path.DirectorySeparatorChar) || arg.Contains('/')))
            {
                options.Command = "detect";
                options.Positionals.Add(arg);
            }
            else
            {
                options.Positionals.Add(arg);
            }
        }

        if (options.Command.Length == 0 && options.Positionals.Count > 0)
        {
            options.Command = "detect";
        }

        if (options.Command.Length == 0)
        {
            options.Help = true;
        }

        return true;
    }

    private static bool TryParseLevel(string? value, out LogLevel level)
    {
        switch (value?.ToLowerInvariant())
        {
            case "trace":
                level = LogLevel.Trace;
                return true;
            case "debug":
                level = LogLevel.Debug;
                return true;
            case "info":
                level = LogLevel.Info;
                return true;
            case "warn":
            case "warning":
                level = LogLevel.Warning;
                return true;
            case "error":
                level = LogLevel.Error;
                return true;
            case "off":
            case "none":
                level = LogLevel.Off;
                return true;
            default:
                level = LogLevel.Info;
                return false;
        }
    }
}