using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>User policy: lateral classification uses the fore/aft component, not stick magnitude.</summary>
    [Headless]
    [Fact]
    public void ReviewedSet_RolesMultiTakeIdentityWrapAndElapsedSelection()
    {
        Assert.Equal("left_noncross", ReviewedRoleSampler.Classify(new Vector2(-1, .01f), 0, .15f));
        Assert.Equal("left_cross", ReviewedRoleSampler.Classify(new Vector2(-.3f, .3f), 0, .15f));
        Assert.Equal("left_cross", ReviewedRoleSampler.Classify(new Vector2(-.3f, -.3f), 0, .15f));
        Assert.Equal("backward", ReviewedRoleSampler.Classify(new Vector2(.01f, -.5f), 0, .15f));
        Assert.Equal("left_turn", ReviewedRoleSampler.Classify(Vector2.Zero, -.5f, .15f));
        Assert.Equal("right_turn", ReviewedRoleSampler.Classify(Vector2.Zero, .5f, .15f));
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://assets/animations/packs/default/locomotion/feminine/set.json"), data);
        _ = sampler.SetIntent("forward", 0);
        Transform3D[][] window = sampler.Targets(0, 34.0 / 30, Basis.Identity, Vector3.Zero);
        Assert.Equal(92, sampler.Selection.WindowStart);
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://assets/animations/packs/default/locomotion/feminine/landing-ablation-01.json"), data);
        ReviewedMotionSource.Role role = source.GetRole("feminine", "normal", "forward");
        var raw = new Transform3D[308];
        source.CopyWindow(role, 58, raw);
        // Wrap's third frame is original frame 58: articulation is unchanged, not interpolated or repaired.
        Assert.Equal(raw[1], window[2][1]);
        Assert.False(sampler.SetIntent("forward", 1));
        _ = sampler.Targets(4, 49.0 / 30, Basis.Identity, new Vector3(3, 0, 5));
        Assert.True(sampler.Selection.Available);
        Assert.Equal(178, sampler.Selection.SourceFrame, 8);
        long revision = sampler.Selection.Revision;
        _ = sampler.SetIntent("right_turn", 4);
        Assert.True(sampler.Selection.Revision > revision);
        Assert.Equal(1096, sampler.Selection.SourceFrame);
        _ = sampler.Targets(7, 49.0 / 30, Basis.Identity, Vector3.Zero);
        Assert.True(sampler.Selection.Available); // Episodic repetition, not a falsely finite turn that exhausts in one hold.
        _ = sampler.SetIntent("", 7);
        Assert.False(sampler.Selection.Available);
        _ = Assert.Throws<InvalidOperationException>(() => sampler.Targets(8, 0, Basis.Identity, Vector3.Zero));
    }

    /// <summary>Normal player installer + primary callbacks reach all eight source roles without a native quality run.</summary>
    [Headless]
    [Fact]
    public async Task ReviewedSet_NormalPlayerSwitchesAllRolesAndReleasesOwnership()
    {
        {
            await using ClosureFixture fixture = await ClosureFixture.CreateAsync();
            WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
            Assert.NotNull(driver.ReviewedRoles);
            Assert.Null(driver.Completion);
            SomaInferenceWorker worker = fixture.Root.MotionWorkers.Create(() => new ExhaustionContextSession());
            ExhaustionField("_worker").SetValue(driver, worker);
            (Vector2 movement, float turn, string role)[] cases = [
                (new(0,.5f),0,"forward"), (new(0,-.5f),0,"backward"),
                (new(-.5f,0),0,"left_noncross"),(new(.5f,0),0,"right_noncross"),
                (new(-.5f,.5f),0,"left_cross"),(new(.5f,.5f),0,"right_cross"),
                (Vector2.Zero,-.5f,"left_turn"),(Vector2.Zero,.5f,"right_turn")];
            foreach ((Vector2 movement, float turn, string role) in cases)
            {
                fixture.Right.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                fixture.Left.TriggerActionVector2InputChanged("primary", movement);
                fixture.Right.TriggerActionVector2InputChanged("primary", new Vector2(turn, 0));
                Assert.Equal(role, driver.ReviewedRoles!.Role);
                Assert.Equal("dd5837bd66f83a5117ca06d3305b5789d377f447673743961f2a198f8fd023af", driver.ReviewedRoles.RoleSourceSHA256(role));
                int before = driver.Requests;
                for (int i = 0; i < 30 && driver.Requests == before; i++)
                {
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
                }
                Assert.True(driver.Requests > before, role);
                Assert.EndsWith("/" + role, driver.LastReviewedRequest!.Value.Key);
                Assert.Equal("", fixture.Motion.Failure);
                if (role == "backward")
                {
                    Assert.InRange(Math.Abs(driver.RequestedVelocity.Length() -
                        (driver.ReviewedRoles!.ForwardReferenceSpeed * movement.Length() * .75f)), 0, .00001f);
                }
                if (role == "forward")
                {
                    int requests = driver.Requests;
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 180);
                    Assert.True(driver.Requests > requests, "Held reviewed motion must chain beyond a native window and raw period.");
                    Assert.Equal("forward", driver.ReviewedRoles.Role);
                    Assert.True(driver.ReviewedRoles.Selection.SourceFrame > 58 + 80);
                }
            }
            fixture.Motion.ClearInput();
            Assert.Equal("", driver.ReviewedRoles!.Role);
            Assert.False(driver.IsPlaying);
            Assert.False(driver.RecordingPending);
            await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 100);
            Assert.Equal("", driver.ReviewedRoles.Role);
            Assert.Equal("", fixture.Motion.Failure);
            Assert.False(fixture.VRIK.AnimationBodyDiagnostic);
        }
    }

    /// <summary>Existing NAV scene, Godot route and destination replacement use shared reviewed sources, not a new planner.</summary>
    [Headless]
    [Fact]
    public async Task ReviewedSet_ExistingNavigationSceneSubmitsAndCancelsReviewedRole()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ReviewedNavigationRoute" };
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        AlleyCat.Character.Character npc = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        MotionBricksNavigation? installed = null;
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation navigation)
            {
                navigation.SessionFactory = () => new ExhaustionContextSession();
                installed = navigation;
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
            Assert.Equal("", nav.Failure);
            Vector3 start = npc.GlobalPosition;
            _ = nav.SelectDestination(new Transform3D(Basis.Identity, start + new Vector3(0, 0, -2)), []);
            for (int i = 0; i < 90 && nav.Requests == 0; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.True(nav.Requests > 0);
            Assert.Equal("forward", nav.ReviewedRole);
            // Nearby lateral replacement: a 2m journey is forward under the current 1.5m enter-distance contract.
            _ = nav.SelectDestination(new Transform3D(Basis.Identity, start + new Vector3(-1, 0, 0)), []);
            int before = nav.Requests;
            for (int i = 0; i < 90 && nav.Requests == before; i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.Equal("left_noncross", nav.ReviewedRole);
            nav.ClearDestination();
            Assert.Equal("", nav.ReviewedRole);
            await TestUtils.WaitForPhysicsFramesAsync(tree, 100);
            Assert.Equal("", nav.Failure);
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
