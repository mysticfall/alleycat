using AlleyCat.Common;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Common;

/// <summary>
/// Unit coverage for optional directional offset limits.
/// </summary>
public sealed class OffsetLimits3DTests
{
    /// <summary>
    /// Disabled directional bounds must leave that side unclamped.
    /// </summary>
    [Fact]
    public void ClampOffset_MissingDirectionalLimit_DoesNotClampThatSide()
    {
        Vector3 clamped = OffsetLimits3D.ClampOffset(
            new Vector3(0f, -2f, 0f),
            normalisationDistance: 1f,
            up: 0.15f,
            down: null,
            left: 0.2f,
            right: 0.2f,
            forward: 0.25f,
            back: 0.15f);

        Assert.Equal(-2f, clamped.Y);
    }

    /// <summary>
    /// Enabled directional bounds must continue to clamp normally.
    /// </summary>
    [Fact]
    public void ClampOffset_PresentDirectionalLimit_StillClampsThatSide()
    {
        Vector3 clamped = OffsetLimits3D.ClampOffset(
            new Vector3(0f, 2f, 0f),
            normalisationDistance: 1f,
            up: 0.15f,
            down: null,
            left: null,
            right: null,
            forward: null,
            back: null);

        Assert.Equal(0.15f, clamped.Y);
    }
}
