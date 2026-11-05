using UnGodot.Core.Logging;
using UnGodot.Resources;

namespace UnGodot.Recovery.Stages;

/// <summary>
/// Converts Godot 3 binary resources to their text form: <c>.material</c>, <c>.mesh</c>,
/// <c>.anim</c> and <c>.res</c> become <c>.tres</c>, while <c>.scn</c> and packed scenes
/// become <c>.tscn</c>.
///
/// The original binary file is left in place: game resources keep referencing
/// <c>res://thing.material</c>, and rewriting those paths is a job for a later stage. The
/// text copy is what an editor can open.
/// </summary>
public sealed class ResourceStage : IRecoveryStage
{
    private const int MaxNotes = 50;

    /// <summary>Extensions the stage inspects; each one is only converted when it holds RSRC/RSCC.</summary>
    private static readonly HashSet<string> CandidateExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".res",
        ".material",
        ".scn",
        ".mesh",
        ".anim",
    };

    public string Name => "resources";

    public Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken)
    {
        if (!context.Options.ConvertResources)
        {
            result.Notes.Add("resource conversion disabled");
            return Task.CompletedTask;
        }

        List<string> files = EnumerateCandidates(context.Root);
        if (files.Count == 0)
        {
            result.Notes.Add("no binary resources found");
            return Task.CompletedTask;
        }

        int converted = 0;
        int scenes = 0;
        int notBinary = 0;
        int failed = 0;
        int collisions = 0;
        var notes = new List<string>();

        // Files are sorted and processed in order so that when two sources flatten to the
        // same text name the same one always wins and the report is reproducible.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(context.Root, file);

            try
            {
                byte[] data = File.ReadAllBytes(file);
                if (!BinaryResource.LooksLikeResource(data))
                {
                    notBinary++;
                    continue;
                }

                BinaryResource resource = BinaryResource.Load(data);
                ResourceTextResult text = ResourceTextWriter.Write(resource);
                string target = TargetPath(file, text.IsScene);
                if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
                {
                    notBinary++;
                    continue;
                }

                if (claimed.TryGetValue(target, out string? owner))
                {
                    target = Disambiguate(target, file, claimed);
                    collisions++;
                    if (notes.Count < MaxNotes)
                    {
                        notes.Add($"{relative}: text name taken by {Path.GetRelativePath(context.Root, owner)}");
                    }
                }

                claimed[target] = file;
                File.WriteAllText(target, text.Text);
                converted++;
                if (text.IsScene)
                {
                    scenes++;
                }

                string sourcePath = ToResPath(context.Root, file);
                string targetPath = ToResPath(context.Root, target);
                if (!string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase))
                {
                    context.ConvertedResources[sourcePath] = targetPath;
                }

                if (text.Notes.Count > 0)
                {
                    foreach (string note in text.Notes.Take(Math.Max(0, MaxNotes - notes.Count)))
                    {
                        notes.Add($"{relative}: {note}");
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                if (notes.Count < MaxNotes)
                {
                    notes.Add($"{relative}: {ex.GetType().Name}: {ex.Message}");
                }

                Log.Debug($"{relative}: {ex.Message}");
            }
        }

        if (converted > 0)
        {
            result.Count("converted", converted);
        }

        if (scenes > 0)
        {
            result.Count("scenes", scenes);
        }

        if (notBinary > 0)
        {
            result.Count("not binary", notBinary);
        }

        if (collisions > 0)
        {
            result.Count("name collisions", collisions);
        }

        if (failed > 0)
        {
            result.Count("failed", failed);
        }

        result.Notes.AddRange(notes);

        if (converted == 0 && failed > 0)
        {
            throw new InvalidOperationException("no binary resource could be converted");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>foo.tres.converted.res</c> becomes <c>foo.tres</c>; <c>foo.material</c> becomes
    /// <c>foo.tres</c> and <c>foo.scn</c> becomes <c>foo.tscn</c>. A scene stored under a
    /// resource name (or the reverse) is renamed to match its real text header.
    /// </summary>
    internal static string TargetPath(string sourcePath, bool isScene)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string name = Path.GetFileName(sourcePath);

        const string convertedSuffix = ".converted.res";
        string stem = name.EndsWith(convertedSuffix, StringComparison.OrdinalIgnoreCase)
            ? name[..^convertedSuffix.Length]
            : Path.GetFileNameWithoutExtension(name);

        string extension = isScene ? ".tscn" : ".tres";
        if (!Path.GetExtension(stem).Equals(extension, StringComparison.OrdinalIgnoreCase))
        {
            stem = Path.GetFileNameWithoutExtension(stem) + extension;
        }

        return Path.Combine(directory, stem);
    }

    /// <summary>Builds the <c>res://</c> path of a file inside the recovered project.</summary>
    internal static string ToResPath(string root, string path) =>
        "res://" + Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>
    /// Keeps both files when two sources would write the same text path, e.g.
    /// <c>pxyspin_body.material</c> and <c>pxyspin_body.tres.converted.res</c> both want
    /// <c>pxyspin_body.tres</c>. The loser gets a <c>-material</c>/<c>-res</c> kind suffix.
    /// </summary>
    private static string Disambiguate(string target, string sourcePath, Dictionary<string, string> claimed)
    {
        string directory = Path.GetDirectoryName(target)!;
        string stem = Path.GetFileNameWithoutExtension(target);
        string extension = Path.GetExtension(target);
        string kind = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();

        string candidate = Path.Combine(directory, $"{stem}-{kind}{extension}");
        for (int attempt = 2; claimed.ContainsKey(candidate); attempt++)
        {
            candidate = Path.Combine(directory, $"{stem}-{kind}-{attempt}{extension}");
        }

        return candidate;
    }

    private static List<string> EnumerateCandidates(string root)
    {
        var files = new List<string>();
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (!CandidateExtensions.Contains(Path.GetExtension(path)))
            {
                continue;
            }

            string relative = Path.GetRelativePath(root, path);
            if (relative.StartsWith(".autoconverted", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".godot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(path);
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }
}