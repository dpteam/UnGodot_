namespace UnGodot.GDScript;

public sealed class RevisionCandidate
{
    public required BytecodeRevision Revision { get; init; }
    public required bool Passed { get; init; }
    public string? Error { get; init; }
}

public sealed class RevisionDetection
{
    public required int BytecodeVersion { get; init; }
    public BytecodeRevision? Revision { get; init; }
    public required IReadOnlyList<RevisionCandidate> Candidates { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Picks the bytecode revision a compiled script was produced by.
///
/// Several engine commits share a bytecode version (13 covers Godot 3.1 through 3.5) and
/// differ mostly in their built-in function tables. Every revision is therefore validated
/// against the actual token stream (including the argument counts at call sites). The engine
/// version hint (from <c>project.binary</c> or <c>--engine</c>) narrows the candidates first.
/// </summary>
public static class RevisionDetector
{
    public static RevisionDetection Detect(byte[] buffer, string? engineHint = null)
    {
        int version;
        try
        {
            version = GdScriptBytecode.PeekVersion(buffer);
        }
        catch (GdScriptBytecodeException ex)
        {
            return new RevisionDetection { BytecodeVersion = -1, Candidates = [], Error = ex.Message };
        }

        if (version >= GdScriptBytecode.GdScript2Version)
        {
            return new RevisionDetection
            {
                BytecodeVersion = version,
                Candidates = [],
                Error = $"GDScript bytecode v{version} (Godot 4) is not supported yet",
            };
        }

        List<BytecodeRevision> revisions = BytecodeVersionDatabase.ForVersion(version).ToList();
        if (revisions.Count == 0)
        {
            return new RevisionDetection
            {
                BytecodeVersion = version,
                Candidates = [],
                Error = $"no known GDScript revision uses bytecode version {version}",
            };
        }

        GdScriptBytecode bytecode;
        try
        {
            bytecode = GdScriptBytecode.Parse(buffer);
        }
        catch (GdScriptBytecodeException ex)
        {
            return new RevisionDetection { BytecodeVersion = version, Candidates = [], Error = ex.Message };
        }

        var candidates = new List<RevisionCandidate>(revisions.Count);
        foreach (BytecodeRevision revision in revisions)
        {
            BytecodeTestResult result = BytecodeTester.Test(bytecode, revision, out _, out _, out string? testError);
            candidates.Add(new RevisionCandidate
            {
                Revision = revision,
                Passed = result == BytecodeTestResult.Pass,
                Error = testError,
            });
        }

        List<RevisionCandidate> passing = candidates.Where(candidate => candidate.Passed).ToList();
        List<RevisionCandidate> pool = passing.Count > 0 ? passing : candidates;

        if (!string.IsNullOrWhiteSpace(engineHint))
        {
            (int hintMajor, int hintMinor, _) = BytecodeVersionDatabase.ParseVersion(engineHint);
            if (hintMajor > 0)
            {
                var sameMinor = pool
                    .Where(candidate =>
                    {
                        (int major, int minor, _) = BytecodeVersionDatabase.ParseVersion(candidate.Revision.EngineVersion);
                        return major == hintMajor && minor == hintMinor;
                    })
                    .ToList();

                if (sameMinor.Count > 0)
                {
                    pool = sameMinor;
                }
                else
                {
                    BytecodeRevision? hinted = BytecodeVersionDatabase.GuessForEngine(engineHint, version);
                    if (hinted is not null)
                    {
                        pool = pool.Where(candidate => candidate.Revision.Rev == hinted.Rev).DefaultIfEmpty(pool[0]).ToList();
                    }
                }
            }
        }

        RevisionCandidate? best = pool
            .OrderByDescending(candidate => IsStable(candidate.Revision))
            .ThenByDescending(candidate => BytecodeVersionDatabase.ParseVersion(candidate.Revision.EngineVersion).Patch)
            .ThenByDescending(candidate => candidate.Revision.EngineVersion, StringComparer.Ordinal)
            .FirstOrDefault();

        string? error = null;
        if (best is null)
        {
            best = candidates[0];
            error = "no revision passed the bytecode validation";
        }
        else if (passing.Count == 0)
        {
            error = "no revision passed the bytecode validation; the result may use the wrong function names";
        }

        return new RevisionDetection
        {
            BytecodeVersion = version,
            Revision = best?.Revision,
            Candidates = candidates,
            Error = error,
        };
    }

    private static bool IsStable(BytecodeRevision revision) =>
        !revision.EngineVersion.Contains("dev", StringComparison.OrdinalIgnoreCase) &&
        !revision.EngineVersion.Contains("beta", StringComparison.OrdinalIgnoreCase) &&
        !revision.EngineVersion.Contains("alpha", StringComparison.OrdinalIgnoreCase) &&
        !revision.EngineVersion.Contains("rc", StringComparison.OrdinalIgnoreCase);
}