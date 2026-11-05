using System.Diagnostics;
using System.Text.Json;
using UnGodot.Core.Logging;

namespace UnGodot.Recovery;

public sealed class RecoveryOptions
{
    /// <summary>Decompile <c>.gdc</c> scripts to <c>.gd</c>.</summary>
    public bool DecompileScripts { get; init; } = true;

    /// <summary>Move compiled scripts to <c>.autoconverted/</c> after decompiling them.</summary>
    public bool MoveCompiledScripts { get; init; } = true;

    /// <summary>Convert <c>.stex</c> stream textures to PNG.</summary>
    public bool ConvertTextures { get; init; } = true;

    /// <summary>Engine version hint for the bytecode revision detection (e.g. <c>3.1</c>).</summary>
    public string? EngineVersionHint { get; init; }

    /// <summary>Godot major version of the project (0 = let the stage decide).</summary>
    public int EngineMajor { get; init; }
}

public sealed class RecoveryStageResult
{
    public required string Stage { get; init; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public TimeSpan Duration { get; set; }
    public Dictionary<string, int> Counters { get; } = [];
    public List<string> Notes { get; } = [];

    public void Count(string name, int value = 1) => Counters[name] = Counters.GetValueOrDefault(name) + value;
}

public sealed class RecoveryReport
{
    public string Root { get; init; } = string.Empty;
    public List<RecoveryStageResult> Stages { get; } = [];
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public DateTime FinishedUtc { get; set; }

    public bool Ok => Stages.All(stage => stage.Ok);

    public Dictionary<string, int> Totals()
    {
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RecoveryStageResult stage in Stages)
        {
            foreach (KeyValuePair<string, int> counter in stage.Counters)
            {
                totals[counter.Key] = totals.GetValueOrDefault(counter.Key) + counter.Value;
            }
        }

        return totals;
    }

    public void WriteJson(string path)
    {
        var payload = new
        {
            root = Root,
            ok = Ok,
            started = StartedUtc,
            finished = FinishedUtc,
            stages = Stages.Select(stage => new
            {
                stage = stage.Stage,
                ok = stage.Ok,
                error = stage.Error,
                seconds = Math.Round(stage.Duration.TotalSeconds, 3),
                counters = stage.Counters,
                notes = stage.Notes,
            }),
            totals = Totals(),
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public sealed class RecoveryContext
{
    public required string Root { get; init; }
    public required RecoveryOptions Options { get; init; }
    public required RecoveryReport Report { get; init; }

    /// <summary>Godot major version of the project (3 by default).</summary>
    public int EngineMajor { get; set; } = 3;

    /// <summary>Godot minor version when known (3.6, 4.2, …).</summary>
    public int EngineMinor { get; set; }

    public string? EngineHint => Options.EngineVersionHint
        ?? (EngineMinor > 0 ? $"{EngineMajor}.{EngineMinor}" : null);
}

public interface IRecoveryStage
{
    string Name { get; }

    Task RunAsync(RecoveryContext context, RecoveryStageResult result, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the recovery stages in order. A failing stage is recorded and the pipeline keeps
/// going: one broken resource format should not throw away the work of the other stages.
/// </summary>
public static class RecoveryPipeline
{
    public static async Task<RecoveryReport> RunAsync(
        RecoveryContext context,
        IEnumerable<IRecoveryStage> stages,
        CancellationToken cancellationToken = default)
    {
        foreach (IRecoveryStage stage in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new RecoveryStageResult { Stage = stage.Name };
            var watch = Stopwatch.StartNew();
            Log.Info($"[{stage.Name}] starting");

            try
            {
                await stage.RunAsync(context, result, cancellationToken).ConfigureAwait(false);
                result.Ok = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Error = ex.Message;
                Log.Error($"[{stage.Name}] {ex.Message}");
            }

            result.Duration = watch.Elapsed;
            context.Report.Stages.Add(result);

            if (result.Ok)
            {
                string counters = string.Join(", ", result.Counters.Select(pair => $"{pair.Value} {pair.Key}"));
                Log.Info($"[{stage.Name}] done in {result.Duration.TotalSeconds:0.0}s{(counters.Length > 0 ? ": " + counters : string.Empty)}");
            }
        }

        context.Report.FinishedUtc = DateTime.UtcNow;
        return context.Report;
    }
}