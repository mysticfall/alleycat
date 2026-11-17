using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>A translated short adjustment must still submit its final-facing source before external idle.</summary>
    [Fact]
    public async Task ShortAdjustmentSubmitsTranslationAndFacingBeforeExternalIdle()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        MotionBricksNavigation? observed = null;
        var windows = new List<(MotionBricksNavigation.RoleSubmission Role, bool Arrival)>();
        void Submitted(long ticket, Transform3D[][] context, Transform3D[][] target)
        {
            if (observed?.CurrentStatus.HasActiveRequest == true && observed.SubmittedRole is { } role)
            {
                windows.Add((role, observed.SourceRateCurveIntent?.Arrival == true));
            }
        }
        void Configure(Node node)
        {
            if (node is MotionBricksNavigation nav)
            {
                observed = nav;
                nav.WindowSubmitted += Submitted;
            }
        }
        tree.NodeAdded += Configure;
        try
        {
            // Reuse the exact recorded scenario and full observation window without changing its parent criterion.
            await MarkedTinyAdjustmentWithDifferentFacingRemainsLocalThroughRecordedWindow();
            Assert.NotEmpty(windows);
            Assert.Equal("backward", windows[0].Role.SelectedRole);
            Assert.False(windows[0].Arrival);
            Assert.False(windows[0].Role.Decision.Inputs.PositionReached);
            int turn = windows.FindIndex(w => w.Role.SelectedRole == "left_turn" && !w.Arrival);
            int idle = windows.FindIndex(w => w.Arrival);
            Assert.True(turn > 0 && idle > turn, "Required translation and final-facing windows must both precede idle.");
            Assert.True(windows[turn].Role.Decision.Inputs.PositionReached);
        }
        finally
        {
            tree.NodeAdded -= Configure;
            if (GodotObject.IsInstanceValid(observed))
            {
                observed!.WindowSubmitted -= Submitted;
            }
        }
    }
}
