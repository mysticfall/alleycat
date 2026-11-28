using System.Reflection;
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
    /// <summary>Ordinary straight mesh vertices do not request stops; the real route and submit path are retained.</summary>
    [Headless]
    [Fact]
    public async Task ReviewedNavigation_StraightMeshVerticesRetainThroughGuideVelocity()
    {
        string evidence = ProjectSettings.GlobalizePath(System.Environment.GetEnvironmentVariable("NAV_THROUGH_EVIDENCE")
            ?? $"res://temp/tests/navigation/through/{Guid.NewGuid():N}.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        Assert.False(File.Exists(evidence), "Preserve previous regression evidence.");
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ReviewedThroughRegression" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character npc = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        // This fixture checks uninterrupted straight travel, not the separately tested rearward departure reduction.
        npc.RotateY(Mathf.Pi);
        MotionBricksNavigation? installed = null;
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation nav)
            {
                nav.SessionFactory = () => new ExhaustionContextSession();
                installed = nav;
            }
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            MotionBricksNavigation nav = Assert.IsType<MotionBricksNavigation>(npc.Navigation);
            Assert.Same(installed, nav);
            Vector3 start = npc.GlobalPosition;
            // Same unobstructed rear lane as rear02; no forced actor or injected path points.
            Vector3 goal = start + new Vector3(0, 0, 2);
            Transform3D[][]? submitted = null;
            NavigationRouteSnapshot? route = null;
            double sourceTime = 0;
            void Capture(long ticket, Transform3D[][] context, Transform3D[][] target)
            {
                if (submitted is not null)
                {
                    return;
                }

                submitted = target;
                route = (NavigationRouteSnapshot?)typeof(NavigationBase).GetProperty("CurrentRouteSnapshot",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav);
                sourceTime = nav.ObservationTime;
                start = npc.GlobalPosition;
            }
            nav.WindowSubmitted += Capture;
            try
            {
                _ = nav.SelectDestination(new Transform3D(new Basis(Vector3.Up, Mathf.Pi), goal), []);
                for (int i = 0; i < 90 && submitted is null; i++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
                }
                Assert.NotNull(submitted);
                Assert.NotNull(route);
                Assert.Equal("", nav.Failure);
                Assert.Equal("forward", nav.ReviewedRole);
                Assert.Contains(route.PathPoints, p => Mathf.Abs(p.Z - .55f) < .00001f);
                Assert.Contains(route.PathPoints, p => Mathf.Abs(p.Z - 1.23f) < .00001f);
                Vector3 course = goal - start;
                course.Y = 0;
                float available = course.Length();
                course = course.Normalized();
                // Independent route observation establishes this is a straight corridor, not a corner shortcut.
                foreach (Vector3 point in route.PathPoints)
                {
                    Vector3 offset = point - start;
                    offset.Y = 0;
                    Assert.InRange((offset - (course * offset.Dot(course))).Length(), 0, .00001f);
                }
                var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
                var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://assets/animations/packs/default/locomotion/feminine/set.json"), data);
                _ = sampler.SetIntent("forward", sourceTime);
                float speed = sampler.ReferenceSpeed * nav.NormalisedSpeed;
                float horizon = speed * (49f / 30);
                Transform3D[][] source = sampler.Targets(sourceTime, 49.0 / 30, new Basis(Vector3.Up, Mathf.Pi), Vector3.Zero);
                var records = new List<object>();
                var violations = new List<string>();
                for (int i = 0; i < 4; i++)
                {
                    Assert.Equal(77, submitted[i].Length);
                    Assert.Equal(source[i][0].Origin.Y, submitted[i][0].Origin.Y);
                    // Native-path facing may rotate the source root around +Y; articulation/tilt remain source-owned.
                    for (int axis = 0; axis < 3; axis++)
                    {
                        Assert.Equal(source[i][0].Basis[axis].Y, submitted[i][0].Basis[axis].Y, .000002f);
                        Assert.Equal(source[i][0].Basis[axis].Length(), submitted[i][0].Basis[axis].Length(), .000002f);
                    }
                    for (int joint = 1; joint < 77; joint++)
                    {
                        Assert.Equal(source[i][joint], submitted[i][joint]);
                    }

                    Vector3 world = SomaMotionData.ModelToWorld * submitted[i][0].Origin;
                    Vector3 delta = world - start;
                    delta.Y = 0;
                    float actual = delta.Dot(course);
                    float expected = Math.Min(available, horizon + (speed * (i / 30f)));
                    Assert.InRange(actual, 0, available + .00001f);
                    Assert.InRange((delta - (course * actual)).Length(), 0, .00001f);
                    if (Mathf.Abs(actual - expected) > .00001f)
                    {
                        violations.Add($"guide{i}: actual={actual:R}, expected through={expected:R}");
                    }

                    records.Add(new
                    {
                        frame = i,
                        actual,
                        expected,
                        sourceHeight = world.Y
                    });
                }
                using (var stream = new FileStream(evidence, FileMode.CreateNew))
                {
                    JsonSerializer.Serialize(stream, new
                    {
                        contract = "straight-through conditioning",
                        speed,
                        horizon,
                        available,
                        records,
                        violations
                    });
                }
                Assert.True(violations.Count == 0, string.Join("; ", violations));
            }
            finally
            {
                nav.WindowSubmitted -= Capture;
                nav.ClearDestination();
            }
            await TestUtils.WaitForPhysicsFramesAsync(tree, 100);
            Assert.Equal("", nav.Failure);
            Assert.Equal("", nav.ReviewedRole);
        }
        finally
        {
            tree.NodeAdded -= Configure;
            Task? completion = installed?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion.WaitAsync(TimeSpan.FromSeconds(30));
            }

        }
    }
}
