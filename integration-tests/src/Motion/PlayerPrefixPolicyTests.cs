using AlleyCat.Control;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Normal held-input policy gates redundant snapshots, not changed input, steering or release.</summary>
    [Headless]
    [Fact]
    public async Task SourceGuides_PreserveChangedInputAndImmediateRelease()
    {
        await using ClosureFixture fixture = await ClosureFixture.CreateAsync(null,
            motion => motion.SessionFactory = () => new TargetEndpointSession(true));
        WalkingExperimentDriver driver = fixture.Motion.ContinuousDriver!;
        fixture.Motion.SetStick(true, new Vector2(0, .5f));
        Assert.True(driver.AllowsSnapshotSubmission());
        for (int tick = 0; tick < 60 && driver.Requests == 0; tick++)
        {
            await TestUtils.WaitForPhysicsFramesAsync(fixture.Tree, 1);
        }
        Assert.True(driver.Requests > 0);
        Assert.False(driver.AllowsSnapshotSubmission());
        fixture.Motion.SetStick(true, new Vector2(0, .55f));
        Assert.True(driver.AllowsSnapshotSubmission());
        fixture.Motion.SetStick(true, new Vector2(0, .5f));
        Assert.True(driver.AllowsSnapshotSubmission());
        fixture.Motion.SetStick(false, new Vector2(.2f, 0));
        Assert.True(driver.AllowsSnapshotSubmission());
        fixture.Motion.ClearInput();
        Assert.Null(driver.VirtualTarget);
        Assert.Equal(0, driver.SteeringYaw);
        Assert.Equal(Vector2.Zero, fixture.Motion.MovementIntent);
        Assert.False(driver.IsPlaying);
        Assert.False(driver.RecordingPending);
    }
}
