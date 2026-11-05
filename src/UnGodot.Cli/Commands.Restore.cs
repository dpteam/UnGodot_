using System.Text.Json;
using UnGodot.Core.Binfmt;
using UnGodot.Core.Discovery;
using UnGodot.Core.Extract;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Core.Workspace;
using UnGodot.Recovery;
using UnGodot.Recovery.Stages;

namespace UnGodot.Cli;

internal static partial class Commands
{
    /// <summary>
    /// The native pipeline: extract the packs (when the input is a game), then recover the
    /// project in place — <c>project.godot</c> from <c>project.binary</c>, decompiled scripts,
    /// and a machine readable report.
    /// </summary>
    private static int Restore(CliOptions options, CancellationToken cancellationToken)
    {
        string input = RequirePath(options);
        var workspace = new GameWorkspace(WorkspaceFor(options));

        (int hintMajor, int hintMinor) = ParseEngineHint(options.EngineVersion);
        string root;
        int engineMajor;
        int engineMinor;
        int settingsCount = 0;

        if (Directory.Exists(input))
        {
            root = input;
            if (!string.IsNullOrWhiteSpace(options.Out) &&
                !string.Equals(Path.GetFullPath(options.Out!), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase))
            {
                root = Path.GetFullPath(options.Out!);
                Log.Info($"copying {input} to {root} before recovering it");
                CopyDirectory(input, root);
            }

            engineMajor = hintMajor > 0 ? hintMajor : 3;
            engineMinor = hintMinor;
        }
        else
        {
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

            root = options.Out ?? DefaultOutput(discovery, "project");
            PackCandidate? primary = discovery.Primary;
            (engineMajor, engineMinor) = InferEngine(primary, hintMajor, hintMinor);

            var extractOptions = new ExtractOptions
            {
                Overwrite = !options.NoOverwrite,
                VerifyMd5 = !options.NoVerify,
                Filter = options.Filter,
                MaxDegree = options.Jobs,
                WriteManifest = !options.NoManifest,
                ContinueOnError = true,
            };

            Log.Info($"extracting {discovery.Candidates.Count(c => c.IsUsablePack)} pack(s) into {root}");
            GameWorkspace.MultiExtractResult extraction = workspace.ExtractAll(
                discovery, root, extractOptions, cancellationToken: cancellationToken);

            foreach (string failure in extraction.Failures)
            {
                Log.Error(failure);
            }

            if (extraction.PerPack.Count == 0)
            {
                Console.Error.WriteLine("ungodot: nothing could be extracted");
                return 1;
            }

            settingsCount = extraction.TotalWritten;
        }

        var report = new RecoveryReport { Root = root };
        var context = new RecoveryContext
        {
            Root = root,
            Options = new RecoveryOptions
            {
                DecompileScripts = !options.NoScripts,
                MoveCompiledScripts = !options.KeepGdc,
                ConvertTextures = !options.NoTextures,
                ConvertResources = !options.NoResources,
                EngineVersionHint = options.EngineVersion,
                EngineMajor = engineMajor,
            },
            Report = report,
            EngineMajor = engineMajor,
            EngineMinor = engineMinor,
        };

        var stages = new List<IRecoveryStage> { new ProjectSettingsStage() };
        if (!options.NoScripts)
        {
            stages.Add(new ScriptStage());
        }

        if (!options.NoTextures)
        {
            stages.Add(new TextureStage());
        }

        if (!options.NoResources)
        {
            stages.Add(new ResourceStage());
        }

        RecoveryReport result = RecoveryPipeline
            .RunAsync(context, stages, cancellationToken)
            .GetAwaiter().GetResult();

        string reportPath = Path.Combine(root, "ungodot_recovery.json");
        try
        {
            result.WriteJson(reportPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"cannot write the recovery report: {ex.Message}");
        }

        if (options.Json)
        {
            Console.Out.WriteLine(JsonSerializer.Serialize(new
            {
                root,
                ok = result.Ok,
                engine = $"{engineMajor}.{engineMinor}",
                extractedFiles = settingsCount,
                stages = result.Stages.Select(stage => new
                {
                    stage = stage.Stage,
                    ok = stage.Ok,
                    error = stage.Error,
                    counters = stage.Counters,
                    notes = stage.Notes,
                }),
                report = reportPath,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            Console.Out.WriteLine($"root             {root}");
            string engineLabel = engineMinor > 0 || engineMajor == 4
                ? $"Godot {engineMajor}.{engineMinor}"
                : $"Godot {engineMajor}.x";
            Console.Out.WriteLine($"engine           {engineLabel}");
            foreach (RecoveryStageResult stage in result.Stages)
            {
                string counters = string.Join(", ", stage.Counters.Select(pair => $"{pair.Value} {pair.Key}"));
                Console.Out.WriteLine($"{stage.Stage,-17}{counters}{(stage.Ok ? string.Empty : "  FAILED: " + stage.Error)}");
            }

            Console.Out.WriteLine($"report           {reportPath}");
        }

        return result.Ok ? 0 : 1;
    }

    private static (int Major, int Minor) ParseEngineHint(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint))
        {
            return (0, 0);
        }

        (int major, int minor, _) = UnGodot.GDScript.BytecodeVersionDatabase.ParseVersion(hint);
        return (major, minor);
    }

    /// <summary>Engine version from the pack format (reliable) and the header (when plausible).</summary>
    private static (int Major, int Minor) InferEngine(PackCandidate? candidate, int hintMajor, int hintMinor)
    {
        if (hintMajor > 0)
        {
            return (hintMajor, hintMinor);
        }

        if (candidate?.Header is { } header)
        {
            int major = header.FormatVersion switch
            {
                0 => 2,
                1 => 3,
                _ => 4,
            };

            uint headerMajor = header.EngineMajor;
            uint headerMinor = header.EngineMinor;
            if (headerMajor == (uint)major && headerMajor is >= 2 and <= 6)
            {
                return (major, (int)headerMinor);
            }

            return (major, major == 3 ? 1 : 0);
        }

        return (3, 1);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(PathUtil.FullPath(file), PathUtil.FullPath(target), overwrite: true);
        }
    }
}