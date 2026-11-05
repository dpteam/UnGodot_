using System.Diagnostics;
using System.Text.Json;
using UnGodot.Core.Binfmt;
using UnGodot.Core.Crypto;
using UnGodot.Core.Discovery;
using UnGodot.Core.Extract;
using UnGodot.Core.Gdre;
using UnGodot.Core.Godot;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Net;
using UnGodot.Core.Pck;
using UnGodot.Core.Plugins;
using UnGodot.Core.Workspace;
using UnGodot.GDScript;
using PckFile = UnGodot.Core.Pck.Pck;

namespace UnGodot.Cli;

internal static partial class Commands
{
    public static async Task<int> RunAsync(CliOptions options, CancellationToken cancellationToken)
    {
        return options.Command switch
        {
            "detect" or "scan" => Detect(options),
            "info" => Info(options),
            "key" => Key(options),
            "extract" => Extract(options, cancellationToken),
            "recover" => await RecoverAsync(options, cancellationToken).ConfigureAwait(false),
            "decompile" => DecompileCommand.Run(options, cancellationToken),
            "restore" => Restore(options, cancellationToken),
            "dump" => DumpCommand(options, cancellationToken),
            "plugins" => Plugins(options, cancellationToken),
            "editor" => await EditorAsync(options, cancellationToken).ConfigureAwait(false),
            "setup" => await SetupAsync(options, cancellationToken).ConfigureAwait(false),
            "selftest" => SelfTest(),
            "help" => Help(),
            _ => Unknown(options.Command),
        };
    }

