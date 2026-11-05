using UnGodot.Recovery;
using UnGodot.Recovery.Stages;
using Xunit;

namespace UnGodot.Tests;

public class RecoveryTests : IDisposable
{
    private readonly string _root;

    public RecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ungodot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void PipelineWritesProjectGodotAndDecompilesScripts()
    {
        File.WriteAllBytes(
            Path.Combine(_root, "project.binary"),
            ProjectConfigTests.BuildEcFg(
                ("application/config/name", ProjectConfigTests.EncodeString("My Game")),
                ("application/run/main_scene", ProjectConfigTests.EncodeString("res://main.tscn"))));

        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllBytes(Path.Combine(_root, "scripts", "player.gdc"), TestBytecode.SimpleScript());

        var report = new RecoveryReport { Root = _root };
        var context = new RecoveryContext
        {
            Root = _root,
            Options = new RecoveryOptions(),
            Report = report,
            EngineMajor = 3,
            EngineMinor = 1,
        };

        RecoveryReport result = RecoveryPipeline
            .RunAsync(context, [new ProjectSettingsStage(), new ScriptStage()])
            .GetAwaiter().GetResult();

        Assert.True(result.Ok, string.Join("; ", result.Stages.Select(stage => stage.Error)));

        string projectGodot = File.ReadAllText(Path.Combine(_root, "project.godot"));
        Assert.Contains("config_version=4", projectGodot);
        Assert.Contains("[application]\n\nconfig/name=\"My Game\"", projectGodot);

        string script = File.ReadAllText(Path.Combine(_root, "scripts", "player.gd"));
        Assert.Equal("extends Node\nfunc _ready():\n\tprint(\"hi\")\n", script);

        // The compiled script is archived, not deleted.
        Assert.False(File.Exists(Path.Combine(_root, "scripts", "player.gdc")));
        Assert.True(File.Exists(Path.Combine(_root, ".autoconverted", "scripts", "player.gdc")));

        string reportPath = Path.Combine(_root, "ungodot_recovery.json");
        result.WriteJson(reportPath);
        Assert.True(File.Exists(reportPath));
        Assert.Contains("project-settings", File.ReadAllText(reportPath));
    }

    [Fact]
    public void PipelineKeepsCompiledScriptsWhenAsked()
    {
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        Directory.CreateDirectory(Path.Combine(_root, "scripts"));
        File.WriteAllBytes(Path.Combine(_root, "scripts", "player.gdc"), TestBytecode.SimpleScript());

        var context = new RecoveryContext
        {
            Root = _root,
            Options = new RecoveryOptions { MoveCompiledScripts = false },
            Report = new RecoveryReport { Root = _root },
            EngineMajor = 3,
            EngineMinor = 1,
        };

        RecoveryReport result = RecoveryPipeline
            .RunAsync(context, [new ScriptStage()])
            .GetAwaiter().GetResult();

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_root, "scripts", "player.gdc")));
        Assert.True(File.Exists(Path.Combine(_root, "scripts", "player.gd")));
    }

    [Fact]
    public void AFailingStageDoesNotStopTheOthers()
    {
        // project.binary is truncated: the settings stage must fail, the script stage still runs.
        File.WriteAllBytes(Path.Combine(_root, "project.binary"), "ECFG"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "a.gdc"), TestBytecode.SimpleScript());

        var context = new RecoveryContext
        {
            Root = _root,
            Options = new RecoveryOptions(),
            Report = new RecoveryReport { Root = _root },
            EngineMajor = 3,
            EngineMinor = 1,
        };

        RecoveryReport result = RecoveryPipeline
            .RunAsync(context, [new ProjectSettingsStage(), new ScriptStage()])
            .GetAwaiter().GetResult();

        Assert.False(result.Stages[0].Ok);
        Assert.True(result.Stages[1].Ok);
        Assert.True(File.Exists(Path.Combine(_root, "a.gd")));
    }
}