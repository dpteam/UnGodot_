using System.Text.RegularExpressions;
using UnGodot.Core.Logging;

namespace UnGodot.Recovery.Stages;

/// <summary>
/// Points the recovered project at the text files the resource stage wrote: after
/// <c>res://thing.material</c> became <c>thing.tres</c>, every scene, resource, script and
/// <c>project.godot</c> entry that referenced the binary path is rewritten.
///
/// Only complete <c>"res://…"</c> string values that the *resource stage actually converted*
/// are touched, so references to assets that were not converted (textures, shaders, scripts,
/// imports) stay as they are. Running the stage twice changes nothing the second time.
/// </summary>
public sealed partial class RelinkStage : IRecoveryStage
{
    public string Name => "references";

    public Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken)
    {
        if (context.ConvertedResources.Count == 0)
        {
            result.Notes.Add("no converted resources to relink");
            return Task.CompletedTask;
        }

        List<string> files = EnumerateTextFiles(context.Root);
        int updated = 0;
        int references = 0;
        var notes = new List<string>();

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(context.Root, file);

            string original;
            try
            {
                original = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (notes.Count < 10)
                {
                    notes.Add($"{relative}: {ex.Message}");
                }

                continue;
            }

            int replaced = 0;
            string rewritten = ResourcePathPattern().Replace(original, match =>
            {
                string value = match.Groups["path"].Value;
                int subResource = value.IndexOf("::", StringComparison.Ordinal);
                string basePath = subResource >= 0 ? value[..subResource] : value;
                string suffix = subResource >= 0 ? value[subResource..] : string.Empty;

                if (!context.ConvertedResources.TryGetValue(basePath, out string? target))
                {
                    return match.Value;
                }

                replaced++;
                return "\"" + target + suffix + "\"";
            });

            if (replaced == 0)
            {
                continue;
            }

            try
            {
                File.WriteAllText(file, rewritten);
                updated++;
                references += replaced;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (notes.Count < 10)
                {
                    notes.Add($"{relative}: {ex.Message}");
                }

                Log.Warn($"{relative}: {ex.Message}");
            }
        }

        if (updated > 0)
        {
            result.Count("files updated", updated);
            result.Count("references updated", references);
        }

        result.Notes.AddRange(notes);
        return Task.CompletedTask;
    }

    private static List<string> EnumerateTextFiles(string root)
    {
        var files = new List<string>();

        foreach (string pattern in new[] { "*.tres", "*.tscn", "*.gd", "project.godot" })
        {
            foreach (string path in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path);
                if (relative.StartsWith(".autoconverted", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".godot", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                files.Add(path);
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>A complete quoted <c>res://</c> string, optionally with a <c>::sub</c> suffix.</summary>
    [GeneratedRegex("\"(?<path>res://[^\"\r\n]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ResourcePathPattern();
}