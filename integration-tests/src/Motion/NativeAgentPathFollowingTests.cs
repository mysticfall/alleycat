using System.Reflection;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Native-accepted recorded corner with an explicit native-path via must submit, not hit a custom veto.</summary>
    [Headless]
    [Fact]
    public Task NativeAcceptedRecordedCornerViaSubmitsOrderedPathTargets() => RunNativeRecordedPathSubmission(true);

    /// <summary>The original no-via command also uses native ordered target placement, without replaying saved futures.</summary>
    [Headless]
    [Fact]
    public Task NativeAcceptedRecordedCornerSubmitsOrderedPathTargets() => RunNativeRecordedPathSubmission(false);

    private static async Task RunNativeRecordedPathSubmission(bool via)
    {
        string source = ProjectSettings.GlobalizePath("res://temp/motionbricks/navigation-playtests/20261110T033217788Z-166a727ca4bb4be09ec1709c71596d4f/");
        JsonElement input = NavigationSessionReader.ReadCheckpoint(source).Single(e => e.Event.GetProperty("sequence").GetInt64() == 1362).Event.GetProperty("data").GetProperty("data");
        Transform3D goal = FeedbackTransform(input.GetProperty("requested").Deserialize<float[]>()!, 0);
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame();
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        actor.Transform = FeedbackTransform(input.GetProperty("actor").Deserialize<float[]>()!, 0);
        MotionBricksNavigation? nav = null;
        Transform3D[][]? targets = null;
        NavigationRouteSnapshot? submittedRoute = null;
        bool actualContext = false;
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation n)
            {
                return;
            }
            nav = n;
            n.Ready += () => n.SetPhysicsProcess(false);
            n.SessionFactory = () => new TargetEndpointSession(true);
            n.WindowSubmitted += (_, context, target) =>
            {
                targets = target;
                submittedRoute = (NavigationRouteSnapshot?)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n);
                var history = (RealisedPoseHistory)typeof(MotionBricksNavigation).GetField("_history", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(n)!;
                actualContext = context.Zip(history.Context()).All(v => v.First.SequenceEqual(v.Second));
                n.SetPhysicsProcess(false); // Observe one real submission only; no generated journey or model call.
            };
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            int accepted = nav.SelectDestination(goal, via ? [new(3.9f, 0, 1.23f)] : []);
            Assert.Equal((int)NavigationDestinationResult.Accepted, accepted);
            nav.SetPhysicsProcess(true);
            for (int i = 0; i < 120 && targets is null && nav.TerminalOutcomes.Count == 0; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            File.WriteAllText(ProjectSettings.GlobalizePath(via ? "res://temp/motionbricks/native-agent-path-following-01/submission.json" : "res://temp/motionbricks/native-agent-path-following-01/no-via-submission.json"), JsonSerializer.Serialize(new
            {
                accepted,
                nav.Failure,
                submitted = targets is not null,
                actualContext,
                path = submittedRoute?.PathPoints.Select(p => new[] { p.X, p.Y, p.Z }).ToArray(),
                cursor = submittedRoute?.ActivePathIndex,
                targets = targets?.Select(p => { Vector3 v = SomaMotionData.ModelToWorld * p[0].Origin; return new[] { v.X, v.Y, v.Z }; }).ToArray(),
                nativeCalls = 0,
                scope = "Recorded-command corner/via component variant; synthetic provider; not original no-via arrival"
            }, new JsonSerializerOptions { WriteIndented = true }));
            Assert.Equal("", nav.Failure);
            Assert.NotNull(targets);
            Assert.NotNull(submittedRoute);
            Assert.True(actualContext);
            Assert.True(nav.OwnsRoute(submittedRoute));
            Assert.Equal(4, targets.Length);
            Assert.Equal(nav.GetCurrentNavigationPathIndex(), submittedRoute.ActivePathIndex);
            Assert.Equal("forward", nav.ReviewedRole);
            var sampler = (ReviewedRoleSampler)typeof(MotionBricksNavigation).GetField("_reviewed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
            Transform3D[][] sourceWindow = sampler.Targets(nav.ObservationTime, 49.0 / 30, Basis.Identity, Vector3.Zero);
            // Independent ordered-polyline observation: locate each placed target on native segments and measure
            // its station from the first returned native point. This never decides live route validity.
            float previousStation = -1;
            for (int i = 0; i < targets.Length; i++)
            {
                Vector3 world = SomaMotionData.ModelToWorld * targets[i][0].Origin;
                Assert.Equal([world.X, world.Y, world.Z], nav.SourceRateCurveIntent!.Positions[i]);
                var point = new Vector2(world.X, world.Z);
                float station = 0, found = -1;
                for (int j = 1; j < submittedRoute.PathPoints.Count; j++)
                {
                    Vector3 a3 = submittedRoute.PathPoints[j - 1], b3 = submittedRoute.PathPoints[j];
                    var a = new Vector2(a3.X, a3.Z);
                    var b = new Vector2(b3.X, b3.Z);
                    Vector2 edge = b - a;
                    float length = edge.Length();
                    float t = length > 0 ? (point - a).Dot(edge) / edge.LengthSquared() : 0;
                    if (t is >= 0 and <= 1 && point.DistanceTo(a + (edge * t)) < .00001f)
                    {
                        found = station + (length * t);
                        break;
                    }
                    station += length;
                }
                Assert.True(found >= previousStation && found >= 0);
                Assert.InRange(Math.Abs(found - (sampler.ForwardReferenceSpeed * ((49f + i) / 30))), 0, .00001f);
                previousStation = found;
                Assert.Equal(sourceWindow[i][0].Origin.Y, targets[i][0].Origin.Y);
                for (int bone = 1; bone < targets[i].Length; bone++)
                {
                    Assert.Equal(sourceWindow[i][bone], targets[i][bone]);
                }
            }
        }
        finally
        {
            tree.NodeAdded -= Configure;
            nav?.SetPhysicsProcess(false);
            await root.DrainMotionAndDiagnosticsAsync();
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
        }
    }
}
