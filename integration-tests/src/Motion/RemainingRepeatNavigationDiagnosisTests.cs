using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    /// <summary>Three same-floor clicks remain reachable from the measured result of each completed trip.</summary>
    [Headless]
    [Fact]
    public async Task SourceRateCurve_ThreeFloorRequestsAfterCompletionRemainReachable()
    {
        await using RepeatNavigationFixture fixture = await RepeatNavigationFixture.CreateAsync(new Vector3(-1.6f, 0, 2.5f));
        Vector3[] aims = [new(-1.6f, 0, 2), new(-1.6f, 0, 1.5f), new(-1.6f, 0, 5.15f)];
        for (int i = 0; i < aims.Length; i++)
        {
            await fixture.TripAsync(fixture.ControllerGoal(fixture.Camera.UnprojectPosition(aims[i])), i + 1);
        }
    }
}
