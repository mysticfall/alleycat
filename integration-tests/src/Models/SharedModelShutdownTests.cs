using System.Diagnostics;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Models;

/// <summary>
/// Runtime evidence for CORE-011-UAC-004 and TAC-008: leaving the scene, or quitting through the menu exit or a
/// root-window close, while real MotionBricks generation and Supertonic synthesis are in flight keeps the scene thread
/// responsive, disposes the shared backends before quitting, and ends the process cleanly.
/// </summary>
/// <remarks>
/// <para>
/// The production exit path ends the Godot process, so each fact launches its own Godot process running
/// <c>res://tests/shared-ai-models/shared_model_shutdown_probe.tscn</c>, in which the real <see cref="Game" />
/// autoload owns the shutdown, and observes that process from the outside: its exit code, its log and the probe's
/// report. The probe triggers the exit through the real <c>GameMenu</c> (mock XR controller presses) or the root
/// window's close request, or leaves the NPC scene through the global loading screen's scene change and quits through
/// the root window once the consumers have released their handles; it never calls the shutdown internals.
/// </para>
/// <para>
/// Each fact loads the real models on CUDA, so the facts are <see cref="IsolatedProcessAttribute" />: default, class
/// and exact-method runs all include them, and each runs in its own session with no other session alive. Each fact
/// first checks for Godot processes the framework did not launch and reports itself skipped, naming them, if any is
/// running.
/// </para>
/// <para>
/// The probe process is headless: the measured behaviour is scene-thread scheduling, model disposal and process exit,
/// none of which needs a renderer. Without a display server the operating system's close event cannot be produced, so
/// the probe delivers it as Godot's root window does: a close-request notification followed by
/// <c>close_requested</c>. Artefacts are written to <c>res://temp/shared-ai-models/&lt;fact&gt;/</c>.
/// </para>
/// </remarks>
[Headless]
[IsolatedProcess]
public sealed class SharedModelShutdownTests
{
    private const string ProbeScene = "res://tests/shared-ai-models/shared_model_shutdown_probe.tscn";
    private const string TriggerMarker = "ALLEYCAT_SHUTDOWN_PROBE_TRIGGER";
    private const string ExitMarker = "ALLEYCAT_SHUTDOWN_PROBE_EXIT_TREE";
    private const string DisposedLog = "Shared model backend disposed for ";
    private const string ExitRequestedLog = "Game exit requested with exit code 0.";
    private const string MenuExitLog = "Game menu 'Exit Game' confirmed";
    private const string QuitMarker = "ALLEYCAT_SHUTDOWN_PROBE_QUIT";
    private const string EmptyScene = "res://assets/scenes/empty.tscn";
    private static readonly string[] _sharedKeyTypes = ["SomaModelKey", "SupertonicPipelineKey"];
    private static readonly string[] _modelCategories =
        ["AlleyCat.Models", "AlleyCat.Motion", "AlleyCat.Navigation", "AlleyCat.Speech", "OnnxRuntime"];
    // Cold CUDA loads for three NPCs plus the drain; a hang guard only, not a criterion.
    private static readonly TimeSpan _processBudget = TimeSpan.FromSeconds(600);
    // A frame gap of a quarter second is a visible freeze; a scene thread blocked on the drain would instead stall
    // until the in-flight native calls return, which is the whole remaining inference time.
    private const double MaxFrameGapMs = 250;

    /// <summary>
    /// Confirming "Exit Game" in the real game menu while model work is in flight drains without freezing the scene
    /// thread, disposes every shared backend before quitting and exits with code 0.
    /// </summary>
    [Fact]
    public Task MenuExit_DuringInFlightModelWork_DrainsAndExitsCleanly() =>
        RunAsync(nameof(MenuExit_DuringInFlightModelWork_DrainsAndExitsCleanly), "menu");

    /// <summary>
    /// A root-window close request while model work is in flight drains without freezing the scene thread, disposes
    /// every shared backend before quitting and exits with code 0.
    /// </summary>
    [Fact]
    public Task RootWindowClose_DuringInFlightModelWork_DrainsAndExitsCleanly() =>
        RunAsync(nameof(RootWindowClose_DuringInFlightModelWork_DrainsAndExitsCleanly), "window");

    /// <summary>
    /// Leaving the scene through the game's scene change (the loading screen's <c>ChangeSceneToPacked</c>) while model
    /// work is in flight keeps the scene thread responsive while that work ends and every consumer releases its
    /// handle, with no model errors; the process then quits through the root-window path, disposing every shared
    /// backend before quitting, and exits with code 0.
    /// </summary>
    [Fact]
    public Task SceneExit_DuringInFlightModelWork_ReleasesConsumersThenExitsCleanly() =>
        RunAsync(nameof(SceneExit_DuringInFlightModelWork_ReleasesConsumersThenExitsCleanly), "scene");

