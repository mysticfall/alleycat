using System.Security.Cryptography;
using System.Text.Json;
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
    private const string ApprovedMotionBundleSHA256 = "dd5837bd66f83a5117ca06d3305b5789d377f447673743961f2a198f8fd023af";

    /// <summary>Normal installers require no source flags; both controllers bind the same approved bundle.</summary>
    [Headless]
    [Fact]
    public async Task RegeneratedSource_NormalCTRLAndNAVUseSharedTakeWithoutActivationFlags()
    {
        string[] flags = ["PLAYER_REVIEWED_ROLES", "PLAYER_METADATA_TURN_TRIAL", "PLAYER_REVIEWED_FORWARD",
            "PLAYER_VIRTUAL_TARGET", "PLAYER_CONTINUOUS_WALK", "PLAYER_STABLE_INPUT_CHAINING",
            "NAV_REVIEWED_ROLES", "NAV_REVIEWED_FACING", "NAV_SOURCE_RATE_CURVE"];
        string?[] previous = [.. flags.Select(System.Environment.GetEnvironmentVariable)];
        foreach (string flag in flags)
        {
            System.Environment.SetEnvironmentVariable(flag, null);
        }
        try
        {
            await using (ClosureFixture player = await ClosureFixture.CreateAsync(null, motion => motion.SessionFactory = () => new TargetEndpointSession(true)))
            {
                WalkingExperimentDriver driver = Assert.IsType<WalkingExperimentDriver>(player.Motion.ContinuousDriver);
                Assert.Null(driver.Completion);
                Assert.Equal(ApprovedMotionBundleSHA256, driver.ReviewedRoles.RoleSourceSHA256("right_cross"));
                player.Motion.SetStick(false, new Vector2(-1, 0));
                Assert.Equal([947, 951, 961, 1001], driver.MetadataTurn!.Reference.Landmarks.Select(e => e.Frame));
                Assert.Equal(0, driver.MetadataTurn.Cycle);
                player.Motion.ClearInput();
                Assert.Null(driver.MetadataTurn);
            }
            SceneTree tree = TestUtils.GetSceneTree();
            var root = new ProbeGame();
            Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
            MotionBricksNavigation? nav = null;
            void Configure(Node node)
            {
                if (node is MotionBricksNavigation n)
                {
                    nav = n;
                    n.SessionFactory = () => new TargetEndpointSession(true);
                }
            }
            tree.NodeAdded += Configure;
            root.AddChild(scene);
            tree.Root.AddChild(root);
            try
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
                Assert.NotNull(nav);
                Assert.Equal("", nav.Failure);
                var sampler = (ReviewedRoleSampler)typeof(MotionBricksNavigation).GetField("_reviewed",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(nav)!;
                Assert.Equal(ApprovedMotionBundleSHA256, sampler.RoleSourceSHA256("left_turn"));
                Assert.Equal([1103, 1120], sampler.TurnReference("right_turn").Feet.Select(e => e.Frame));
            }
            finally
            {
                tree.NodeAdded -= Configure;
                Task? completion = nav?.WorkerCompletion;
                root.QueueFree();
                await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
                if (completion is not null)
                {
                    await completion.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
        finally
        {
            for (int i = 0; i < flags.Length; i++)
            {
                System.Environment.SetEnvironmentVariable(flags[i], previous[i]);
            }
        }
    }

    /// <summary>Runtime readers bind the committed approved ANIM-002 take, roles, cycles and angular metadata.</summary>
    [Headless]
    [Fact]
    public void RegeneratedSource_CommittedSetAndAngularMetadataBindApprovedTake()
    {
        string folder = ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine");
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(Path.Combine(folder, "set.json"), data);
        string path = Path.Combine(folder, "landing-ablation-01.json");
        Assert.Equal(ApprovedMotionBundleSHA256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        var reader = new ReviewedMotionSource(path, data);
        JsonElement source = reader.Manifest.GetProperty("descriptor").GetProperty("source");
        Assert.Equal("3da77b8ab373dc503b789b8377d3023ea2496eab34086ef91e400a31f68de80a", source.GetProperty("sha256").GetString());
        Assert.Equal("source_feminine_landing_ablation_01", source.GetProperty("action").GetString());
        Assert.Equal("OBKimodo", source.GetProperty("slot").GetString());
        Assert.Equal(1200, reader.FrameCount);
        string[] roles = ["forward", "backward", "left_noncross", "right_noncross", "left_cross", "right_cross", "left_turn", "right_turn"];
        int[] first = [58, 207, 346, 496, 646, 796, 946, 1096], period = [36, 39, 30, 30, 30, 30, 60, 60];
        using var set = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "set.json")));
        var raw = new Transform3D[4 * data.Names.Length];
        for (int i = 0; i < roles.Length; i++)
        {
            ReviewedMotionSource.Role role = reader.GetRole("feminine", "normal", roles[i]);
            Assert.Equal(46 + (i * 150), role.First);
            Assert.Equal(106 + (i * 150), role.Last);
            Assert.Equal(ApprovedMotionBundleSHA256, sampler.RoleSourceSHA256(roles[i]));
            JsonElement cycle = set.RootElement.GetProperty("roles")[i].GetProperty("cycle");
            ReviewedMotionSource.ExperimentalCycle bound = reader.LoadExperimentalCycle(cycle, role);
            Assert.Equal(first[i], bound.First);
            Assert.Equal(period[i], bound.PeriodFrames);

            // Only root placement may differ from the unpositioned source, for every installed role.
            _ = sampler.SetIntent(roles[i], 0);
            Transform3D[][] target = sampler.Targets(0, 0, Basis.Identity, Vector3.Zero);
            reader.CopyWindow(role, first[i], raw);
            for (int frame = 0; frame < 4; frame++)
            {
                Assert.Equal(raw.AsSpan((frame * data.Names.Length) + 1, data.Names.Length - 1).ToArray(), target[frame][1..]);
            }
        }
        Assert.InRange(sampler.TurnReference("left_turn").SignedSpan, 1.16660, 1.16662);
        Assert.InRange(sampler.TurnReference("right_turn").SignedSpan, -1.59440, -1.59437);
        Assert.Equal([947, 951, 961, 1001], sampler.TurnReference("left_turn").Landmarks.Select(e => e.Frame));
        Assert.Equal([1103, 1107, 1120, 1152], sampler.TurnReference("right_turn").Landmarks.Select(e => e.Frame));
        _ = sampler.SetIntent("right_cross", 0);
        Assert.InRange(sampler.ReferenceSpeed, .50628f, .50630f);
    }
}
