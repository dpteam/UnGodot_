using System.Text.RegularExpressions;

namespace UnGodot.Recovery;

/// <summary>
/// Reads Godot's <c>*.import</c> metadata. An imported file (a texture, a model, a scene)
/// ships only its imported form under <c>.import/</c>; the <c>.import</c> sidecar next to the
/// original path maps the imported file back to the source path the project references.
///
/// <code>
/// [remap]
/// importer="texture"
/// type="StreamTexture"
/// path.astc="res://.import/icon.png-4872….astc.stex"
/// path.s3tc="res://.import/icon.png-4872….s3tc.stex"
/// </code>
/// </summary>
public static partial class ImportMetadata
{
    /// <summary>
    /// <c>res://</c> path of an imported file → <c>res://</c> path of the source asset, e.g.
    /// <c>res://.import/icon.png-4872….s3tc.stex</c> → <c>res://icon.png</c>.
    /// </summary>
    public static Dictionary<string, string> ReadSourceMap(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in Directory.EnumerateFiles(root, "*.import", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith(".autoconverted", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith(".godot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string source = "res://" + relative[..^".import".Length].Replace('\\', '/');
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match match in PathKeyPattern().Matches(text))
            {
                map.TryAdd(match.Groups["path"].Value, source);
            }
        }

        return map;
    }

    /// <summary>A <c>[remap]</c> entry: <c>path=</c> or a platform variant such as <c>path.s3tc=</c>.</summary>
    [GeneratedRegex("^\\s*path(?:\\.[A-Za-z0-9_]+)?\\s*=\\s*\"(?<path>res://[^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex PathKeyPattern();
}