    private static async Task RunAsync(string fact, string path)
    {
        HostIsolation.RequireNoOtherGodotProcess();
        string directory = ProjectSettings.GlobalizePath($"res://temp/shared-ai-models/{fact}");
        _ = Directory.CreateDirectory(directory);
        string stamp = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}";
        string reportFile = Path.Combine(directory, $"{stamp}.json");
        string logFile = Path.Combine(directory, $"{stamp}.log");

        (int? exitCode, List<string> output) = await RunProbeAsync(path, reportFile);
        await File.WriteAllLinesAsync(logFile, output);

        Assert.True(exitCode is not null, $"The probe process did not exit within {_processBudget.TotalSeconds} s; log {logFile}.");
        Assert.True(File.Exists(reportFile), $"The probe wrote no report (exit code {exitCode}); log {logFile}.");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(reportFile));
        JsonElement report = document.RootElement;

        // Preconditions: without these a pass would not show shutdown during in-flight work.
        Assert.True(report.GetProperty("setupError").ValueKind == JsonValueKind.Null,
            $"Probe setup failed: {report.GetProperty("setupError")}");
        Assert.True(report.GetProperty("triggered").GetBoolean(), "The exit path was never triggered.");
        if (path == "menu")
        {
            Assert.True(report.GetProperty("menuOpened").GetBoolean(), "The game menu did not open.");
            Assert.Equal("ExitGame", report.GetProperty("menuSelection").GetString());
        }

        if (path == "scene")
        {
            AssertSceneExit(report, output, logFile);
        }

        Assert.All(report.GetProperty("npcs").EnumerateArray(), npc => Assert.True(
            npc.GetProperty("generationsAtTrigger").GetInt32() > 0,
            $"{npc.GetProperty("name")} completed no MotionBricks generation before the exit."));
        Assert.True(report.GetProperty("motionInFlightAtTrigger").GetInt32() > 0, "No MotionBricks call was in flight at the exit.");
        Assert.True(report.GetProperty("speechPendingAtTrigger").GetInt32() > 0, "No Supertonic synthesis was pending at the exit.");

        // Responsiveness: the scene thread keeps producing frames while the in-flight calls drain.
        Assert.True(report.GetProperty("framesAfterTrigger").GetInt32() >= 2,
            $"The scene thread produced {report.GetProperty("framesAfterTrigger")} frames between the exit and the quit.");
        double maxGap = report.GetProperty("maxFrameGapAfterTriggerMs").GetDouble();
        Assert.True(maxGap < MaxFrameGapMs, $"The scene thread stalled for {maxGap:F0} ms during the shutdown drain.");

        // The quit waits for the native call that was in flight at the exit.
        Assert.True(report.GetProperty("motionReturnsAfterTrigger").GetInt32() > 0,
            "No in-flight MotionBricks call returned after the exit was requested.");
        Assert.True(report.GetProperty("lastMotionReturnAfterTriggerMs").GetDouble() <= report.GetProperty("exitAfterTriggerMs").GetDouble(),
            "The process quit before the in-flight MotionBricks call returned.");

        // Log order: exit request, then every shared backend disposed, then the quit.
        int trigger = IndexOf(output, TriggerMarker);
        int exitRequested = IndexOf(output, ExitRequestedLog);
        int exitTree = IndexOf(output, ExitMarker);
        Assert.True(trigger >= 0 && exitRequested > trigger, $"No exit request was logged after the trigger; log {logFile}.");
        if (path == "menu")
        {
            int menuExit = IndexOf(output, MenuExitLog);
            Assert.True(menuExit > trigger && menuExit < exitRequested, $"The exit request did not come from the game menu; log {logFile}.");
        }

        if (path == "scene")
        {
            Assert.True(exitRequested > IndexOf(output, QuitMarker),
                $"The exit was requested before the scene-exit drain had been observed; log {logFile}.");
        }

        Assert.True(exitTree > exitRequested, $"The probe did not leave the tree after the exit request; log {logFile}.");
        foreach (string keyType in _sharedKeyTypes)
        {
            int disposed = output.FindIndex(line => line.Contains(DisposedLog + keyType, StringComparison.Ordinal));
            Assert.True(disposed > exitRequested && disposed < exitTree,
                $"The {keyType} backend was not disposed between the exit request and the quit; log {logFile}.");
        }

        Assert.Equal(0, exitCode);
    }

    // Scene exit: the scene change frees the NPCs while their work is in flight; the scene thread stays responsive
    // while that work ends and every consumer releases its handle, before the quit is requested at all.
    private static void AssertSceneExit(JsonElement report, List<string> output, string logFile)
    {
        JsonElement scene = report.GetProperty("sceneExit");
        Assert.True(scene.GetProperty("sceneChangeCompleted").GetBoolean(), "The loading screen did not complete the scene change.");
        Assert.Equal(EmptyScene, scene.GetProperty("sceneAfterExit").GetString());
        Assert.True(scene.GetProperty("npcSceneFreed").GetBoolean(), "The NPC scene was not freed by the scene change.");
        Assert.True(scene.GetProperty("drainSettled").GetBoolean(),
            $"In-flight work or handle release did not settle within the drain budget; log {logFile}.");

        Assert.True(scene.GetProperty("framesDuringDrain").GetInt32() >= 2,
            $"The scene thread produced {scene.GetProperty("framesDuringDrain")} frames during the scene-exit drain.");
        double drainGap = scene.GetProperty("maxFrameGapDuringDrainMs").GetDouble();
        Assert.True(drainGap < MaxFrameGapMs, $"The scene thread stalled for {drainGap:F0} ms during the scene-exit drain.");

        // In-flight work ended: the native call returned, synthesis settled without a fault, and every consumer's
        // handle was released (MotionBricks handle completion, Supertonic handle disposed).
        Assert.Equal(0, scene.GetProperty("motionInFlightAfterDrain").GetInt32());
        Assert.All(report.GetProperty("npcs").EnumerateArray(), npc =>
        {
            string name = npc.GetProperty("name").GetString()!;
            Assert.True(npc.GetProperty("workerAtDrain").GetString() == nameof(TaskStatus.RanToCompletion),
                $"{name} MotionBricks handle did not release cleanly: {npc.GetProperty("workerAtDrain")}.");
            Assert.True(npc.GetProperty("pipelineDisposedAtDrain").GetBoolean(), $"{name} Supertonic handle was not released.");
            string? speech = npc.GetProperty("speechAtDrain").GetString();
            Assert.True(speech is nameof(TaskStatus.RanToCompletion) or nameof(TaskStatus.Canceled),
                $"{name} synthesis ended as {speech}, expected completed or cancelled: " +
                $"{npc.GetProperty("speechFault").GetString()?.Split('\n')[0]}");
        });

        int trigger = IndexOf(output, TriggerMarker);
        int quit = IndexOf(output, QuitMarker);
        Assert.True(quit > trigger, $"The quit was not requested after the scene exit; log {logFile}.");
        List<string> modelErrors = [.. output.Skip(trigger).Where(line =>
            (line.Contains("[Error]", StringComparison.Ordinal) || line.Contains("[Critical]", StringComparison.Ordinal)
                || line.Contains("Unhandled exception", StringComparison.Ordinal))
            && _modelCategories.Any(category => line.Contains(category, StringComparison.Ordinal)))];
        Assert.True(modelErrors.Count == 0, $"Model code logged errors after the scene exit: {string.Join(" | ", modelErrors)}");
    }

    private static int IndexOf(List<string> lines, string text) =>
        lines.FindIndex(line => line.Contains(text, StringComparison.Ordinal));

    private static async Task<(int? ExitCode, List<string> Output)> RunProbeAsync(string path, string reportFile)
    {
        string godot = System.Environment.GetEnvironmentVariable("GODOT_PATH") is { Length: > 0 } configured
            ? configured
            : "godot-mono";
        var start = new ProcessStartInfo(godot)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in new[]
        {
            "--headless", "--xr-mode", "off", "--path", ProjectSettings.GlobalizePath("res://"), ProbeScene,
            "--", "--shutdown-path", path, "--shutdown-report", reportFile,
        })
        {
            start.ArgumentList.Add(argument);
        }

        var output = new List<string>();
        using var process = new Process { StartInfo = start };
        void Capture(object sender, DataReceivedEventArgs line)
        {
            if (line.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.Add(line.Data);
            }
        }

        process.OutputDataReceived += Capture;
        process.ErrorDataReceived += Capture;
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = new CancellationTokenSource(_processBudget);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            lock (output)
            {
                return (null, [.. output]);
            }
        }

        // The parameterless wait flushes the redirected streams.
        await process.WaitForExitAsync();
        lock (output)
        {
            return (process.ExitCode, [.. output]);
        }
    }
}
