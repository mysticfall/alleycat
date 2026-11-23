using AlleyCat.Navigation;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Navigation;

/// <summary>
/// The straight native corridor used by nearby positioning: navigation-mesh subdivision points do not end it, real
/// corners do, and it never leaves the ordered native path.
/// </summary>
public sealed class NativePathMotionTargetsTests
{
    private const float Tolerance = .01f;

    /// <summary>
    /// The representative hover route: points subdividing a straight line (float noise only) are passed through to the
    /// destination, from any active point.
    /// </summary>
    [Fact]
    public void CollinearSubdivisionPointsExtendTheCorridorToTheDestination()
    {
        Vector3[] path =
        [
            new(.99952096f, 0, 1.4993798f), new(-.9817054f, 0, 1.4993801f), new(-1.0771317f, 0, 1.4993804f),
            new(-1.2999191f, 0, 1.4993801f), new(-1.3443755f, 0, 1.4993801f), new(-2.3775024f, 0, 1.4993804f),
            new(-3f, 0, 1.4993807f),
        ];
        for (int active = 0; active < path.Length; active++)
        {
            Assert.Equal(path[^1], NativePathMotionTargets.StraightCorridorEnd(path, active, new Vector3(1f, 0, 1.5f), Tolerance));
        }
    }

    /// <summary>A corner ends the corridor: the point after it is not reachable in a straight line along the path.</summary>
    [Fact]
    public void CornerEndsTheCorridor()
    {
        Vector3[] path = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0), new(2, 0, 1), new(2, 0, 2)];
        Assert.Equal(path[2], NativePathMotionTargets.StraightCorridorEnd(path, 1, Vector3.Zero, Tolerance));
        Assert.Equal(path[4], NativePathMotionTargets.StraightCorridorEnd(path, 3, Vector3.Zero, Tolerance));
    }

    /// <summary>
    /// A shallow bend whose vertex deviates more than the tolerance from the straight line still ends the corridor,
    /// so the corridor never cuts inside a native corner.
    /// </summary>
    [Fact]
    public void BendBeyondToleranceEndsTheCorridor()
    {
        Vector3[] path = [new(0, 0, 0), new(1, 0, 0), new(2, 0, .05f), new(3, 0, 0)];
        Assert.Equal(path[1], NativePathMotionTargets.StraightCorridorEnd(path, 1, Vector3.Zero, Tolerance));
    }

    /// <summary>A path that doubles back on its own line is not straight on, even though its points are collinear.</summary>
    [Fact]
    public void ReversalEndsTheCorridor()
    {
        Vector3[] path = [new(0, 0, 0), new(2, 0, 0), new(1, 0, 0)];
        Assert.Equal(path[1], NativePathMotionTargets.StraightCorridorEnd(path, 1, Vector3.Zero, Tolerance));
    }

    /// <summary>Before the first route point the corridor starts at the supplied origin.</summary>
    [Fact]
    public void FirstPointUsesTheOriginAsCorridorStart()
    {
        Vector3[] path = [new(1, 0, 0), new(2, 0, 0), new(2, 0, 1)];
        Assert.Equal(path[1], NativePathMotionTargets.StraightCorridorEnd(path, 0, Vector3.Zero, Tolerance));
        Assert.Equal(path[0], NativePathMotionTargets.StraightCorridorEnd(path, 0, new Vector3(0, 0, 1), Tolerance));
    }
}
