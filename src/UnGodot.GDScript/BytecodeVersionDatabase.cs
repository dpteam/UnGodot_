using System.Text.Json;

namespace UnGodot.GDScript;

/// <summary>One GDScript bytecode revision (a commit of the engine) and its token tables.</summary>
public sealed class BytecodeRevision
{
    public required string Rev { get; init; }
    public required int Version { get; init; }
    public required string EngineVersion { get; init; }
    public required string MaxEngineVersion { get; init; }
    public required int EngineMajor { get; init; }
    public required int VariantMajor { get; init; }
    public string? ParentRev { get; init; }

    /// <summary>Local token id (low 8 bits of a bytecode token) to global token.</summary>
    public required IReadOnlyList<GlobalToken> TokenMap { get; init; }

    /// <summary>Built-in function names; a token payload indexes this list.</summary>
    public required IReadOnlyList<string> FunctionNames { get; init; }

    /// <summary>Accepted argument range of each built-in function (empty when unknown).</summary>
    public IReadOnlyList<(int Min, int Max)> ArgCounts { get; init; } = [];

    public int TokenMax => TokenMap.Count;

    public bool IsGdScript2 => Version >= 100;

    public GlobalToken ToGlobalToken(uint token)
    {
        uint local = token & 0xFF;
        return local < TokenMap.Count ? TokenMap[(int)local] : GlobalToken.G_TK_MAX;
    }

    public string GetFunctionName(int index) =>
        index >= 0 && index < FunctionNames.Count ? FunctionNames[index] : string.Empty;

    public override string ToString() => $"{EngineVersion} (rev 0x{Rev}, bytecode v{Version})";
}

/// <summary>
/// Reads the bytecode revision tables shipped by GDRE Tools (<c>misc/bytecode_versions.json</c>,
/// MIT) and exposes them as strongly typed revisions.
/// </summary>
public static class BytecodeVersionDatabase
{
    private static readonly Lazy<IReadOnlyList<BytecodeRevision>> LazyRevisions = new(Load);
    private static readonly Lazy<Dictionary<string, BytecodeRevision>> LazyByRev;

    static BytecodeVersionDatabase()
    {
        LazyByRev = new Lazy<Dictionary<string, BytecodeRevision>>(() =>
        {
            var map = new Dictionary<string, BytecodeRevision>(StringComparer.OrdinalIgnoreCase);
            foreach (BytecodeRevision revision in Revisions)
            {
                map[revision.Rev] = revision;
            }

            return map;
        });
    }

    public static IReadOnlyList<BytecodeRevision> Revisions => LazyRevisions.Value;

    public static BytecodeRevision? ForRev(string rev) =>
        LazyByRev.Value.TryGetValue(rev, out BytecodeRevision? revision) ? revision : null;

    public static IReadOnlyList<BytecodeRevision> ForVersion(int version) =>
        Revisions.Where(revision => revision.Version == version).ToList();

    /// <summary>
    /// Best revision guess from an engine version string (<c>3.1.2</c>, <c>4.3</c>,
    /// <c>3.5.1-stable</c>) and a bytecode version read from a file.
    /// </summary>
    public static BytecodeRevision? GuessForEngine(string engineVersion, int bytecodeVersion)
    {
        (int major, int minor, int patch) = ParseVersion(engineVersion);
        List<BytecodeRevision> candidates = ForVersion(bytecodeVersion).ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        if (major == 0)
        {
            return NewestStable(candidates);
        }

        static bool SameMinor(BytecodeRevision revision, int major, int minor)
        {
            (int revisionMajor, int revisionMinor, _) = ParseVersion(revision.EngineVersion);
            return revisionMajor == major && revisionMinor == minor;
        }

        static bool InRange(BytecodeRevision revision, int major, int minor, int patch)
        {
            (int engineMajor, int engineMinor, int enginePatch) = ParseVersion(revision.EngineVersion);
            (int maxMajor, int maxMinor, int maxPatch) = ParseVersion(revision.MaxEngineVersion);
            if (engineMajor != major)
            {
                return false;
            }

            if (Compare(major, minor, patch, engineMajor, engineMinor, enginePatch) < 0)
            {
                return false;
            }

            if (maxMajor == 0 || (maxMajor == 3 && maxMinor == 0))
            {
                // No upper bound recorded: treat it as "this minor and later".
                return true;
            }

            return Compare(major, minor, patch, maxMajor, maxMinor, maxPatch) <= 0;
        }

        // Exact range match first, newest such revision (patch releases are cumulative).
        BytecodeRevision? exact = candidates
            .Where(revision => InRange(revision, major, minor, patch))
            .OrderByDescending(revision => ParseVersion(revision.EngineVersion).Patch)
            .ThenByDescending(revision => revision.EngineVersion, StringComparer.Ordinal)
            .FirstOrDefault();
        if (exact is not null)
        {
            return exact;
        }

        // Otherwise the newest revision of the same major.minor, stable builds first.
        BytecodeRevision? sameMinor = candidates
            .Where(revision => SameMinor(revision, major, minor))
            .OrderByDescending(revision => IsStable(revision.EngineVersion))
            .ThenByDescending(revision => ParseVersion(revision.EngineVersion).Patch)
            .ThenByDescending(revision => revision.EngineVersion, StringComparer.Ordinal)
            .FirstOrDefault();

        return sameMinor ?? NewestStable(candidates);
    }

