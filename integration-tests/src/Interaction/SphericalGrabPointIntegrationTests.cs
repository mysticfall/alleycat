using AlleyCat.Body;
using AlleyCat.Interaction;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Runtime coverage for the INTR-001 spherical grab-point implementation.
/// </summary>
public sealed class SphericalGrabPointIntegrationTests
{
    private const float ReachDistanceMetres = 0.1f;
    private const float PositionToleranceMetres = 0.0001f;
    private const float BasisTolerance = 0.0001f;

    /// <summary>
    /// Verifies both hand sides are accepted when the hand is within reach and the palm side faces the centre.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithinReachAndPalmFacing_ReturnsCandidateForBothHands()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D leftHand = CreateHandTransform(new Vector3(0.05f, 0.0f, 0.0f), Vector3.Left);
        Transform3D rightHand = CreateHandTransform(new Vector3(-0.05f, 0.0f, 0.0f), Vector3.Right);

        try
        {
            GrabPointCandidate? leftCandidate = grabPoint.GetGrabPoint(LimbSide.Left, leftHand);
            GrabPointCandidate? rightCandidate = grabPoint.GetGrabPoint(LimbSide.Right, rightHand);

            AssertCandidateMatchesGrabPoint(leftCandidate, grabPoint, animation, leftHand);
            AssertCandidateMatchesGrabPoint(rightCandidate, grabPoint, animation, rightHand);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies hands beyond the configured centre reach are rejected gracefully.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_OutsideReach_ReturnsNull()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(ReachDistanceMetres + 0.01f, 0.0f, 0.0f), Vector3.Left);

        try
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies a hand whose palm side points away from the centre fails the angle check.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PalmFacingAway_ReturnsNull()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.05f, 0.0f, 0.0f), Vector3.Right);

        try
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies spherical grab points can be approached from several directions without a fixed object-facing axis.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_MultipleApproachAnglesAroundCentre_ReturnCandidatesAtCentre()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3[] handOrigins =
        [
            new(0.05f, 0.0f, 0.0f),
            new(-0.05f, 0.0f, 0.0f),
            new(0.0f, 0.05f, 0.0f),
            new(0.0f, 0.0f, -0.05f),
        ];

        try
        {
            foreach (Vector3 handOrigin in handOrigins)
            {
                Vector3 palmDirection = -handOrigin.Normalized();
                Transform3D hand = CreateHandTransform(handOrigin, palmDirection);

                GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

                AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand);
            }
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies missing resources, invalid thresholds, invalid palm axes, and coincident centres reject with null.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_InvalidConfigurationOrZeroLengthDirection_ReturnsNull()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D validHand = CreateHandTransform(new Vector3(0.05f, 0.0f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.GrabAnimation = null;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.GrabAnimation = animation;
            grabPoint.ReachDistanceMetres = 0.0f;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.ReachDistanceMetres = ReachDistanceMetres;
            grabPoint.PalmLocalDirection = Vector3.Zero;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.PalmLocalDirection = Vector3.Down;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, Transform3D.Identity));
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies the palm-facing minimum dot threshold is inclusive when the computed dot exactly matches it.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PalmFacingDotEqualsMinimum_ReturnsCandidate()
    {
        using var animation = new Animation();
        SphericalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 handOrigin = new(0.05f, 0.0f, 0.0f);
        Vector3 palmDirectionAtThreshold = ((0.75f * Vector3.Left)
            + (Mathf.Sqrt(1.0f - (0.75f * 0.75f)) * Vector3.Up))
            .Normalized();
        Transform3D hand = CreateHandTransform(handOrigin, palmDirectionAtThreshold);
        float threshold = palmDirectionAtThreshold.Dot(Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = threshold;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    private static SphericalGrabPoint CreateGrabPoint(Animation animation) =>
        new()
        {
            ReachDistanceMetres = ReachDistanceMetres,
            PalmFacingMinimumDot = 0.75f,
            PalmLocalDirection = Vector3.Down,
            GrabAnimation = animation,
            GlobalPosition = Vector3.Zero,
        };

    private static Transform3D CreateHandTransform(Vector3 origin, Vector3 palmWorldDirection)
    {
        Vector3 yAxis = -palmWorldDirection.Normalized();
        Vector3 helperAxis = Mathf.Abs(yAxis.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 xAxis = helperAxis.Cross(yAxis).Normalized();
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();

        return new Transform3D(new Basis(xAxis, yAxis, zAxis), origin);
    }

    private static void AssertCandidateMatchesGrabPoint(
        GrabPointCandidate? candidate,
        SphericalGrabPoint grabPoint,
        Animation animation,
        Transform3D expectedHandTransform)
    {
        Assert.NotNull(candidate);
        Assert.Same(grabPoint, candidate.Source);
        Assert.Same(animation, candidate.Animation);
        Assert.True(
            candidate.HandTarget.Origin.DistanceTo(grabPoint.GlobalPosition) <= PositionToleranceMetres,
            $"Expected hand target origin at the spherical centre {grabPoint.GlobalPosition}, observed {candidate.HandTarget.Origin}.");
        AssertBasisApproximatelyEqual(expectedHandTransform.Basis, candidate.HandTarget.Basis);
    }

    private static void AssertBasisApproximatelyEqual(Basis expected, Basis actual)
    {
        Assert.True(
            expected.X.DistanceTo(actual.X) <= BasisTolerance,
            $"Expected hand target basis X axis {expected.X}, observed {actual.X}.");
        Assert.True(
            expected.Y.DistanceTo(actual.Y) <= BasisTolerance,
            $"Expected hand target basis Y axis {expected.Y}, observed {actual.Y}.");
        Assert.True(
            expected.Z.DistanceTo(actual.Z) <= BasisTolerance,
            $"Expected hand target basis Z axis {expected.Z}, observed {actual.Z}.");
    }
}
