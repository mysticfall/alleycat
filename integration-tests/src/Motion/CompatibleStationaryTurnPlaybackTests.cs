using System.Diagnostics;
using System.Reflection;
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
    private const int CompatibleTurnTicks = 3180 - 895;

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task CompatibleTurn_ChangesRetainPendingOwnership() => CheckCompatibleTurnAsync(false);

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public Task CompatibleTurn_ChangesRetainActiveProgress() => CheckCompatibleTurnAsync(true);

    private static IEnumerable<(int Tick, float Magnitude)> CompatibleTurnChanges()
    {
        // Synthetic replacement, not reconstructed headset evidence. Retain the original
        // 165 near-full changes and 2,285 fixed-step window; include both short bursts and holds.
        for (int pair = 0; pair < 82; pair++)
        {
            yield return (1 + (pair * 27), 1);
            yield return (2 + (pair * 27), .95f);
        }
        yield return (CompatibleTurnTicks - 1, 1);
    }

    private static async Task CheckCompatibleTurnAsync(bool active)
    {
        var session = new CompatibleTurnSession();
        StationaryTurnFixture fixture = await StationaryTurnFixture.CreateAsync();
        string path;
        Task trace;
        await using (fixture)
        {
            try
            {
                PlayerMotionBricks motion = fixture.Motion;
                motion.RecordInputTrace = true;
                motion.InputTraceDirectoryOverride = ProjectSettings.GlobalizePath("res://temp/motionbricks-fixture-repair/self-contained/compatible-turn");
                motion.InputTraceLabel = active ? "active" : "pending";
                motion.PoseTraceEvidenceLabel = "synthetic-compatible-turn-no-native-inference";
                motion._PhysicsProcess(0); // Open the production recording boundary without advancing simulated time.
                WalkingExperimentDriver driver = fixture.Driver;
                SomaInferenceWorker worker = fixture.UseSession(session);
                var submissions = new List<(long Ticket, Transform3D[][] Target, float RelativeYaw, MetadataStationaryTurn.Snapshot Owner)>();
                driver.WindowSubmitted += (ticket, _, target) =>
                {
                    Vector3 direction = fixture.Player.GlobalBasis.Inverse() * target[0][0].Basis.Z;
                    submissions.Add((ticket, target, Mathf.RadToDeg(Mathf.Atan2(direction.X, direction.Z)),
                        Assert.IsType<MetadataStationaryTurn.Snapshot>(driver.LastMetadataTurnRequest)));
                };
                async Task PublishAndPlayAsync()
                {
                    session.Release();
                    await WaitForMotionPublicationAsync(fixture.Tree, worker);
                    fixture.Tick();
                    Assert.True(driver.IsPlaying);
                }
                motion.SetStick(false, new Vector2(.95f, 0));
                fixture.Tick();
                await session.Entered.WaitAsync(TimeSpan.FromSeconds(5));
                if (active)
                {
                    await PublishAndPlayAsync();
                }
                Assert.Equal(!active, driver.RecordingPending);
                long ticket = submissions[0].Ticket;
                string immutable = CompatibleTurnPoseHash(submissions[0].Target);
                int requests = driver.Requests;
                long epoch = CompatibleTurnEpoch(motion);
                long revision = driver.ReviewedRoles.Selection.Revision;
                int retired = 0;
                driver.IntentRetired += () => retired++;
                double before = (double)ExhaustionField("_playTime").GetValue(driver)!;
                int tick = 0;
                foreach ((int nextTick, float magnitude) in CompatibleTurnChanges())
                {
                    for (; tick < nextTick; tick++)
                    {
                        fixture.Tick();
                    }
                    double atChange = (double)ExhaustionField("_playTime").GetValue(driver)!;
                    motion.SetStick(false, new Vector2(magnitude, 0));
                    Assert.Equal(magnitude, motion.TurnIntent);
                    Assert.Equal(.95f, driver.SubmittedTurnIntent);
                    if (active)
                    {
                        Assert.Equal(.95f, driver.PlayedTurnIntent);
                    }
                    Assert.Equal(0, retired);
                    Assert.Equal(epoch, CompatibleTurnEpoch(motion));
                    Assert.Equal(requests, driver.Requests);
                    Assert.Equal(revision, driver.ReviewedRoles.Selection.Revision);
                    Assert.Equal(atChange, (double)ExhaustionField("_playTime").GetValue(driver)!);
                    Assert.Equal(!active, driver.RecordingPending);
                    Assert.Equal(active, driver.IsPlaying);
                    Assert.Equal(ticket, (long)ExhaustionField("_ticket").GetValue(driver)!);
                }
                for (; tick < CompatibleTurnTicks; tick++)
                {
                    fixture.Tick();
                }
                Assert.Equal(1, motion.TurnIntent); // The requested intensity changes, not the owning cycle.
                Assert.Equal(immutable, CompatibleTurnPoseHash(submissions[0].Target));
                if (active)
                {
                    Assert.True((double)ExhaustionField("_playTime").GetValue(driver)! > before + 18);
                }
                else
                {
                    await PublishAndPlayAsync();
                }
                Assert.Equal(ticket, driver.LastAppliedRequest);
                // Exhaust only this synthetic constant clip. The second cue must use the original
                // cycle owner even though the latest same-direction input is now full magnitude.
                for (int i = 0; i < 24 * StationaryTurnFixture.TicksPerSecond && submissions.Count < 2; i++)
                {
                    fixture.Tick();
                }
                Assert.Equal(2, submissions.Count);
                using var angular = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(
                    "res://assets/animations/locomotion/feminine/turn-angular-reference.json")));
                JsonElement firstCue = angular.RootElement.GetProperty("right_turn").GetProperty("recoveryTrial").GetProperty("events")[0];
                float firstYaw = (float)(firstCue.GetProperty("sourceAngularProgressRadians").GetDouble() * 180 / Math.PI);
                float mappedYaw = firstYaw * 114 / 91.3519f;
                Assert.InRange(submissions[0].RelativeYaw, mappedYaw - .001f, mappedYaw + .001f);
                Assert.Equal(firstCue.GetProperty("id").GetString(), submissions[0].Owner.EventId);
                Assert.Equal(.95f, (float)submissions[0].Owner.Magnitude);
                Assert.Equal(.95f, driver.SubmittedTurnIntent);
                Assert.Equal(.95f, (float)submissions[1].Owner.Magnitude); // The second lift belongs to the original cycle.
                Assert.Equal("lift_LeftFoot", submissions[1].Owner.EventId);
                Assert.Equal(epoch, CompatibleTurnEpoch(motion));
                Assert.Equal(revision, driver.ReviewedRoles.Selection.Revision);
                Assert.Equal(immutable, CompatibleTurnPoseHash(submissions[0].Target));
                if (driver.LastAppliedRequest != submissions[1].Ticket)
                {
                    await WaitForMotionPublicationAsync(fixture.Tree, worker);
                    fixture.Tick();
                }
                Assert.Equal(submissions[1].Ticket, driver.LastAppliedRequest);
                Assert.True(driver.IsPlaying);
                // Release is immediate after retained active playback; a separate control covers pending cancellation.
                motion.SetStick(false, Vector2.Zero);
                Assert.Equal(epoch + 1, CompatibleTurnEpoch(motion));
                Assert.False(driver.IsPlaying);
                Assert.False(driver.RecordingPending);
                Assert.Equal(1, retired);
                path = motion.InputPoseTracePath;
                Assert.NotEmpty(path);
                trace = Assert.IsAssignableFrom<Task>(motion.InputTraceCompletion);
            }
            finally
            {
                session.Release();
            }
        }
        await trace.WaitAsync(TimeSpan.FromSeconds(10));
        NavigationSessionReader.Entry[] entries = [.. NavigationSessionReader.ReadCheckpoint(path)];
        NavigationSessionReader.Entry[] submissionsReadBack = [.. entries.Where(e => e.Event.GetProperty("kind").GetString() == "submission")];
        Assert.Equal(2, submissionsReadBack.Length);
        Assert.All(submissionsReadBack, e => Assert.Equal(.95f, e.Event.GetProperty("data").GetProperty("submittedTurnIntent").GetSingle()));
        long firstSequence = submissionsReadBack[0].Event.GetProperty("sequence").GetInt64();
        long secondSequence = submissionsReadBack[1].Event.GetProperty("sequence").GetInt64();
        Assert.DoesNotContain(entries, e => e.Event.GetProperty("kind").GetString() == "input-retire" &&
            e.Event.GetProperty("sequence").GetInt64() > firstSequence && e.Event.GetProperty("sequence").GetInt64() < secondSequence);
        Assert.Contains(entries, e => e.Event.GetProperty("kind").GetString() == "input-input-acknowledged" &&
            e.Event.GetProperty("data").GetProperty("TurnIntent").GetSingle() == 1 &&
            e.Event.GetProperty("data").GetProperty("submittedTurnIntent").GetSingle() == .95f &&
            e.Event.GetProperty("data").GetProperty("playedTurnIntent").GetSingle() == (active ? .95f : 0));
    }

    /// <inheritdoc/>
    [Headless]
    [Fact]
    public async Task CompatibleTurn_IncompatibleChangesRetireAndLateReturnStaysStale()
    {
        var session = new CompatibleTurnSession();
        await using StationaryTurnFixture fixture = await StationaryTurnFixture.CreateAsync();
        try
        {
            PlayerMotionBricks motion = fixture.Motion;
            WalkingExperimentDriver driver = fixture.Driver;
            SomaInferenceWorker worker = fixture.UseSession(session);
            var consumed = new List<long>();
            var submitted = new List<long>();
            driver.PlaybackStarted += consumed.Add;
            driver.WindowSubmitted += (ticket, _, _) => submitted.Add(ticket);
            void Retires(Action change)
            {
                long epoch = CompatibleTurnEpoch(motion);
                change();
                Assert.Equal(epoch + 1, CompatibleTurnEpoch(motion));
                Assert.False(driver.RecordingPending);
                Assert.False(driver.IsPlaying);
            }
            motion.SetStick(false, new Vector2(1, 0));
            fixture.Tick();
            await session.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            long firstTicket = submitted[0], epoch = CompatibleTurnEpoch(motion);
            motion.SetStick(false, new Vector2(.95f, 0));
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.True(driver.RecordingPending);
            motion.SetStick(false, new Vector2(.6f, 0));
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.True(driver.RecordingPending);
            Assert.Equal(1f, driver.SubmittedTurnIntent);
            Retires(() => motion.SetStick(false, Vector2.Zero));
            Retires(() => motion.SetStick(false, new Vector2(.9f, 0))); // New intent cannot resurrect the cancelled ticket.
            fixture.Tick();
            Assert.True(driver.RecordingPending);
            Retires(() => motion.SetStick(false, new Vector2(-1, 0)));
            fixture.Tick();
            epoch = CompatibleTurnEpoch(motion);
            motion.SetStick(false, new Vector2(-.95f, 0));
            Assert.Equal(epoch, CompatibleTurnEpoch(motion));
            Assert.True(driver.RecordingPending);
            Retires(() => motion.SetStick(true, Vector2.Up)); // Movement/role switch remains immediate.
            motion.SetStick(true, Vector2.Zero);
            fixture.Tick();
            Retires(() => motion.SetStick(false, Vector2.Zero));
            motion.SetStick(false, new Vector2(1, 0));
            fixture.Tick();
            Retires(() => motion.SetStick(false, new Vector2(float.NaN, 0))); // Existing input sanitisation -> release.
            int discardedBefore = worker.DiscardedResults;
            session.Release();
            await WaitForMotionPublicationAsync(fixture.Tree, worker, firstTicket, discardedBefore);
            Assert.DoesNotContain(firstTicket, consumed);
            Assert.False(driver.RecordingPending);
            Assert.False(driver.IsPlaying);
            Assert.Equal("", motion.Failure);
        }
        finally
        {
            session.Release();
        }
    }

    private static long CompatibleTurnEpoch(PlayerMotionBricks motion) => (long)typeof(PlayerMotionBricks)
        .GetField("_inputEpoch", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(motion)!;

    private static string CompatibleTurnPoseHash(Transform3D[][] pose)
    {
        float[] values = NavigationRecordingSink.Pack(pose);
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static async Task WaitForMotionPublicationAsync(SceneTree tree, SomaInferenceWorker worker,
        long? ticket = null, int? discardedBefore = null)
    {
        // Returned observers execute before publication. Read the mailbox under the worker's
        // own lock without taking ownership, and do not advance the manually stepped controller while waiting.
        var gate = (Lock)_motionWorkerGate.GetValue(worker)!;
        long started = Stopwatch.GetTimestamp();
        var hangWatchdog = TimeSpan.FromSeconds(10);
        while (true)
        {
            lock (gate)
            {
                if (discardedBefore is int previous ? worker.DiscardedResults > previous :
                    _motionWorkerResult.GetValue(worker) is SomaInferenceWorker.Result result && (ticket is null || result.Request == ticket))
                {
                    return;
                }
            }
            // This is a hang diagnostic, not a simulated response deadline. Physics signals only yield the scene thread.
            if (Stopwatch.GetElapsedTime(started) >= hangWatchdog)
            {
                string expected = discardedBefore.HasValue ? "reject" : "publish";
                string request = ticket is long expectedTicket ? $"ticket {expectedTicket}" : "the current request";
                throw new TimeoutException($"Motion worker did not {expected} {request} within the {hangWatchdog.TotalSeconds:g}-second hang watchdog.");
            }
            await TestUtils.WaitForNextPhysicsFrameAsync(tree);
        }
    }

    private static readonly FieldInfo _motionWorkerGate =
        typeof(SomaInferenceWorker).GetField("_gate", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo _motionWorkerResult =
        typeof(SomaInferenceWorker).GetField("_result", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private sealed class StationaryTurnFixture : IAsyncDisposable
    {
        internal const int TicksPerSecond = 120;
        private const double Delta = 1.0 / TicksPerSecond;
        private readonly ProbeGame _root = new() { Name = "StationaryTurnFixture" };

        private StationaryTurnFixture()
        {
            Player.AddChild(MotionBricksIntegrationTests.MountMotionModel(true, out Skeleton3D skeleton));
            Skeleton = skeleton;
            typeof(AlleyCat.Character.Character).GetField("_motionTarget", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(Player, skeleton);
            Player.AddChild(new AnimationTree { Name = "AnimationTree", Active = false });
            // Player locomotion resolves its reviewed set through the character's catalogue (ANIM-003).
            Player.AddChild(MotionFixtureCatalogue.Create(npc: false));
            Player.AddChild(Motion);
            _root.AddChild(Player);
        }

        internal SceneTree Tree { get; } = TestUtils.GetSceneTree();
        internal AlleyCat.Character.Character Player { get; } = new() { ProcessMode = Node.ProcessModeEnum.Disabled };
        internal PlayerMotionBricks Motion { get; } = new();
        internal Skeleton3D Skeleton
        {
            get;
        }
        internal WalkingExperimentDriver Driver => Assert.IsType<WalkingExperimentDriver>(Motion.ContinuousDriver);

        internal static async Task<StationaryTurnFixture> CreateAsync()
        {
            await TestUtils.WaitForNextFrameAsync(TestUtils.GetSceneTree());
            var fixture = new StationaryTurnFixture();
            try
            {
                fixture.Tree.Root.AddChild(fixture._root);
                await TestUtils.WaitForNextPhysicsFrameAsync(fixture.Tree);
                Assert.Equal("", fixture.Motion.Failure);
                Assert.Null(fixture.Driver.Completion);
                fixture.Skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                fixture.Skeleton.GetParent().GetParent<Skeleton3D>().ModifierCallbackModeProcess =
                    Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
                // Seed the real driver's observed context at its 30 Hz history cadence; no tracking or XR involved.
                for (int i = 0; i < 40; i++)
                {
                    fixture.Tick();
                }
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        internal SomaInferenceWorker UseSession(SomaInferenceWorker.ISession session)
        {
            Assert.Null(Driver.Completion);
            SomaInferenceWorker worker = _root.MotionWorkers.Create(() => session, Driver.ReturnedObserved);
            ExhaustionField("_worker").SetValue(Driver, worker);
            return worker;
        }

        internal void Tick()
        {
            Driver.Advance(Delta, Motion.MovementIntent, Basis.Identity, Motion.TurnIntent);
            Driver.Observe();
        }

        internal async Task TickThroughPublicationAsync(SomaInferenceWorker worker)
        {
            int requests = Driver.Requests;
            Tick();
            if (Driver.Requests != requests && Driver.RecordingPending)
            {
                await WaitForMotionPublicationAsync(Tree, worker);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _root.DrainMotionAndDiagnosticsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                _root.QueueFree();
                await TestUtils.WaitForPhysicsFramesAsync(Tree, 2);
            }
        }
    }

    private sealed class CompatibleTurnSession : SomaInferenceWorker.ISession
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _continue = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;
        internal void Release() => _ = _continue.TrySetResult();

        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            _ = _entered.TrySetResult();
            _continue.Task.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return [.. Enumerable.Range(0, 700).Select(_ => (Transform3D[])context[3].Clone())];
        }
        public void Dispose()
        {
        }
    }
}
