using AlleyCat.Navigation;
using Godot;
using Xunit;

#pragma warning disable CS1591

namespace AlleyCat.Tests.Navigation;

public sealed class NPCBlendDomainTests
{
    private static readonly NPCBlendDomain _domain = new(
        [new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f)],
        [0, 1, 2, 0, 2, 3]);

    [Fact]
    public void Project_PreservesSupportedPoint()
    {
        Vector2 point = new(0.25f, -0.6f);

        Assert.Equal(point, _domain.Project(point));
    }

    [Fact]
    public void Project_ClampsUnsupportedPointToNearestAuthoredEdge()
    {
        Vector2 projected = _domain.Project(new(1.8f, 0.25f));

        Assert.True(_domain.Contains(projected));
        Assert.Equal(new Vector2(1f, 0.25f), projected);
    }

    [Fact]
    public void StepInside_BoundsPublishedDistanceAndRetainsTriangleSupport()
    {
        Vector2 current = new(-0.8f, -0.5f);

        Vector2 stepped = _domain.StepInside(current, new(4f, 0.5f), 0.1f);

        Assert.True(_domain.Contains(stepped));
        Assert.InRange(current.DistanceTo(stepped), 0f, 0.10001f);
    }
}
