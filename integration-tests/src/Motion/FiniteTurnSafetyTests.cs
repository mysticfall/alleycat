using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Source-coordinate control across the negative-angle cycle boundary; no provider.</summary>
    [Headless]
    [Fact]
    public void FiniteNAVTurn_RightCyclesPreserveAllFourSourceRowsAndPlacement()
    {
        string path = ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine");
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(Path.Combine(path, "set.json"), data);
        StationaryTurnReference reference = sampler.TurnReference("right_turn");
        var plan = new FiniteTerminalTurnPlan(reference, .4, -Math.PI);
        using var source = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "landing-ablation-01.json")));
        JsonElement samples = source.RootElement.GetProperty("samples");
        double entry = samples[1095].GetProperty("heading_radians").GetDouble();
        double span = samples[1155].GetProperty("heading_radians").GetDouble() - entry;
        Vector3 ground = new(2, 0, 3);
        foreach (FiniteTerminalTurnPlan.Cue cue in plan.Cues)
        {
            Transform3D[][] target = sampler.FiniteTurnTargets("right_turn", cue.Index, cue.VirtualFrame, cue.LocalPhase, cue.CycleStart - Math.PI, cue.Scale, ground);
            Assert.Equal(cue.VirtualFrame, sampler.Selection.WindowStart);
            for (int f = 0; f < 4; f++)
            {
                JsonElement raw = samples[cue.Frame + f - 1];
                double progress = raw.GetProperty("heading_radians").GetDouble() - entry;
                double expected = cue.CycleStart - Math.PI + (cue.Allocation * progress / span);
                double observed = Math.Atan2(target[f][0].Basis.Z.X, target[f][0].Basis.Z.Z);
                Assert.InRange(Math.Abs(Math.IEEERemainder(expected - observed, Math.Tau)), 0, 1e-5);
                for (int j = 0; j < 77; j++)
                {
                    float[] row = [.. raw.GetProperty("pose")[j].EnumerateArray().Select(v => v.GetSingle())];
                    if (j == 0)
                    {
                        Assert.Equal(row[1], target[f][j].Origin.Y, 6);
                        continue;
                    }
                    var original = new Transform3D(new Basis(new Quaternion(row[3], row[4], row[5], row[6])), new Vector3(row[0], row[1], row[2]));
                    Assert.True(original.IsEqualApprox(target[f][j]));
                }
                float[] first = [.. samples[cue.Frame - 1].GetProperty("pose")[0].EnumerateArray().Select(v => v.GetSingle())];
                float[] current = [.. raw.GetProperty("pose")[0].EnumerateArray().Select(v => v.GetSingle())];
                Basis rawFirst = new(new Quaternion(first[3], first[4], first[5], first[6]));
                double firstProgress = samples[cue.Frame - 1].GetProperty("heading_radians").GetDouble() - entry;
                double firstMapped = cue.CycleStart - Math.PI + (cue.Allocation * firstProgress / span);
                Basis placement = new(Vector3.Up, (float)(firstMapped - Math.Atan2(rawFirst.Z.X, rawFirst.Z.Z)));
                Vector3 expectedPosition = ground + (placement * new Vector3(current[0] - first[0], 0, current[2] - first[2]));
                Assert.InRange(Math.Abs(target[f][0].Origin.X - expectedPosition.X), 0, 1e-5);
                Assert.InRange(Math.Abs(target[f][0].Origin.Z - expectedPosition.Z), 0, 1e-5);
            }
            Assert.InRange(target[0][0].Origin.X - ground.X, -1e-6f, 1e-6f);
            Assert.InRange(target[0][0].Origin.Z - ground.Z, -1e-6f, 1e-6f);
        }
    }

    private sealed class GatedFiniteTurnSession : SomaInferenceWorker.ISession
    {
        internal readonly ManualResetEventSlim Release = new(false);
        internal int Calls;
        internal bool Disposed;
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
        {
            return Interlocked.Increment(ref Calls) == 1 && !Release.Wait(TimeSpan.FromSeconds(10))
                ? throw new TimeoutException("Synthetic worker gate not released.")
                : [.. Enumerable.Range(0, 30).Select(f => context[3].Select((p, j) => SomaMotionData.Blend(p, target[0][j], f / 29f)).ToArray())];
        }
        public void Dispose()
        {
            Disposed = true;
            Release.Dispose();
        }
    }

    /// <summary>Pending old-owner synthetic work cannot service a cancelled or replacement plan.</summary>
    [Headless]
    [Fact]
    public async Task FiniteNAVTurn_CancelAndReplacementInvalidatePendingCueWithoutService()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        var session = new GatedFiniteTurnSession();
        MotionBricksNavigation? nav = null;
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation n)
            {
                nav = n;
                n.SessionFactory = () => session;
            }
        }
        tree.NodeAdded += Configure;
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            nav.SetPhysicsProcess(false);
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(new Transform3D(new Basis(Vector3.Up, 2), actor.GlobalPosition), []));
            nav.SetPhysicsProcess(false);
            nav._PhysicsProcess(1d / 60);
            using var first = JsonDocument.Parse(JsonSerializer.Serialize(nav.TerminalTurnSnapshot));
            long revision = first.RootElement.GetProperty("revision").GetInt64();
            for (int wait = 0; wait < 120 && Volatile.Read(ref session.Calls) == 0; wait++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal(1, Volatile.Read(ref session.Calls));
            nav.ClearDestination();
            using var cancelled = JsonDocument.Parse(JsonSerializer.Serialize(nav.TerminalTurnSnapshot));
            Assert.Equal("interrupted", cancelled.RootElement.GetProperty("disposition").GetString());
            Assert.Equal(0, cancelled.RootElement.GetProperty("Served").GetInt32());
            Assert.Equal((int)NavigationDestinationResult.Accepted, nav.SelectDestination(new Transform3D(new Basis(Vector3.Up, -1), actor.GlobalPosition), []));
            nav.SetPhysicsProcess(false);
            nav._PhysicsProcess(1d / 60);
            using var replacement = JsonDocument.Parse(JsonSerializer.Serialize(nav.TerminalTurnSnapshot));
            Assert.True(replacement.RootElement.GetProperty("revision").GetInt64() > revision);
            session.Release.Set();
            for (int tick = 0; tick < 12; tick++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                nav._PhysicsProcess(1d / 60);
            }
            using var current = JsonDocument.Parse(JsonSerializer.Serialize(nav.TerminalTurnSnapshot));
            Assert.Equal(0, current.RootElement.GetProperty("Served").GetInt32());
            Assert.NotEqual("failed", nav.Phase);
            Assert.True(nav.DiscardedResults >= 1);
            _ = Assert.Single(nav.TerminalOutcomes); // Only cancelled owner; no false endpoint service/completion.
        }
        finally
        {
            session.Release.Set();
            tree.NodeAdded -= Configure;
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        Assert.True(session.Disposed);
    }
}
