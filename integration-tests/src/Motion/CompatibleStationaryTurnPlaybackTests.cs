using System.Security.Cryptography;
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
    private const string CompatibleTurnEvidence = "res://temp/motionbricks/ctrl-compatible-turn-playback-01/";

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task CompatibleTurn_ArchivedChangesRetainPendingOwnership() => CheckCompatibleTurnAsync(false);

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task CompatibleTurn_ArchivedChangesRetainActiveProgress() => CheckCompatibleTurnAsync(true);

    private static JsonElement[] CompatibleTurnChanges()
    {
        string path = ProjectSettings.GlobalizePath("res://temp/motionbricks/player-virtual-target-01/headset/virtual-20261114-072920-252-8267f4b094aa48c48ac3b06fe2edce0a.jsonl");
        string receipt = ProjectSettings.GlobalizePath("res://temp/motionbricks/ctrl-full-turn-footwork-01/input-hashes.json");
        using var hashes = JsonDocument.Parse(File.ReadAllText(receipt));
        Assert.Equal(hashes.RootElement.GetProperty("game/temp/motionbricks/player-virtual-target-01/headset/" + Path.GetFileName(path)).GetString(),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var changes = new List<JsonElement>();
        float previous = 0;
        foreach (string line in File.ReadLines(path))
        {
            JsonElement entry = JsonSerializer.Deserialize<JsonElement>(line);
            if (entry.GetProperty("kind").GetString() != "input-acknowledged")
            {
                continue;
            }
            JsonElement d = entry.GetProperty("data");
            float turn = d.GetProperty("TurnIntent").GetSingle();
            if (turn != previous && previous >= .95f && turn >= .95f)
            {
                changes.Add(d);
            }
            previous = turn;
        }
        Assert.Equal(165, changes.Count);
        return [.. changes];
    }

    private static async Task CheckCompatibleTurnAsync(bool active)
    {
        JsonElement[] changes = CompatibleTurnChanges();
        var session = new CompatibleTurnSession();
        ClosureFixture? fixture = null;
        Task? trace = null;
        string path = "";
        try
        {
            fixture = await ClosureFixture.CreateAsync(.75f, motion =>
            {
                motion.RecordInputTrace = true;
                motion.InputTraceLabel = active ? "synthetic-compatible-active" : "synthetic-compatible-pending";
                motion.InputTraceDirectoryOverride = ProjectSettings.GlobalizePath(CompatibleTurnEvidence + "synthetic");
                motion.PoseTraceEvidenceLabel = "synthetic-compatible-turn-no-native-inference";
            });
            PlayerMotionBricks motion = fixture.Motion;
            WalkingExperimentDriver driver = motion.ContinuousDriver!;
            Assert.Null(driver.Completion);
            Assert.NotNull(driver.ReviewedRoles);
            // Only ownership/timing is manually stepped. No displayed-quality or post-modifier feedback claim.
            fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
            SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => session, driver.ReturnedObserved);
            ExhaustionField("_worker").SetValue(driver, worker);
            var consumed = new List<long>();
            var submissions = new List<(long Ticket, Transform3D[][] Target, float RelativeYaw)>();
            var turnOwners = new List<MetadataStationaryTurn.Snapshot>();
            driver.PlaybackStarted += consumed.Add;
            driver.WindowSubmitted += (ticket, _, target) =>
            {
                Vector3 direction = fixture.Player.GlobalBasis.Inverse() * target[0][0].Basis.Z;
                submissions.Add((ticket, target, Mathf.RadToDeg(Mathf.Atan2(direction.X, direction.Z))));
                turnOwners.Add(Assert.IsType<MetadataStationaryTurn.Snapshot>(driver.LastMetadataTurnRequest));
            };
            void Tick()
            {
                driver.Advance(1.0 / 120, motion.MovementIntent, Basis.Identity, motion.TurnIntent);
                driver.Observe();
            }
            async Task PollAsync(Func<bool> condition)
            {
                for (int i = 0; i < 120 && !condition(); i++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(condition());
            }
            motion.SetStick(false, new Vector2(.95f, 0));
            Tick();
            await PollAsync(() => session.Entered.IsSet);
            if (active)
            {
                session.Continue.Set();
                await PollAsync(() => session.Returned.IsSet);
                for (int i = 0; i < 120 && !driver.IsPlaying; i++)
                {
                    Tick();
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(driver.IsPlaying);
            }
            Assert.Equal(!active, driver.RecordingPending);
            long ticket = submissions[0].Ticket;
            string immutable = CompatibleTurnPoseHash(submissions[0].Target);
            int requests = driver.Requests;
            long epoch = CompatibleTurnEpoch(motion);
            long revision = driver.ReviewedRoles!.Selection.Revision;
            int retired = 0;
            driver.IntentRetired += () => retired++;
            double before = (double)ExhaustionField("_playTime").GetValue(driver)!;
            int archivedFrame = 895;
            foreach (JsonElement d in changes)
            {
                int nextFrame = d.GetProperty("physicsFrame").GetInt32();
                for (; archivedFrame < nextFrame; archivedFrame++)
                {
                    Tick();
                }
                double atChange = (double)ExhaustionField("_playTime").GetValue(driver)!;
                motion.SetStick(false, new Vector2(d.GetProperty("rawRight")[0].GetSingle(), d.GetProperty("rawRight")[1].GetSingle()), "archived-near-full");
                Assert.Equal(0, retired);
                Assert.Equal(epoch, CompatibleTurnEpoch(motion));
                Assert.Equal(requests, driver.Requests);
                Assert.Equal(revision, driver.ReviewedRoles.Selection.Revision);
                Assert.Equal(atChange, (double)ExhaustionField("_playTime").GetValue(driver)!);
                Assert.Equal(!active, driver.RecordingPending);
                Assert.Equal(active, driver.IsPlaying);
                Assert.Equal(ticket, (long)ExhaustionField("_ticket").GetValue(driver)!);
            }
            for (; archivedFrame < 3180; archivedFrame++)
            {
                Tick();
            }
            Assert.Equal(1, motion.TurnIntent); // Independent archived final intensity; original submitted owner remains .95.
            Assert.Equal(immutable, CompatibleTurnPoseHash(submissions[0].Target));
            if (active)
            {
                Assert.True((double)ExhaustionField("_playTime").GetValue(driver)! > before + 18);
            }
            else
            {
                session.Continue.Set();
                await PollAsync(() => session.Returned.IsSet);
                for (int i = 0; i < 120 && !driver.IsPlaying; i++)
                {
                    Tick();
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(driver.IsPlaying);
            }
            Assert.Equal(ticket, driver.LastAppliedRequest);
            // Exhaust only this synthetic constant clip; the next eligible boundary must use latest intent without another SetStick.
            driver.Advance(24, Vector2.Zero, Basis.Identity, motion.TurnIntent);
            Tick();
            Tick();
            Assert.Equal(2, submissions.Count);
            using var angular = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(
                "res://data/motionbricks/reviewed-feminine/turn-angular-reference.json")));
            JsonElement firstCue = angular.RootElement.GetProperty("right_turn").GetProperty("recoveryTrial").GetProperty("events")[0];
            float firstYaw = (float)(firstCue.GetProperty("sourceAngularProgressRadians").GetDouble() * 180 / Math.PI);
            Assert.InRange(submissions[0].RelativeYaw, firstYaw - .001f, firstYaw + .001f);
            Assert.Equal(firstCue.GetProperty("id").GetString(), turnOwners[0].EventId);
            Assert.Equal(.95f, (float)turnOwners[0].Magnitude);
            Assert.Equal(1, turnOwners[1].Magnitude); // Latest intent applies at the next eligible cue, not a fixed 60-degree target.
            Assert.Equal("recover_after_RightFoot", turnOwners[1].EventId);
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.Equal(revision, driver.ReviewedRoles.Selection.Revision);
            Assert.Equal(immutable, CompatibleTurnPoseHash(submissions[0].Target));
            for (int i = 0; i < 120 && driver.LastAppliedRequest != submissions[1].Ticket; i++)
            {
                Tick();
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.Equal(submissions[1].Ticket, driver.LastAppliedRequest);
            Assert.True(driver.IsPlaying);
            // Release is immediate after retained active playback; a separate control covers pending cancellation.
            motion.SetStick(false, Vector2.Zero);
            Assert.Equal(epoch + 1, CompatibleTurnEpoch(motion));
            Assert.False(driver.IsPlaying);
            Assert.False(driver.RecordingPending);
            Assert.Equal(1, retired);
            trace = motion.InputTraceCompletion;
            path = motion.InputPoseTracePath;
            File.WriteAllText(ProjectSettings.GlobalizePath(CompatibleTurnEvidence + (active ? "active-result.json" : "pending-result.json")), JsonSerializer.Serialize(new
            {
                evidence = "synthetic-ownership-not-gait",
                transitions = changes.Length,
                ticket,
                epoch,
                revision,
                requestsDuringFluctuations = requests,
                retainedTargetHash = immutable,
                firstYaw = submissions[0].RelativeYaw,
                nextYaw = submissions[1].RelativeYaw,
                unchangedArchivedWindow = "895..3180 manual step timing; no archived model output reused",
                path
            }));
        }
        finally
        {
            session.Continue.Set();
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
            if (trace is not null)
            {
                await trace;
            }
        }
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(path)];
        NavigationSessionReader.Entry[] firstTwo = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "submission").Take(2)];
        Assert.Equal(.95f, firstTwo[0].Event.GetProperty("data").GetProperty("submittedTurnIntent").GetSingle());
        Assert.Equal(1, firstTwo[1].Event.GetProperty("data").GetProperty("submittedTurnIntent").GetSingle());
        Assert.DoesNotContain(entries, e => e.Event.GetProperty("kind").GetString() == "input-retire" &&
            e.Event.GetProperty("sequence").GetInt64() > firstTwo[0].Event.GetProperty("sequence").GetInt64() &&
            e.Event.GetProperty("sequence").GetInt64() < firstTwo[1].Event.GetProperty("sequence").GetInt64());
        Assert.Contains(entries, e => e.Event.GetProperty("kind").GetString() == "input-input-acknowledged" &&
            e.Event.GetProperty("data").GetProperty("TurnIntent").GetSingle() == 1 &&
            e.Event.GetProperty("data").GetProperty("submittedTurnIntent").GetSingle() == .95f);
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public async Task CompatibleTurn_IncompatibleChangesRetireAndLateReturnStaysStale()
    {
        var session = new CompatibleTurnSession();
        ClosureFixture? fixture = null;
        try
        {
            fixture = await ClosureFixture.CreateAsync(.75f);
            fixture.Player.ProcessMode = Node.ProcessModeEnum.Disabled;
            PlayerMotionBricks motion = fixture.Motion;
            WalkingExperimentDriver driver = motion.ContinuousDriver!;
            Assert.Null(driver.Completion);
            SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => session);
            ExhaustionField("_worker").SetValue(driver, worker);
            var consumed = new List<long>();
            var submitted = new List<long>();
            driver.PlaybackStarted += consumed.Add;
            driver.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
            void Tick()
            {
                driver.Advance(1.0 / 120, motion.MovementIntent, Basis.Identity, motion.TurnIntent);
            }

            void Retires(Action change)
            {
                long epoch = CompatibleTurnEpoch(motion);
                change();
                Assert.Equal(epoch + 1, CompatibleTurnEpoch(motion));
                Assert.False(driver.RecordingPending);
                Assert.False(driver.IsPlaying);
            }
            motion.SetStick(false, new Vector2(1, 0));
            Tick();
            for (int i = 0; i < 120 && !session.Entered.IsSet; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.True(session.Entered.IsSet);
            long firstTicket = submitted[0], epoch = CompatibleTurnEpoch(motion);
            motion.SetStick(false, new Vector2(.95f, 0));
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.True(driver.RecordingPending);
            Retires(() => motion.SetStick(false, new Vector2(.9f, 0))); // No cumulative 1 -> .95 -> .9 widening.
            Retires(() => motion.SetStick(false, new Vector2(.95f, 0))); // No resurrection of retired owner.
            Tick();
            Assert.True(driver.RecordingPending);
            Retires(() => motion.SetStick(false, new Vector2(-1, 0)));
            Tick();
            epoch = CompatibleTurnEpoch(motion);
            motion.SetStick(false, new Vector2(-.95f, 0));
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.True(driver.RecordingPending);
            Retires(() => motion.SetStick(true, Vector2.Up)); // Movement/role switch remains immediate.
            motion.SetStick(true, Vector2.Zero);
            Tick();
            Retires(() => motion.SetStick(false, Vector2.Zero));
            motion.SetStick(false, new Vector2(1, 0));
            Tick();
            Retires(() => motion.SetStick(false, new Vector2(float.NaN, 0))); // Existing input sanitisation -> release.
            session.Continue.Set();
            for (int i = 0; i < 120 && worker.DiscardedResults == 0; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
            }
            Assert.True(worker.DiscardedResults > 0);
            Assert.DoesNotContain(firstTicket, consumed);
            Assert.False(driver.RecordingPending);
            Assert.False(driver.IsPlaying);
            Assert.Equal("", motion.Failure);
        }
        finally
        {
            session.Continue.Set();
            if (fixture is not null)
            {
                await fixture.DisposeAsync();
            }
        }
    }

    private static long CompatibleTurnEpoch(PlayerMotionBricks motion) => (long)typeof(PlayerMotionBricks)
        .GetField("_inputEpoch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(motion)!;

    private static string CompatibleTurnPoseHash(Transform3D[][] pose)
    {
        float[] values = NavigationRecordingSink.Pack(pose);
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed class CompatibleTurnSession : SomaInferenceWorker.ISession
    {
        internal readonly ManualResetEventSlim Entered = new(), Continue = new(), Returned = new();
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            Entered.Set();
            if (!Continue.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Synthetic ownership handshake not released.");
            }
            Transform3D[][] clip = [.. Enumerable.Range(0, 700).Select(_ => (Transform3D[])context[3].Clone())];
            Returned.Set();
            return clip;
        }
        public void Dispose()
        {
        }
    }
}
