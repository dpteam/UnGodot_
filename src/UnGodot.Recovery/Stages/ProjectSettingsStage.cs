using UnGodot.Core.Logging;
using UnGodot.Core.IO;
using UnGodot.Recovery.Project;

namespace UnGodot.Recovery.Stages;

/// <summary>
/// Writes an editor-openable <c>project.godot</c> from <c>project.binary</c>.
///
/// The binary config is exactly what the game runs with, but the editor only reads the text
/// format, so a recovered project without this stage opens with "no main scene" and default
/// settings.
/// </summary>
public sealed class ProjectSettingsStage : IRecoveryStage
{
    public string Name => "project-settings";

    public Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken)
    {
        string textPath = Path.Combine(context.Root, "project.godot");
        if (File.Exists(textPath))
        {
            result.Notes.Add("project.godot already exists, kept as is");
            result.Count("kept");
            return Task.CompletedTask;
        }

        string binaryPath = Path.Combine(context.Root, "project.binary");
        string legacyPath = Path.Combine(context.Root, "engine.cfb");
        if (!File.Exists(binaryPath))
        {
            if (File.Exists(legacyPath))
            {
                result.Notes.Add("engine.cfb (Godot 2 project config) is not supported yet");
            }
            else
            {
                result.Notes.Add("no project.binary found, nothing to convert");
            }

            return Task.CompletedTask;
        }

        int engineMajor = context.EngineMajor > 0 ? context.EngineMajor : 3;
        if (engineMajor >= 4)
        {
            throw new ProjectConfigException(
                "Godot 4 project.binary uses the Godot 4 variant format, which is not implemented yet");
        }

        cancellationToken.ThrowIfCancellationRequested();

        ProjectConfig config = ProjectConfig.LoadEcFg(binaryPath, engineMajor, context.EngineMinor);
        string text = config.ToProjectGodotText();
        File.WriteAllText(PathUtil.FullPath(textPath), text);

        result.Count("settings", config.Settings.Count);
        result.Count("written");
        result.Notes.Add($"config_version={config.ConfigVersion} for Godot {engineMajor}{(context.EngineMinor > 0 ? "." + context.EngineMinor : string.Empty)}");
        Log.Debug($"wrote {textPath} ({config.Settings.Count} settings)");

        return Task.CompletedTask;
    }
}