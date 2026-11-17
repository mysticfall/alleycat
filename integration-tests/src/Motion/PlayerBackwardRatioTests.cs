using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Normal exported player setting reaches the terminal velocity and every submitted guide root.</summary>
    [Fact]
    public async Task BackwardRatio_ExportedDefaultReachesAllVirtualGuideRoots()
    {
        using (var defaults = new PlayerMotionBricks())
        {
            Assert.Equal(0.75f, defaults.BackwardSpeedRatio);
            Assert.NotNull(Attribute.GetCustomAttribute(typeof(PlayerMotionBricks).GetProperty(nameof(PlayerMotionBricks.BackwardSpeedRatio))!, typeof(ExportAttribute)));
            defaults.BackwardSpeedRatio = 0;
            Assert.Equal(0.75f, defaults.BackwardSpeedRatio);
            defaults.BackwardSpeedRatio = 2;
            Assert.Equal(1, defaults.BackwardSpeedRatio);
        }
        {
            foreach (float ratio in new[] { 0.75f, 1f })
            {
                // The subject is the exported ratio arithmetic and release/settle semantics on the production
                // driver. A pre-Ready synthetic endpoint keeps every assertion deterministic; real generation
                // latency has dedicated real-CUDA facts (closure, virtual-target, steering-rate, authored-windows).
                await using ClosureFixture fixture = await ClosureFixture.CreateAsync(ratio,
                    motion => motion.SessionFactory = () => new TargetEndpointSession(true));
                WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
                Assert.Equal(ratio, driver.BackwardSpeedRatio);
                Vector3 requested = Vector3.Zero;
                Transform3D[][]? submitted = null;
                void Submitted(long id, Transform3D[][] context, Transform3D[][] target)
                {
                    requested = driver.RequestedVelocity;
                    submitted = target;
                    Assert.True(driver.GuideVelocity.IsEqualApprox(requested));
                    for (int i = 1; i < 4; i++)
                    {
                        Vector3 delta = target[i][0].Origin - target[i - 1][0].Origin;
                        delta.Y = 0;
                        Assert.True(delta.DistanceTo(requested / 30) < 1e-5f);
                    }
                }
                fixture.Motion.WindowSubmitted += Submitted;
                try
                {
                    fixture.Left.TriggerActionVector2InputChanged("primary", new Vector2(0, -0.5f));
                    await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 2);
                    Assert.NotNull(submitted);
                    // The ratio contract is requested = adopted forward reference speed × input magnitude × ratio.
                    // The reference speed itself is owned by the reviewed source set, not pinned here.
                    float reference = driver.ReviewedRoles!.ForwardReferenceSpeed;
                    Assert.InRange(requested.Length(), (reference * 0.5f * ratio) - 1e-6f, (reference * 0.5f * ratio) + 1e-6f);
                }
                finally
                {
                    fixture.Motion.WindowSubmitted -= Submitted;
                }
                fixture.Left.TriggerActionVector2InputChanged("primary", Vector2.Zero);
                Assert.Equal("settling-requested", fixture.Motion.Phase);
                Assert.Null(driver.VirtualTarget);
                await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 240);
                Assert.Equal("settled-idle", fixture.Motion.Phase);
            }
        }
    }
}