    private static bool IsStable(string engineVersion) =>
        !engineVersion.Contains("dev", StringComparison.OrdinalIgnoreCase) &&
        !engineVersion.Contains("beta", StringComparison.OrdinalIgnoreCase) &&
        !engineVersion.Contains("alpha", StringComparison.OrdinalIgnoreCase) &&
        !engineVersion.Contains("rc", StringComparison.OrdinalIgnoreCase);

    private static BytecodeRevision NewestStable(List<BytecodeRevision> candidates) =>
        candidates
            .OrderByDescending(revision => IsStable(revision.EngineVersion))
            .ThenByDescending(revision => revision.EngineVersion, StringComparer.Ordinal)
            .First();

    public static (int Major, int Minor, int Patch) ParseVersion(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (0, 0, 0);
        }

        string[] parts = text.Split('.');
        return (
            LeadingNumber(parts.ElementAtOrDefault(0)),
            LeadingNumber(parts.ElementAtOrDefault(1)),
            LeadingNumber(parts.ElementAtOrDefault(2)));
    }

    private static int LeadingNumber(string? part)
    {
        if (string.IsNullOrEmpty(part))
        {
            return 0;
        }

        int value = 0;
        int digits = 0;
        foreach (char c in part)
        {
            if (!char.IsAsciiDigit(c))
            {
                break;
            }

            value = value * 10 + (c - '0');
            digits++;
            if (digits > 6)
            {
                break;
            }
        }

        return value;
    }

    private static int Compare(int majorA, int minorA, int patchA, int majorB, int minorB, int patchB)
    {
        if (majorA != majorB)
        {
            return majorA.CompareTo(majorB);
        }

        if (minorA != minorB)
        {
            return minorA.CompareTo(minorB);
        }

        return patchA.CompareTo(patchB);
    }

    private static IReadOnlyList<BytecodeRevision> Load()
    {
        var revisions = new List<BytecodeRevision>();
        using Stream? stream = typeof(BytecodeVersionDatabase).Assembly
            .GetManifestResourceStream("UnGodot.GDScript.bytecode_versions.json");
        if (stream is null)
        {
            throw new InvalidOperationException("the bytecode revision table is missing from the assembly");
        }

        var argCounts = LoadArgCounts();

        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            string? rev = entry.TryGetProperty("bytecode_rev", out JsonElement revElement) ? revElement.GetString() : null;
            if (string.IsNullOrEmpty(rev))
            {
                continue;
            }

            int version = entry.TryGetProperty("bytecode_version", out JsonElement versionElement) ? versionElement.GetInt32() : -1;
            if (version < 0)
            {
                continue;
            }

            var tokenMap = new List<GlobalToken>();
            if (entry.TryGetProperty("tk_names", out JsonElement tokens) && tokens.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement token in tokens.EnumerateArray())
                {
                    string name = token.GetString() ?? string.Empty;
                    tokenMap.Add(Enum.TryParse($"G_{name}", ignoreCase: false, out GlobalToken global)
                        ? global
                        : GlobalToken.G_TK_MAX);
                }
            }

            var functions = new List<string>();
            if (entry.TryGetProperty("func_names", out JsonElement funcs) && funcs.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement function in funcs.EnumerateArray())
                {
                    functions.Add(function.GetString() ?? string.Empty);
                }
            }

            if (tokenMap.Count == 0)
            {
                continue;
            }

            IReadOnlyList<(int Min, int Max)> counts = argCounts.TryGetValue(rev, out List<(int Min, int Max)>? table) &&
                                                       table.Count == functions.Count
                ? table
                : [];

            revisions.Add(new BytecodeRevision
            {
                Rev = rev,
                Version = version,
                EngineVersion = entry.TryGetProperty("engine_version", out JsonElement engine) ? engine.GetString() ?? string.Empty : string.Empty,
                MaxEngineVersion = entry.TryGetProperty("max_engine_version", out JsonElement maxEngine) ? maxEngine.GetString() ?? string.Empty : string.Empty,
                EngineMajor = entry.TryGetProperty("engine_ver_major", out JsonElement major) ? major.GetInt32() : 3,
                VariantMajor = entry.TryGetProperty("variant_ver_major", out JsonElement variant) ? variant.GetInt32() : 3,
                ParentRev = entry.TryGetProperty("parent", out JsonElement parent) && parent.ValueKind == JsonValueKind.String ? parent.GetString() : null,
                TokenMap = tokenMap,
                FunctionNames = functions,
                ArgCounts = counts,
            });
        }

        return revisions;
    }

    private static Dictionary<string, List<(int Min, int Max)>> LoadArgCounts()
    {
        var result = new Dictionary<string, List<(int Min, int Max)>>(StringComparer.OrdinalIgnoreCase);
        using Stream? stream = typeof(BytecodeVersionDatabase).Assembly
            .GetManifestResourceStream("UnGodot.GDScript.function_arg_counts.json");
        if (stream is null)
        {
            return result;
        }

        using JsonDocument document = JsonDocument.Parse(stream);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            var table = new List<(int Min, int Max)>();
            foreach (JsonElement entry in property.Value.EnumerateArray())
            {
                table.Add((entry[1].GetInt32(), entry[2].GetInt32()));
            }

            result[property.Name] = table;
        }

        return result;
    }
}