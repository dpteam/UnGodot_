using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.GDScript;

namespace UnGodot.Recovery.Stages;

/// <summary>
/// Decompiles every <c>.gdc</c> of the recovered tree into a <c>.gd</c> next to it, then moves
/// the compiled file to <c>.autoconverted/</c> so the project tree only shows sources.
/// </summary>
public sealed class ScriptStage : IRecoveryStage
{
    public string Name => "scripts";

    public Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken)
    {
        if (!context.Options.DecompileScripts)
        {
            result.Notes.Add("script decompilation disabled");
            return Task.CompletedTask;
        }

        List<string> scripts = EnumerateScripts(context.Root);
        if (scripts.Count == 0)
        {
            result.Notes.Add("no compiled scripts found");
            return Task.CompletedTask;
        }

        var options = new DecompileOptions();
        string autoconverted = Path.Combine(context.Root, ".autoconverted");

        foreach (string script in scripts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(context.Root, script);

            try
            {
                byte[] bytecode = File.ReadAllBytes(script);
                RevisionDetection detection = RevisionDetector.Detect(bytecode, context.EngineHint);
                if (detection.Revision is null)
                {
                    throw new GdScriptBytecodeException(detection.Error ?? "cannot determine the bytecode revision");
                }

                DecompileResult decompiled = GdScriptDecompiler.Decompile(bytecode, detection.Revision, options);
                string target = Path.ChangeExtension(script, ".gd");
                File.WriteAllText(PathUtil.FullPath(target), decompiled.Source);

                result.Count("decompiled");
                string revision = detection.Revision.EngineVersion;
                result.Counters[$"bytecode {revision}"] = result.Counters.GetValueOrDefault($"bytecode {revision}") + 1;

                if (context.Options.MoveCompiledScripts)
                {
                    string archived = Path.Combine(autoconverted, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
                    File.Move(script, archived, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is GdScriptBytecodeException or GdScriptDecompileException or IOException)
            {
                result.Count("failed");
                result.Notes.Add($"{relative}: {ex.Message}");
                Log.Warn($"{relative}: {ex.Message}");
            }
        }

        if (result.Counters.GetValueOrDefault("decompiled") == 0)
        {
            throw new InvalidOperationException("no script could be decompiled");
        }

        return Task.CompletedTask;
    }

    private static List<string> EnumerateScripts(string root)
    {
        var scripts = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*.gdc", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith(".autoconverted", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".godot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scripts.Add(path);
        }

        scripts.Sort(StringComparer.OrdinalIgnoreCase);
        return scripts;
    }
}