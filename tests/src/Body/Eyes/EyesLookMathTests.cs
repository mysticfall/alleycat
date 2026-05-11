using AlleyCat.Body.Eyes;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Body.Eyes;

/// <summary>
/// Unit coverage for BODY-004 Eyes target-to-AnimationNodeTimeSeek time conversion.
/// </summary>
public sealed class EyesLookMathTests
{
    /// <summary>
    /// Verifies the reference look animations are treated as normalised one-second timelines.
    /// </summary>
    [Fact]
    public void SeekTimeConstants_DefineNormalisedReferenceAnimationContract()
    {
        Assert.Equal(0f, EyesLookMath.MinimumSeekTimeSeconds);
        Assert.Equal(0.5f, EyesLookMath.NeutralSeekTimeSeconds);
        Assert.Equal(1f, EyesLookMath.MaximumSeekTimeSeconds);
    }

    /// <summary>
    /// Verifies a forward target resolves to the neutral eye animation seek positions.
    /// </summary>
    [Fact]
    public void ResolveLookSeekTimesFromLocalDirection_ForwardTargetReturnsNeutralSeekTimes()
    {
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(
            new Vector3(0f, 0f, -10f),
            Mathf.DegToRad(35f),
            Mathf.DegToRad(25f));

        Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, seekTimes.X, precision: 5);
        Assert.Equal(EyesLookMath.NeutralSeekTimeSeconds, seekTimes.Y, precision: 5);
    }

    /// <summary>
    /// Verifies signed local directions map to the normalised 0..1 second reference look animation seek range.
    /// </summary>
    [Theory]
    [InlineData(1f, 0f, -1f, 0f, 0.5f)]
    [InlineData(-1f, 0f, -1f, 1f, 0.5f)]
    [InlineData(0f, 1f, -1f, 0.5f, 0f)]
    [InlineData(0f, -1f, -1f, 0.5f, 1f)]
    public void ResolveLookSeekTimesFromLocalDirection_ClampsSignedDirectionsToNormalisedAnimationRange(
        float x,
        float y,
        float z,
        float expectedHorizontal,
        float expectedVertical)
    {
        Vector2 seekTimes = EyesLookMath.ResolveLookSeekTimesFromLocalDirection(
            new Vector3(x, y, z),
            Mathf.DegToRad(35f),
            Mathf.DegToRad(25f));

        Assert.Equal(expectedHorizontal, seekTimes.X, precision: 5);
        Assert.Equal(expectedVertical, seekTimes.Y, precision: 5);
    }
}
