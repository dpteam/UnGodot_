using System.Text.RegularExpressions;
using UnGodot.Core.IO;
using UnGodot.Core.Logging;
using UnGodot.Textures;

namespace UnGodot.Recovery.Stages;

/// <summary>
/// Converts Godot 3 <c>.stex</c> stream textures to PNG.
///
/// Games do not ship the <c>.import</c> metadata, so the original source path cannot be read
/// from disk; it is reconstructed from the import name
/// (<c>.import/&lt;name&gt;-&lt;md5&gt;.&lt;platform&gt;.stex</c>). When several platform
/// variants of one texture exist, the first decodable one wins (desktop S3TC first).
/// </summary>
public sealed partial class TextureStage : IRecoveryStage
{
    public string Name => "textures";

    public Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken)
    {
        if (!context.Options.ConvertTextures)
        {
            result.Notes.Add("texture conversion disabled");
            return Task.CompletedTask;
        }

        List<string> files = Directory
            .EnumerateFiles(context.Root, "*.stex", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            result.Notes.Add("no stream textures found");
            return Task.CompletedTask;
        }

        // Group platform variants by their source name and pick the best one per group.
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string key = SourceKey(Path.GetFileNameWithoutExtension(file)) ?? Path.GetFileNameWithoutExtension(file);
            if (!groups.TryGetValue(key, out List<string>? variants))
            {
                variants = [];
                groups[key] = variants;
            }

            variants.Add(file);
        }

        int converted = 0;
        int skipped = 0;
        int redundantVariants = 0;
        int collisions = 0;
        var usedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        object targetGate = new();
        var notes = new List<string>();
        object notesGate = new();

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Environment.ProcessorCount,
        };

        Parallel.ForEach(groups, options, group =>
        {
            (string sourceName, List<string> variants) = group;

            string? chosen = variants
                .OrderByDescending(VariantScore)
                .FirstOrDefault();

            if (chosen is null)
            {
                return;
            }

            try
            {
                byte[] data = File.ReadAllBytes(chosen);
                if (!StreamTexture.LooksLikeStreamTexture(data))
                {
                    throw new TextureException("not a Godot 3 stream texture");
                }

                byte[] png = StreamTexture.ToPng(data);
                string target = Path.Combine(context.Root, Path.ChangeExtension(sourceName, ".png"));

                // Two source paths can flatten to the same file name once the original
                // folders are gone; keep both files instead of overwriting one.
                lock (targetGate)
                {
                    if (!usedTargets.Add(target))
                    {
                        string hash = SourceHash(Path.GetFileNameWithoutExtension(chosen)) ?? "duplicate";
                        target = Path.Combine(
                            context.Root,
                            $"{Path.GetFileNameWithoutExtension(target)}-{hash[..Math.Min(8, hash.Length)]}.png");
                        usedTargets.Add(target);
                        Interlocked.Increment(ref collisions);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(PathUtil.FullPath(target), png);

                Interlocked.Increment(ref converted);
                if (variants.Count > 1)
                {
                    Interlocked.Add(ref redundantVariants, variants.Count - 1);
                }
            }
            catch (Exception ex) when (ex is TextureException or IOException)
            {
                Interlocked.Increment(ref skipped);
                lock (notesGate)
                {
                    if (notes.Count < 50)
                    {
                        notes.Add($"{Path.GetFileName(chosen)}: {ex.Message}");
                    }
                }

                Log.Debug($"{Path.GetFileName(chosen)}: {ex.Message}");
            }
        });

        result.Count("converted", converted);
        result.Count("skipped", skipped);
        if (collisions > 0)
        {
            result.Count("name collisions", collisions);
        }

        if (redundantVariants > 0)
        {
            result.Count("extra variants", redundantVariants);
        }

        result.Notes.AddRange(notes);
        return Task.CompletedTask;
    }

    /// <summary>Score a variant so that a decodable desktop texture beats a mobile one.</summary>
    private static int VariantScore(string path)
    {
        string name = Path.GetFileName(path).ToLowerInvariant();
        try
        {
            using var stream = File.OpenRead(PathUtil.FullPath(path));
            byte[] header = new byte[20];
            if (stream.Read(header, 0, 20) != 20 || !StreamTexture.LooksLikeStreamTexture(header))
            {
                return -1000;
            }

            StreamTexture.Header parsed = StreamTexture.ReadHeader(header);
            if (parsed.EmbeddedPng)
            {
                return 900;
            }

            int formatScore = parsed.Format switch
            {
                Godot3ImageFormat.Dxt1 or Godot3ImageFormat.Dxt5 => 800,
                Godot3ImageFormat.Dxt3 => 790,
                Godot3ImageFormat.RGBA8 or Godot3ImageFormat.RGB8 or Godot3ImageFormat.RGBA4444 => 780,
                Godot3ImageFormat.RgtcR or Godot3ImageFormat.RgtcRG => 770,
                _ => 100,
            };

            // "-low" variants are half resolution, prefer the full one.
            return name.Contains("-low", StringComparison.Ordinal) ? formatScore - 50 : formatScore;
        }
        catch (Exception ex) when (ex is IOException or TextureException)
        {
            return -1000;
        }
    }

    /// <summary>
    /// <c>02__foo.tga-b7bf2bd743f1e9cd07fa1b4c0ada05bf.s3tc</c> gives <c>02__foo.tga</c>.
    /// </summary>
    internal static string? SourceKey(string nameWithoutVariantExtension)
    {
        Match match = SourceNamePattern().Match(nameWithoutVariantExtension);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>The import hash of the source path, from <c>name-&lt;32 hex&gt;.s3tc</c>.</summary>
    internal static string? SourceHash(string nameWithoutVariantExtension)
    {
        Match match = SourceNamePattern().Match(nameWithoutVariantExtension);
        return match.Success ? match.Groups["hash"].Value : null;
    }

    [GeneratedRegex(@"^(?<name>.+)-(?<hash>[0-9a-fA-F]{32})(\.(?<platform>s3tc|etc2|etc|astc|nx|pvrtc|bptc))?(-low)?$")]
    private static partial Regex SourceNamePattern();
}