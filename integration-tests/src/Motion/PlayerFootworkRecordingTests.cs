using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Synthetic normal-VRIK wiring joins source/targets, late cancelled return, played fractions and actual final bones.</summary>
    [Headless]
    [Fact]
    public async Task FootworkRecording_JoinsRealBoundariesAndRetainsCancelledOutput()
    {
        var session = new FootworkRecordingSession();
        ClosureFixture? fixture = null;
        Task? recordingCompletion = null;
        string path = "";
        Transform3D finalFoot = default;
        int foot = -1;
        try
        {
            fixture = await ClosureFixture.CreateAsync(.75f, motion =>
            {
                motion.RecordInputTrace = true;
                motion.InputTraceLabel = "synthetic-footwork";
                motion.InputTraceDirectoryOverride = ProjectSettings.GlobalizePath("res://temp/motionbricks/ctrl-turn-footwork-recording-01/synthetic");
                motion.PoseTraceEvidenceLabel = "synthetic-worker-no-native-inference";
            });
            PlayerMotionBricks motion = fixture.Motion;
            WalkingExperimentDriver driver = motion.ContinuousDriver!;
            Assert.NotNull(driver.ReviewedRoles);
            Assert.Null(driver.Completion);
            Assert.False(fixture.VRIK.AnimationBodyDiagnostic);
            foot = fixture.Skeleton.FindBone("LeftFoot");
            fixture.Skeleton.SkeletonUpdated += () => finalFoot = fixture.Skeleton.GetBoneGlobalPose(foot);
            SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => session, driver.ReturnedObserved);
            ExhaustionField("_worker").SetValue(driver, worker);
            fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(1, 0));
            for (int i = 0; i < 150 && !session.SecondEntered.IsSet; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.True(session.SecondEntered.IsSet);
            long cancelled = motion.Requests; // Submission count is not the worker ticket; use saved submission records below.
            Assert.True(cancelled >= 2);
            fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
            session.Continue.Set();
            for (int i = 0; i < 120 && motion.Phase != "tracked-idle"; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.Equal("tracked-idle", motion.Phase);
            await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 3); // Retain an actual post-settlement observation, not a quality window.
            Assert.Equal("", motion.Failure);
            path = motion.InputPoseTracePath;
            recordingCompletion = motion.InputTraceCompletion;
        }
        finally
        {
            session.Continue.Set();
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            if (recordingCompletion is not null)
            {
                await recordingCompletion;
            }
        }
        Assert.True(session.Calls >= 3);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        Assert.Equal("completed", manifest.RootElement.GetProperty("state").GetString());
        Assert.Equal(0, manifest.RootElement.GetProperty("droppedRecords").GetInt64());
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("error").ValueKind);
        Assert.Equal("synthetic-worker-no-native-inference", manifest.RootElement.GetProperty("identity").GetProperty("evidence").GetString());
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(path)];
        NavigationSessionReader.Entry[] submissions = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "submission")];
        NavigationSessionReader.Entry first = submissions[0], second = submissions[1];
        JsonElement data = first.Event.GetProperty("data");
        Assert.Equal(7392, first.Values.Length);
        Assert.Equal("right_turn", data.GetProperty("source").GetProperty("role").GetString());
        Assert.Equal(0, data.GetProperty("source").GetProperty("initialPhaseFrames").GetDouble());
        using var angular = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(
            "res://assets/animations/locomotion/feminine/turn-angular-reference.json")));
        JsonElement firstCue = angular.RootElement.GetProperty("right_turn").GetProperty("recoveryTrial").GetProperty("events")[0];
        Assert.Equal(firstCue.GetProperty("frame").GetInt32(),
            data.GetProperty("source").GetProperty("metadata").GetProperty("samples")[0].GetProperty("OriginalFrame").GetInt32());
        Assert.Equal(firstCue.GetProperty("id").GetString(), data.GetProperty("metadataTurn").GetProperty("EventId").GetString());
        long firstTicket = data.GetProperty("ticket").GetInt64(), cancelledTicket = second.Event.GetProperty("data").GetProperty("ticket").GetInt64();
        Assert.Contains(entries, e => e.Event.GetProperty("kind").GetString() == "provider-returned" && e.Event.GetProperty("data").GetProperty("ticket").GetInt64() == cancelledTicket);
        Assert.DoesNotContain(entries, e => e.Event.GetProperty("kind").GetString() == "input-consume" && e.Event.GetProperty("data").GetProperty("ticket").GetInt64() == cancelledTicket);
        Assert.Contains(entries, e => e.Event.GetProperty("kind").GetString() == "skin-pose" &&
            e.Event.GetProperty("data").GetProperty("playedTicket").GetInt64() == firstTicket &&
            e.Event.GetProperty("data").GetProperty("playedFrame").GetDouble() > 0);
        NavigationSessionReader.Entry skin = entries.Last(e => e.Event.GetProperty("kind").GetString() == "skin-pose");
        int n = skin.Event.GetProperty("data").GetProperty("generatedBones").GetInt32();
        int at = (3 + (3 * n) + foot) * 12;
        Assert.Equal(NavigationRecordingSink.Pack([finalFoot]), skin.Values.AsSpan(at, 12).ToArray());
        File.WriteAllText(ProjectSettings.GlobalizePath("res://temp/motionbricks/ctrl-turn-footwork-recording-01/smoke-session-path.txt"), path);
    }

    /// <summary>Normal setup with recording disabled creates no companion/legacy trace and no model worker.</summary>
    [Headless]
    [Fact]
    public async Task FootworkRecording_DisabledLeavesNoCaptureOrWorker()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
        Assert.False(fixture.Motion.RecordInputTrace);
        Assert.Equal("", fixture.Motion.InputPoseTracePath);
        Assert.Equal("", fixture.Motion.InputTracePath);
        Assert.Null(fixture.Motion.WorkerCompletion);
    }

    private sealed class FootworkRecordingSession : SomaInferenceWorker.ISession
    {
        internal readonly ManualResetEventSlim SecondEntered = new();
        internal readonly ManualResetEventSlim Continue = new();
        internal int Calls;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            if (Interlocked.Increment(ref Calls) == 2)
            {
                SecondEntered.Set();
                if (!Continue.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Synthetic cancellation handshake was not released.");
                }
            }
            // Deliberately synthetic connector, not a model or an approved gait. Representative production rig application is retained.
            return [.. Enumerable.Range(0, 20).Select(f => context[3].Select((pose, j) => SomaMotionData.Blend(pose, target[0][j], f / 19f)).ToArray())];
        }
        public void Dispose()
        {
        }
    }
}
