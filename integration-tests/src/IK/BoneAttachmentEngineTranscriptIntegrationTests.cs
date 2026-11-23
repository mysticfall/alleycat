using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Captures the native Godot engine transcript while reference female player and NPC roles independently enter a
/// dedicated attachment-only fixture. The roles keep the production rig role installer and its template-subtree, body
/// parts, and dynamic physical rig installers, but omit MotionBricks, navigation, mind, speech, and voice wiring.
/// </summary>
public sealed class BoneAttachmentEngineTranscriptIntegrationTests
{
    private const string RunnerPath = "res://tests/ik/bone_attachment_role_transcript_runner.gd";
    private const string TranscriptRelativePath = "test-results/ik/bone-attachment-role-engine-transcript.log";
    private static readonly TimeSpan _runnerTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan _outputDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly Regex[] _invalidBoneIndexPatterns =
    [
        new(@"p_bone\s*=\s*-1", RegexOptions.CultureInvariant),
        new(@"bone_idx\s*=\s*-1", RegexOptions.CultureInvariant),
    ];
    private static readonly Regex _godotDiagnosticLinePattern = new(
        @"^(?:ERROR:|WARNING:).*",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex _ximInputMethodWarningPattern = new(
        @"^WARNING: Failed to create wd\.xic as the input method or its xim server may not have started yet\.$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// Verifies the actual engine transcript is clean while the fixture loads, then player and NPC roles are each
    /// instantiated, attached, and installed before initial native physics frames run.
    /// </summary>
    [Fact]
    public async Task MinimalRoleFixture_IndependentRoleAttachment_EmitsNoInvalidBoneIndexEngineOutput()
        => await AssertCleanEngineTranscriptAsync(
            RunnerPath,
            TranscriptRelativePath,
            [
                "fixture-load-start",
                "fixture-load-complete",
                "fixture-instantiated",
                "fixture-attached",
                "player-role-instantiated",
                "player-role-attached",
                "npc-role-instantiated",
                "npc-role-attached",
                "roles-ready",
                "initial-physics-frame-1",
                "initial-physics-frame-2",
                "complete",
            ]);

    private static async Task AssertCleanEngineTranscriptAsync(
        string runnerPath,
        string transcriptRelativePath,
        string[] expectedStages)
    {
        string gameDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")));
        string workspaceDirectory = Directory.GetParent(gameDirectory)?.FullName
            ?? throw new InvalidOperationException("Unable to resolve the workspace directory from the Godot project path.");
        string transcriptPath = Path.Combine(workspaceDirectory, transcriptRelativePath);
        string globalRunnerPath = ProjectSettings.GlobalizePath(runnerPath);

        _ = Directory.CreateDirectory(Path.GetDirectoryName(transcriptPath)!);
        DirectGodotRunResult runResult = await RunDirectGodotAsync(gameDirectory, globalRunnerPath);
        string transcript = BuildTranscript(runResult);
        await File.WriteAllTextAsync(transcriptPath, transcript, Encoding.UTF8);
        string retainedTranscript = await File.ReadAllTextAsync(transcriptPath, Encoding.UTF8);

        Assert.False(
            runResult.TimedOut,
            $"The direct Godot transcript runner did not exit within {_runnerTimeout.TotalSeconds:0} s and was killed. "
            + $"Partial transcript:{System.Environment.NewLine}{transcript}");
        Assert.Equal(0, runResult.ExitCode);
        Assert.Equal(transcript, retainedTranscript);
        AssertStageSequence(retainedTranscript, expectedStages);
        AssertNoUnexpectedEngineDiagnostics(retainedTranscript);

        Match[] invalidBoneIndexMatches =
        [.. _invalidBoneIndexPatterns.SelectMany(pattern => pattern.Matches(retainedTranscript).Cast<Match>())];
        Assert.Empty(invalidBoneIndexMatches);
    }

    private static async Task<DirectGodotRunResult> RunDirectGodotAsync(string gameDirectory, string runnerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = System.Environment.GetEnvironmentVariable("GODOT_PATH") ?? "godot-mono",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = gameDirectory,
        };
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add(gameDirectory);
        startInfo.ArgumentList.Add("--xr-mode");
        startInfo.ArgumentList.Add("off");
        // The Dummy audio driver keeps the runner off the host audio server: a real driver opens a PulseAudio
        // stream whose teardown can emit "pa_stream_disconnect error" environment noise into the asserted
        // transcript. The runners exercise bone attachment stages only, so no audio behaviour is affected.
        startInfo.ArgumentList.Add("--audio-driver");
        startInfo.ArgumentList.Add("Dummy");
        startInfo.ArgumentList.Add("--script");
        startInfo.ArgumentList.Add(runnerPath);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "The direct Godot transcript runner did not start.");

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync().WaitAsync(_runnerTimeout);
        }
        catch (TimeoutException)
        {
            // Keep the partial output: killing the tree closes its pipes so the pending reads complete.
            timedOut = true;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill; its pipes are already closing.
            }
        }

        return new DirectGodotRunResult(
            timedOut ? null : process.ExitCode,
            timedOut,
            await DrainAsync(standardOutputTask),
            await DrainAsync(standardErrorTask));
    }

    private static async Task<string> DrainAsync(Task<string> readTask)
    {
        try
        {
            return await readTask.WaitAsync(_outputDrainTimeout);
        }
        catch (TimeoutException)
        {
            return "<output stream did not close after the runner exited>";
        }
    }

    private static string BuildTranscript(DirectGodotRunResult runResult)
        => $"ExitCode: {(runResult.TimedOut ? "timed out" : runResult.ExitCode)}{System.Environment.NewLine}"
            + $"=== Standard Output ==={System.Environment.NewLine}{runResult.StandardOutput}"
            + $"{System.Environment.NewLine}=== Standard Error ==={System.Environment.NewLine}{runResult.StandardError}";

    private static void AssertStageSequence(string transcript, string[] stages)
    {
        int previousStageOffset = -1;
        foreach (string stage in stages)
        {
            int stageOffset = transcript.IndexOf($"ALLEYCAT_BONE_TRANSCRIPT_STAGE:{stage}", StringComparison.Ordinal);
            Assert.True(stageOffset >= 0, $"The retained engine transcript did not contain stage '{stage}'.");
            Assert.True(stageOffset > previousStageOffset, $"Engine transcript stage '{stage}' was out of order.");
            previousStageOffset = stageOffset;
        }
    }

    private static void AssertNoUnexpectedEngineDiagnostics(string transcript)
    {
        string[] unexpectedDiagnostics =
        [
            .. _godotDiagnosticLinePattern.Matches(transcript)
                .Cast<Match>()
                .Select(match => match.Value)
                // The Linux X11 backend may emit this exact environment warning when no XIM server is available.
                // It is the sole diagnostic exception: every ERROR: line and every other WARNING: line fails.
                .Where(line => !_ximInputMethodWarningPattern.IsMatch(line)),
        ];

        Assert.True(
            unexpectedDiagnostics.Length == 0,
            $"The retained Godot transcript contained unexpected engine diagnostics:{System.Environment.NewLine}"
            + string.Join(System.Environment.NewLine, unexpectedDiagnostics));
    }

    private sealed record DirectGodotRunResult(int? ExitCode, bool TimedOut, string StandardOutput, string StandardError);
}