    private static int Help()
    {
        Console.Out.WriteLine(CommandLine.Usage);
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"ungodot: unknown command '{command}'");
        Console.Error.WriteLine("Run `ungodot help` for the list of commands.");
        return 2;
    }

    private static string RequirePath(CliOptions options)
    {
        if (options.Positionals.Count == 0 || string.IsNullOrWhiteSpace(options.Positionals[0]))
        {
            throw new UsageException("this command needs a path (a .pck, a game executable or a folder)");
        }

        return options.Positionals[0];
    }

    private static WorkspaceOptions WorkspaceFor(CliOptions options)
    {
        byte[]? key = null;
        if (!string.IsNullOrWhiteSpace(options.Key))
        {
            if (!GodotCrypto.TryParseHexKey(options.Key, out key))
            {
                throw new UsageException("--key must be 64 hex characters");
            }
        }

        return new WorkspaceOptions
        {
            Discovery = new DiscoveryOptions { Recursive = !options.NoRecursive },
            Key = key,
            ExtraKeySources = options.Scan,
            Wordlist = options.Wordlist,
        };
    }

    // -------------------------------------------------------------------------------------
    // detect
    // -------------------------------------------------------------------------------------

    private static int Detect(CliOptions options)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));
        DiscoveryResult discovery = workspace.Discover(input);

        if (options.Json)
        {
            var payload = new
            {
                input = discovery.Input,
                inputIsDirectory = discovery.InputIsDirectory,
                packs = discovery.Candidates.Where(c => c.IsUsablePack).Select(DescribeJson),
                notes = discovery.Notes,
                inspected = discovery.Inspected,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return discovery.HasPacks ? 0 : 3;
        }

        Console.Out.WriteLine($"input            {discovery.Input}{(discovery.InputIsDirectory ? " (directory)" : string.Empty)}");
        List<PackCandidate> candidates = discovery.Candidates.Where(c => c.IsUsablePack).ToList();
        Console.Out.WriteLine($"packs found      {candidates.Count}");

        string? root = discovery.InputIsDirectory
            ? discovery.Input
            : Path.GetDirectoryName(discovery.Input);

        int index = 0;
        foreach (PackCandidate candidate in candidates.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            index++;
            string label = Compact(root, candidate.Path);
            if (candidate.ZipEntry is not null)
            {
                label += ":" + candidate.ZipEntry;
            }

            string files = candidate.FileCount?.ToString() ?? "?";
            string tags = candidate.HasProjectSettings ? " [base pack]" : string.Empty;
            if (candidate.Sparse)
            {
                tags += " [sparse bundle]";
            }

            Console.Out.WriteLine($"  {index,2}. {label}");
            Console.Out.WriteLine($"      {candidate.Kind.Describe()}, {candidate.PackLabel}, {files} files, {PathUtil.HumanSize(candidate.Length)}{tags}");
            if (candidate.Error is not null && candidate.Encrypted)
            {
                Console.Out.WriteLine($"      note: {candidate.Error}");
            }
        }

        foreach (string note in discovery.Notes)
        {
            Log.Info(note);
        }

        if (options.Verbose)
        {
            Console.Out.WriteLine("inspected:");
            foreach (string inspected in discovery.Inspected)
            {
                Console.Out.WriteLine($"  {Compact(root, inspected)}");
            }
        }

        if (!discovery.HasPacks)
        {
            Log.Warn("no Godot pack was found around this input");
            return 3;
        }

        PackCandidate? primary = discovery.Target;
        if (primary is not null)
        {
            Console.Out.WriteLine($"primary          {Compact(root, primary.Path)} ({primary.PackLabel})");
        }

        return 0;
    }

    private static object DescribeJson(PackCandidate candidate) => new
    {
        path = candidate.Path,
        kind = candidate.Kind.ToString(),
        packOffset = candidate.PackOffset,
        formatVersion = candidate.Header?.FormatVersion,
        engine = candidate.Header?.EngineVersion,
        encrypted = candidate.Encrypted,
        sparse = candidate.Sparse,
        files = candidate.FileCount,
        hasProjectSettings = candidate.HasProjectSettings,
        scripts = candidate.ScriptCount,
        zipEntry = candidate.ZipEntry,
        length = candidate.Length,
        error = candidate.Error,
    };

    // -------------------------------------------------------------------------------------
    // info
    // -------------------------------------------------------------------------------------

    private static int Info(CliOptions options)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));
        DiscoveryResult discovery = workspace.Discover(input);
        if (!discovery.HasPacks)
        {
            foreach (string note in discovery.Notes)
            {
                Log.Warn(note);
            }

            Console.Error.WriteLine("ungodot: no Godot pack found");
            return 3;
        }

        List<PackCandidate> targets = options.All
            ? discovery.Candidates.Where(c => c.IsUsablePack).ToList()
            : [discovery.Target!];

        int exitCode = 0;
        bool first = true;
        foreach (PackCandidate candidate in targets)
        {
            OpenedPack opened;
            try
            {
                opened = workspace.Open(candidate);
            }
            catch (Exception ex) when (ex is IOException or PckException)
            {
                Log.Error($"{candidate.Path}: {ex.Message}");
                exitCode = 1;
                continue;
            }

            using (opened)
            {
                if (!first)
                {
                    Console.Out.WriteLine();
                }

                first = false;
                PrintInfo(opened, candidate, options);
                if (opened.Pack is null)
                {
                    exitCode = exitCode == 0 ? 1 : exitCode;
                }
            }
        }

        return exitCode;
    }

    private static void PrintInfo(OpenedPack opened, PackCandidate candidate, CliOptions options)
    {
        PckHeader header = opened.Header;
        if (options.Json)
        {
            var payload = new
            {
                file = candidate.Path,
                zipEntry = candidate.ZipEntry,
                packOffset = header.Start,
                formatVersion = header.FormatVersion,
                engine = header.EngineVersion,
                flags = header.Flags,
                dirEncrypted = header.DirEncrypted,
                relFileBase = header.RelFileBase,
                fileBase = header.FileBase,
                dirOffset = header.DirOffset,
                key = opened.Key is null ? null : GodotCrypto.ToHex(opened.Key),
                files = opened.Pack?.Entries.Count,
                encryptedFiles = opened.Pack?.Entries.Count(e => e.Encrypted),
                removedFiles = opened.Pack?.Entries.Count(e => e.Removed),
                scripts = opened.Pack?.Entries.Count(e => e.Path.EndsWith(".gd") || e.Path.EndsWith(".gdc") || e.Path.EndsWith(".cs")),
                firstEntry = opened.Pack?.Entries.FirstOrDefault()?.Path,
                lastEntry = opened.Pack?.Entries.LastOrDefault()?.Path,
                error = opened.Error,
            };
            Console.Out.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
            return;
        }

        Console.Out.WriteLine($"file             {candidate.Path}{(candidate.ZipEntry is null ? string.Empty : ":" + candidate.ZipEntry)}");
        Console.Out.WriteLine($"size             {PathUtil.HumanSize(opened.Source.Length)} ({candidate.Kind.Describe()})");
        Console.Out.WriteLine($"pack offset      {header.Start}");
        Console.Out.WriteLine($"pack version     v{header.FormatVersion}");
        Console.Out.WriteLine($"engine version   {header.EngineVersion}");
        Console.Out.WriteLine($"flags            0x{header.Flags:x4}");
        Console.Out.WriteLine($"  dir encrypted  {header.DirEncrypted}");
        Console.Out.WriteLine($"  rel file base  {header.RelFileBase}");
        Console.Out.WriteLine($"file base        {header.FileBase}");
        Console.Out.WriteLine($"dir offset       {header.DirOffset}");

        if (opened.Key is not null)
        {
            Console.Out.WriteLine($"key              {GodotCrypto.ToHex(opened.Key)}");
        }
        else if (header.DirEncrypted)
        {
            Console.Out.WriteLine("key              <unknown>");
        }

        if (opened.Pack is { } pack)
        {
            int encrypted = pack.Entries.Count(entry => entry.Encrypted);
            int removed = pack.Entries.Count(entry => entry.Removed);
            Console.Out.WriteLine($"files            {pack.Entries.Count}");
            if (encrypted > 0)
            {
                Console.Out.WriteLine($"  encrypted      {encrypted}");
            }

            if (removed > 0)
            {
                Console.Out.WriteLine($"  removed        {removed}");
            }

            int scripts = pack.Entries.Count(entry =>
                entry.Path.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase) ||
                entry.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            if (scripts > 0)
            {
                Console.Out.WriteLine($"  scripts        {scripts}");
            }

            if (pack.Entries.Count > 0)
            {
                Console.Out.WriteLine($"first entry      {pack.Entries[0].Path}");
                Console.Out.WriteLine($"last entry       {pack.Entries[^1].Path}");
            }

            if (pack.Entries.Any(entry => entry.Path is "res://project.binary" or "res://project.godot"))
            {
                Console.Out.WriteLine("project settings yes");
            }
        }
        else if (opened.Error is not null)
        {
            Console.Out.WriteLine($"error            {opened.Error}");
        }
    }

    // -------------------------------------------------------------------------------------
    // key
    // -------------------------------------------------------------------------------------

    private static int Key(CliOptions options)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));
        DiscoveryResult discovery = workspace.Discover(input);
        PackCandidate? candidate = discovery.Target;
        if (candidate is null)
        {
            Console.Error.WriteLine("ungodot: no Godot pack found");
            return 3;
        }

        using OpenedPack opened = workspace.Open(candidate);
        if (!opened.Header.DirEncrypted)
        {
            Console.Out.WriteLine("the pack directory is not encrypted, no key needed");
            return 0;
        }

        if (opened.Key is null)
        {
            Console.Error.WriteLine("ungodot: could not recover the encryption key automatically, provide it with --key");
            return 1;
        }

        Console.Out.WriteLine(GodotCrypto.ToHex(opened.Key));
        return 0;
    }

    // -------------------------------------------------------------------------------------
    // extract
    // -------------------------------------------------------------------------------------

    private static int Extract(CliOptions options, CancellationToken cancellationToken)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));
        DiscoveryResult discovery = workspace.Discover(input);

        if (!discovery.HasPacks)
        {
            foreach (string note in discovery.Notes)
            {
                Log.Warn(note);
            }

            Console.Error.WriteLine("ungodot: no Godot pack found");
            return 3;
        }

        string output = options.Out ?? DefaultOutput(discovery, "extracted");
        var extractOptions = new ExtractOptions
        {
            Overwrite = !options.NoOverwrite,
            VerifyMd5 = !options.NoVerify,
            Filter = options.Filter,
            MaxDegree = options.Jobs,
            WriteManifest = !options.NoManifest,
            ContinueOnError = true,
        };

        int packCount = discovery.Candidates.Count(c => c.IsUsablePack);

        // A single named pack is extracted alone; a folder (or --all) merges every pack it
        // contains, base pack last, the way the game itself mounts them.
        IEnumerable<PackCandidate> selection = discovery.InputIsDirectory || options.All
            ? discovery.Candidates.Where(c => c.IsUsablePack)
            : [discovery.Target!];

        if (selection.Count() > 1)
        {
            Log.Info($"{packCount} pack(s) around this input: extracting the whole set into one directory (base pack last)");
        }

        GameWorkspace.MultiExtractResult result = workspace.ExtractAll(discovery, output, extractOptions, selection, cancellationToken);

        foreach ((PackCandidate candidate, ExtractStats stats) in result.PerPack)
        {
            Console.Out.WriteLine($"{Path.GetFileName(candidate.Path)}: {stats}");
        }

        foreach (string failure in result.Failures)
        {
            Log.Error(failure);
        }

        Console.Out.WriteLine(
            $"total            {result.TotalWritten} written, {result.TotalDecrypted} decrypted, " +
            $"{result.TotalSkipped} skipped, {result.TotalFailed} failed " +
            $"({result.OverwrittenEntries} overwritten entries) in {result.Elapsed.TotalSeconds:0.0}s");

        if (result.PerPack.Count == 0)
        {
            return 1;
        }

        return result.Failures.Count > 0 ? 1 : 0;
    }

    private static string DefaultOutput(DiscoveryResult discovery, string suffix)
    {
        string name = discovery.InputIsDirectory
            ? new DirectoryInfo(discovery.Input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name
            : Path.GetFileNameWithoutExtension(discovery.Input);

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "godot_game";
        }

        return Path.Combine(Environment.CurrentDirectory, $"{name}_{suffix}");
    }

    // -------------------------------------------------------------------------------------
    // recover
    // -------------------------------------------------------------------------------------

    private static async Task<int> RecoverAsync(CliOptions options, CancellationToken cancellationToken)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));

        string? gdre = GdreTools.Locate(options.Gdre);
        if (gdre is null)
        {
            Console.Error.WriteLine("ungodot: GDRE Tools was not found. Install it with `ungodot setup` or point at it with --gdre.");
            return 1;
        }

        string target;
        byte[]? key = null;
        DiscoveryResult? discovery = null;

        if (Directory.Exists(input))
        {
            target = input;
        }
        else
        {
            discovery = workspace.Discover(input);
            PackCandidate? candidate = discovery.Target;
            if (candidate is null)
            {
                foreach (string note in discovery.Notes)
                {
                    Log.Warn(note);
                }

                Console.Error.WriteLine("ungodot: no Godot pack found");
                return 3;
            }

            using OpenedPack opened = workspace.Open(candidate, cancellationToken);
            if (opened.Pack is null)
            {
                Log.Error($"cannot open {candidate.Path}: {opened.Error}");
                return 1;
            }

            key = opened.Key;
            target = candidate.Path;
        }

        string? targetDirectory = Directory.Exists(target) ? null : Path.GetDirectoryName(target);
        string output = options.Out ?? Path.Combine(
            Environment.CurrentDirectory,
            (targetDirectory is null
                ? new DirectoryInfo(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name
                : Path.GetFileNameWithoutExtension(target)) + "_recovered");

        // Several packs (a base game split into chunks, or a DLC) cannot be handed to GDRE
        // one by one: extract them into one directory first, base pack last, then recover that.
        if (discovery is not null && !options.NoMerge && discovery.Candidates.Count(c => c.IsUsablePack) > 1)
        {
            string merged = Path.Combine(Path.GetDirectoryName(output) ?? ".", Path.GetFileName(output) + "_raw");
            Log.Info($"this game ships {discovery.Candidates.Count(c => c.IsUsablePack)} packs; merging them into {merged} first");
            GameWorkspace.MultiExtractResult extract = workspace.ExtractAll(
                discovery,
                merged,
                new ExtractOptions { Overwrite = true, VerifyMd5 = !options.NoVerify, MaxDegree = options.Jobs, WriteManifest = false },
                only: null,
                cancellationToken);

            if (extract.PerPack.Count == 0)
            {
                Log.Error("could not extract any pack, cannot recover");
                return 1;
            }

            target = merged;
        }

        int gdreExit = await RunGdreAsync(gdre, target, output, key, options, cancellationToken).ConfigureAwait(false);
        if (gdreExit != 0)
        {
            return gdreExit;
        }

        if (!options.NoPlugins && !options.DryRun)
        {
            string? gameDirectory = Directory.Exists(input) ? null : Path.GetDirectoryName(input);
            RestorePlugins(output, gameDirectory, cancellationToken);
        }

        PrintEditorHint(output);
        return 0;
    }

    private static async Task<int> RunGdreAsync(
        string gdre,
        string target,
        string output,
        byte[]? key,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "--headless", $"--recover={Path.GetFullPath(target)}" };
        if (key is not null)
        {
            arguments.Add($"--key={GodotCrypto.ToHex(key)}");
        }

        arguments.Add($"--output={Path.GetFullPath(output)}");

        Log.Info($"running {gdre}");
        foreach (string argument in arguments)
        {
            Log.Info("  " + argument);
        }

        if (options.DryRun)
        {
            return 0;
        }

        try
        {
            await GdreTools.RunAsync(gdre, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GdreException or IOException)
        {
            Log.Error(ex.Message);
            return 1;
        }

        Log.Info($"project recovered into {output}");
        return 0;
    }

    // -------------------------------------------------------------------------------------
    // plugins / editor / setup / selftest
    // -------------------------------------------------------------------------------------

    private static int Plugins(CliOptions options, CancellationToken cancellationToken)
    {
        string project = options.Positionals.Count > 0 ? options.Positionals[0] : ".";
        if (!File.Exists(Path.Combine(project, "project.godot")))
        {
            Console.Error.WriteLine($"ungodot: '{project}' is not a Godot project (no project.godot)");
            return 1;
        }

        RestorePlugins(project, options.GameDirectory, cancellationToken);
        return 0;
    }

    private static void RestorePlugins(string project, string? gameDirectory, CancellationToken cancellationToken)
    {
        EditorSpec? spec = GodotEditor.Detect(project);
        string? engine = spec?.Version;
        List<Gdextension> extensions = PluginRestorer.FindExtensions(project);
        if (extensions.All(extension => extension.Missing().Count == 0))
        {
            return;
        }

        Log.Info("restoring native plugin libraries for this platform");
        foreach (PluginOutcome outcome in PluginRestorer.Restore(project, gameDirectory, engine, cancellationToken))
        {
            if (outcome.StillMissing.Count == 0)
            {
                Log.Info($"{outcome.Name}: {outcome.Restored} file(s) restored from {outcome.Source ?? "?"}");
            }
            else
            {
                Log.Warn($"{outcome.Name}: still missing {outcome.StillMissing.Count} file(s), the plugin will not load in the editor");
                foreach (string path in outcome.StillMissing.Take(5))
                {
                    string relative = Path.GetRelativePath(project, path).Replace('\\', '/');
                    Log.Warn("  res://" + relative);
                }
            }
        }
    }

    private static async Task<int> EditorAsync(CliOptions options, CancellationToken cancellationToken)
    {
        string project = options.Positionals.Count > 0 ? options.Positionals[0] : ".";
        if (!File.Exists(Path.Combine(project, "project.godot")))
        {
            Console.Error.WriteLine($"ungodot: '{project}' is not a Godot project (no project.godot)");
            return 1;
        }

        EditorSpec? detected = GodotEditor.Detect(project);
        string? version = options.GodotVersion ?? detected?.Version;
        if (version is null)
        {
            Console.Error.WriteLine("ungodot: cannot tell which Godot version this project needs, pass it with --godot");
            return 1;
        }

        bool mono = options.Mono ?? detected?.Mono ?? GodotEditor.IsMono(project);
        var spec = new EditorSpec(version, mono);

        string? installed = GodotEditor.FindSystem(spec);
        string editor = installed ?? await GodotEditor.EnsureEditorAsync(spec, cancellationToken).ConfigureAwait(false);
        Log.Info($"{spec.Label}: {editor}");

        if (options.Templates)
        {
            string templates = await GodotEditor.EnsureTemplatesAsync(spec, editor, cancellationToken).ConfigureAwait(false);
            Log.Info($"export templates: {templates}");
        }

        if (spec.Mono && !DotnetAvailable())
        {
            Log.Warn("the .NET SDK (`dotnet`) was not found; the editor needs it to build the C# scripts");
        }

        if (options.NoLaunch)
        {
            Console.Out.WriteLine(editor);
            return 0;
        }

        Log.Info($"opening {Path.GetFullPath(project)}");
        GodotEditor.StartEditor(editor, project);
        return 0;
    }

    private static bool DotnetAvailable()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("dotnet", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<int> SetupAsync(CliOptions options, CancellationToken cancellationToken)
    {
        string destination = options.SetupDirectory ?? Path.Combine(Http.DataDirectory, "gdre");
        string installed = await GdreTools.DownloadLatestAsync(destination, cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(installed);
        return 0;
    }

    private static void PrintEditorHint(string project)
    {
        EditorSpec? spec = GodotEditor.Detect(project);
        if (spec is null)
        {
            return;
        }

        Log.Info($"open it with {spec.Label}: ungodot editor \"{project}\"");
        if (spec.Mono)
        {
            Log.Info("(C# project: the regular Godot build cannot load .cs scripts, it must be the .NET build)");
        }
    }

    private static int SelfTest()
    {
        Console.Out.WriteLine($"ungodot self-test on .NET {Environment.Version}, {Environment.ProcessorCount} cores");
        Console.Out.WriteLine($"AES-NI           {Aes256Schedule.IsHardwareAccelerated}");

        // FIPS-197 C.3
        byte[] key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        byte[] plain = Convert.FromHexString("00112233445566778899aabbccddeeff");
        byte[] expected = Convert.FromHexString("8ea2b7ca516745bfeafc49904b496089");
        var schedule = new Aes256Schedule(key);
        var actual = new byte[16];
        schedule.EncryptBlock(plain, actual);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            Console.Error.WriteLine("AES-256 self-test FAILED");
            return 1;
        }

        // CFB roundtrip through the FileAccessEncrypted framing.
        var payload = new byte[1000];
        new Random(1).NextBytes(payload);
        byte[] blob = EncryptedBlob.Seal(payload, key, new byte[16]);
        using var source = new MemorySource(blob);
        EncryptedBlob parsed = EncryptedBlob.Read(source, 0, out string? error)
            ?? throw new InvalidOperationException(error);
        if (parsed.TryDecrypt(key) is not { } decrypted || !decrypted.AsSpan().SequenceEqual(payload))
        {
            Console.Error.WriteLine("AES-256-CFB self-test FAILED");
            return 1;
        }

        if (GodotCrypto.DecryptCfb(GodotCrypto.EncryptCfb(payload, key, new byte[16]), key, new byte[16]) is not { } roundtrip ||
            !roundtrip.AsSpan().SequenceEqual(payload))
        {
            Console.Error.WriteLine("CFB roundtrip self-test FAILED");
            return 1;
        }

        // Password derivation from Godot's own test vector.
        if (System.Text.Encoding.ASCII.GetString(GodotCrypto.KeyFromPassword("password")) != "5f4dcc3b5aa765d61d8327deb882cf99")
        {
            Console.Error.WriteLine("password key derivation self-test FAILED");
            return 1;
        }

        Console.Out.WriteLine("all checks passed");
        return 0;
    }

    private static string Compact(string? root, string path)
    {
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        try
        {
            string relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch
        {
            return path;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed class UsageException : Exception
{
    public UsageException(string message) : base(message)
    {
    }
}