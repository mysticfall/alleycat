using AlleyCat.IntegrationTests.Support;
using AlleyCat.Interaction;
using AlleyCat.Rigging;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Runtime coverage for the INTR-001 cylindrical grab-point implementation.
/// </summary>
public sealed class CylindricalGrabPointIntegrationTests
{
    private const float LengthMetres = 0.4f;
    private const float ReachDistanceMetres = 0.06f;
    private const float PositionToleranceMetres = 0.0001f;
    private const float BasisTolerance = 0.0001f;

    /// <summary>
    /// Verifies reach is measured to the closest point on the local-Y segment instead of the cylinder centre.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithinReachOfClosestPoint_ReturnsCandidateAtClosestPoint()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.15f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.05f, 0.15f, 0.0f), Vector3.Left);

        try
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand, expectedClosestPoint);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies the closest point clamps to the authored segment end and respects the margin beyond it.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_BeyondSegmentEnd_ClampsToEndAndRejectsOutsideReachMargin()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D nearEndHand = CreateHandTransform(new Vector3(0.03f, 0.23f, 0.0f), new Vector3(-0.03f, -0.03f, 0.0f));
        Transform3D outsideMarginHand = CreateHandTransform(new Vector3(0.03f, 0.26f, 0.0f), new Vector3(-0.03f, -0.06f, 0.0f));

        try
        {
            GrabPointCandidate? nearEndCandidate = grabPoint.GetGrabPoint(LimbSide.Left, nearEndHand);
            GrabPointCandidate? outsideMarginCandidate = grabPoint.GetGrabPoint(LimbSide.Left, outsideMarginHand);

            AssertCandidateMatchesGrabPoint(nearEndCandidate, grabPoint, animation, nearEndHand, new Vector3(0.0f, 0.2f, 0.0f));
            Assert.Null(outsideMarginCandidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies palm-facing evaluates against the closest point on the axis, not the cylinder centre.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PalmFacingClosestPointButNotCentre_ReturnsCandidate()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        grabPoint.PalmFacingMinimumDot = 0.98f;
        Transform3D hand = CreateHandTransform(new Vector3(0.05f, 0.18f, 0.0f), Vector3.Left);

        try
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand, new Vector3(0.0f, 0.18f, 0.0f));
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies invalid resources, dimensions, and palm axes reject with null.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_InvalidConfiguration_ReturnsNull()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D validHand = CreateHandTransform(new Vector3(0.05f, 0.1f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.GrabAnimation = null;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.GrabAnimation = animation;
            grabPoint.LengthMetres = 0.0f;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.LengthMetres = LengthMetres;
            grabPoint.ReachDistanceMetres = 0.0f;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.ReachDistanceMetres = ReachDistanceMetres;
            grabPoint.SnapDistanceMetres = -0.001f;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));

            grabPoint.SnapDistanceMetres = 0.0f;
            grabPoint.PalmLocalDirection = Vector3.Zero;
            Assert.Null(grabPoint.GetGrabPoint(LimbSide.Left, validHand));
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies raw hand-origin acquisition succeeds when the authored pipe-like offset reference is outside reach.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_RawHandOriginNearPipeButAuthoredGripReferenceOutsideReach_ReturnsCandidate()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        grabPoint.PalmFacingMinimumDot = -1.0f;
        Vector3 expectedClosestPoint = new(0.0f, 0.12f, 0.0f);
        Transform3D hand = CreateHandTransform(expectedClosestPoint + new Vector3(0.03f, 0.0f, 0.0f), Vector3.Left);
        Vector3 pipeLikeAuthoredOffset = new(0.0026352f, 0.0673249f, 0.0298438f);
        var authoredRotationOffset = new Vector3(-0.00048048052f, 0.011107354f, -1.5504136f);

        try
        {
            grabPoint.GrabPointPositionOffsetFromHand = pipeLikeAuthoredOffset;
            grabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;
            Vector3 authoredGripReference = hand.Origin + (hand.Basis.Orthonormalized() * pipeLikeAuthoredOffset);
            Assert.True(
                authoredGripReference.DistanceTo(expectedClosestPoint) > ReachDistanceMetres,
                $"Expected the authored grip reference to be outside reach, observed {authoredGripReference}.");

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected raw hand-origin acquisition to select {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Assert.True(
                Mathf.Abs(candidate.AcquisitionDistance - hand.Origin.DistanceTo(expectedClosestPoint)) <= PositionToleranceMetres,
                $"Expected acquisition distance to come from the accepted raw hand reference, observed {candidate.AcquisitionDistance}.");
            Assert.Equal(pipeLikeAuthoredOffset, candidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(authoredRotationOffset, candidate.GrabPointRotationOffsetFromHand);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected raw authored offset composition to recover selected point {expectedClosestPoint}, observed {reconstructedGrabPoint.Origin}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies centred raw hand-origin contact is not rejected only because palm direction cannot be derived.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_CentredRawHandOriginContact_DoesNotRejectZeroDirection()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        grabPoint.PalmFacingMinimumDot = 1.0f;
        Transform3D hand = new(Basis.Identity, new Vector3(0.0f, 0.1f, 0.0f));

        try
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand, new Vector3(0.0f, 0.1f, 0.0f));
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies centred raw hand-origin contact can fall back when the authored reference is outside reach.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_CentredRawHandOriginWithAuthoredReferenceOutsideReach_DoesNotRejectZeroDirection()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        grabPoint.PalmFacingMinimumDot = 0.95f;
        Vector3 selectedPoint = new(0.0f, 0.1f, 0.0f);
        Vector3 authoredOffset = new(0.0f, -0.5f, 0.0f);
        Transform3D hand = new(Basis.Identity, selectedPoint);

        try
        {
            grabPoint.GrabPointPositionOffsetFromHand = authoredOffset;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(selectedPoint) <= PositionToleranceMetres,
                $"Expected raw centred acquisition to select {selectedPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Assert.Equal(authoredOffset, candidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(grabPoint.GrabPointRotationOffsetFromHand, candidate.GrabPointRotationOffsetFromHand);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(selectedPoint) <= PositionToleranceMetres,
                $"Expected raw authored offset composition to recover selected point {selectedPoint}, observed {reconstructedGrabPoint.Origin}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies the default zero snap distance preserves existing perpendicular reach rejection.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_DefaultZeroSnapDistance_DoesNotExtendPerpendicularReach()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.09f, 0.1f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies positive snap distance allows early acquisition only across the cylinder's perpendicular axis.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PositiveSnapDistance_AllowsPerpendicularEarlyAcquisition()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.1f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.09f, 0.1f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.SnapDistanceMetres = 0.09f;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand, expectedClosestPoint);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies snap distance is an absolute perpendicular threshold rather than reach plus snap.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PerpendicularDistanceGreaterThanSnapButWithinReachPlusSnap_ReturnsNull()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.09f, 0.1f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.SnapDistanceMetres = 0.04f;

            Assert.True(
                0.09f > grabPoint.SnapDistanceMetres,
                "Test setup requires perpendicular distance to exceed the snap threshold.");
            Assert.True(
                0.09f <= grabPoint.ReachDistanceMetres + grabPoint.SnapDistanceMetres,
                "Test setup requires perpendicular distance to be within the former reach-plus-snap threshold.");

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies snap distance does not hide along-axis removal past a cylinder end.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PositiveSnapDistance_DoesNotExtendAlongAxisReachPastSegmentEnd()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.02f, 0.27f, 0.0f), new Vector3(-0.02f, -0.07f, 0.0f));

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.SnapDistanceMetres = 0.2f;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies snap-only acquisition is disabled when the projected reference lies beyond the cylinder end.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_SnapEligibleRadialDistanceBeyondSegmentEndWhenOrdinaryReachFails_ReturnsNull()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.09f, 0.23f, 0.0f), new Vector3(-0.09f, -0.03f, 0.0f));

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.SnapDistanceMetres = 0.2f;

            Assert.True(
                hand.Origin.DistanceTo(new Vector3(0.0f, LengthMetres * 0.5f, 0.0f)) > grabPoint.ReachDistanceMetres,
                "Test setup requires ordinary clamped closest-point reach to fail.");
            Assert.True(
                0.09f <= grabPoint.SnapDistanceMetres,
                "Test setup requires radial distance to be snap-eligible.");

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.Null(candidate);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies snap acquisition still returns the actual selected point and raw authored offsets.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_PositiveSnapDistance_PreservesActualHeldPoseAndRawOffsets()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.13f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.1f, 0.13f, 0.0f), Vector3.Left);
        Vector3 authoredOffset = WorldVectorToHandLocal(new Vector3(-0.02f, 0.03f, 0.01f), hand.Basis);
        var authoredRotationOffset = new Vector3(0.1f, -0.2f, 0.3f);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.SnapDistanceMetres = 0.1f;
            grabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            grabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected snapped candidate to keep actual selected point {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Assert.True(
                Mathf.Abs(candidate.AcquisitionDistance - hand.Origin.DistanceTo(expectedClosestPoint)) <= PositionToleranceMetres,
                $"Expected snapped acquisition distance to remain selected closest-point distance, observed {candidate.AcquisitionDistance}.");
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, authoredRotationOffset),
                candidate.GrabPointTransform.Basis);
            Assert.Equal(authoredOffset, candidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(authoredRotationOffset, candidate.GrabPointRotationOffsetFromHand);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected raw authored offset composition to recover actual selected point {expectedClosestPoint}, observed {reconstructedGrabPoint.Origin}.");
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
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 handOrigin = new(0.05f, 0.1f, 0.0f);
        Vector3 palmDirectionAtThreshold = ((0.75f * Vector3.Left)
            + (Mathf.Sqrt(1.0f - (0.75f * 0.75f)) * Vector3.Up))
            .Normalized();
        Transform3D hand = CreateHandTransform(handOrigin, palmDirectionAtThreshold);
        float threshold = palmDirectionAtThreshold.Dot(Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = threshold;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            AssertCandidateMatchesGrabPoint(candidate, grabPoint, animation, hand, new Vector3(0.0f, 0.1f, 0.0f));
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies authored offsets shift the IK target while preserving the selected grab-point rotation.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithAuthoredOffset_ComposesAroundSelectedPoint()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.1f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.05f, 0.1f, 0.0f), Vector3.Left);
        Vector3 offsetPosition = WorldVectorToHandLocal(new Vector3(-0.04f, 0.0f, 0.0f), hand.Basis);
        var offsetRotation = Basis.FromEuler(new Vector3(0.0f, 0.3f, 0.0f));

        try
        {
            grabPoint.GrabPointPositionOffsetFromHand = offsetPosition;
            grabPoint.GrabPointRotationOffsetFromHand = offsetRotation.GetEuler();

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(candidate);
            Assert.Same(grabPoint, candidate.Source);
            Assert.Same(animation, candidate.Animation);
            Assert.Equal(LimbSide.Right, candidate.HandSide);
            Assert.Equal(hand, candidate.HandTransform);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected candidate grab point at selected closest point {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, grabPoint.GrabPointRotationOffsetFromHand),
                candidate.GrabPointTransform.Basis);
            Assert.Equal(offsetPosition, candidate.GrabPointPositionOffsetFromHand);
            AssertBasisApproximatelyEqual(offsetRotation, Basis.FromEuler(candidate.GrabPointRotationOffsetFromHand));
            AssertBasisApproximatelyEqual(
                candidate.GrabPointTransform.Basis,
                (candidate.HandTarget * candidate.GrabPointOffsetFromHand).Basis);
            Assert.True(
                (candidate.HandTarget * candidate.GrabPointOffsetFromHand).Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected the authored offset to put the grab point at the closest point {expectedClosestPoint}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies reach uses the authored grip reference point instead of the raw hand origin.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_RawHandOriginOutsideReachButAuthoredGripReferenceWithinReach_ReturnsCandidate()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.15f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.12f, 0.15f, 0.0f), Vector3.Left);
        Vector3 authoredOffsetWorld = new(-0.08f, 0.0f, 0.0f);

        try
        {
            grabPoint.GrabPointPositionOffsetFromHand = WorldVectorToHandLocal(authoredOffsetWorld, hand.Basis);

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Assert.Same(grabPoint, candidate.Source);
            Assert.Same(animation, candidate.Animation);
            Assert.Equal(hand, candidate.HandTransform);
            Assert.Equal(grabPoint.GrabPointPositionOffsetFromHand, candidate.GrabPointPositionOffsetFromHand);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected candidate grab point at selected closest point {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Vector3 authoredGripReference = hand.Origin + (hand.Basis.Orthonormalized() * grabPoint.GrabPointPositionOffsetFromHand);
            Assert.True(
                Mathf.Abs(candidate.AcquisitionDistance - authoredGripReference.DistanceTo(expectedClosestPoint)) <= PositionToleranceMetres,
                $"Expected acquisition distance to come from the accepted authored grip reference, observed {candidate.AcquisitionDistance}.");
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected offset composition to recover selected point {expectedClosestPoint}, observed {reconstructedGrabPoint.Origin}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies marker roll around the cylinder axis is ignored when composing selected frames and hand targets.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_RolledAroundLocalY_ReturnsInvariantSelectedFrameAndHandTarget()
    {
        using var animation = new Animation();
        CylindricalGrabPoint unrolledGrabPoint = CreateGrabPoint(animation);
        CylindricalGrabPoint rolledGrabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.11f, 0.0f);
        Transform3D hand = CreateHandTransform(expectedClosestPoint + new Vector3(0.04f, 0.0f, 0.0f), Vector3.Left);
        Vector3 authoredOffset = new(0.02f, -0.03f, 0.04f);
        var authoredRotationOffset = new Vector3(0.27f, -0.41f, 0.33f);

        try
        {
            unrolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            rolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            unrolledGrabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            rolledGrabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            unrolledGrabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;
            rolledGrabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;
            rolledGrabPoint.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, 1.37f, 0.0f)), Vector3.Zero);

            GrabPointCandidate? unrolledCandidate = unrolledGrabPoint.GetGrabPoint(LimbSide.Right, hand);
            GrabPointCandidate? rolledCandidate = rolledGrabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(unrolledCandidate);
            Assert.NotNull(rolledCandidate);
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.GrabPointTransform, rolledCandidate.GrabPointTransform);
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.HandTarget, rolledCandidate.HandTarget);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(unrolledGrabPoint.GlobalTransform.Basis, hand.Basis, authoredRotationOffset),
                rolledCandidate.GrabPointTransform.Basis);
            Assert.Equal(authoredOffset, rolledCandidate.GrabPointPositionOffsetFromHand);
            Assert.Equal(authoredRotationOffset, rolledCandidate.GrabPointRotationOffsetFromHand);
        }
        finally
        {
            unrolledGrabPoint.Free();
            rolledGrabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies hand twist around the cylinder length axis changes the selected frame instead of snapping to a fixed
    /// world-derived phase.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_HandTwistAroundCylinderY_PreservesHandReferencedPhase()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.1f, 0.0f);
        Transform3D untwistedHand = new(Basis.Identity, expectedClosestPoint + new Vector3(0.04f, 0.0f, 0.0f));
        Transform3D twistedHand = new(
            Basis.FromEuler(new Vector3(0.0f, 0.71f, 0.0f)).Orthonormalized(),
            untwistedHand.Origin);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;

            GrabPointCandidate? untwistedCandidate = grabPoint.GetGrabPoint(LimbSide.Left, untwistedHand);
            GrabPointCandidate? twistedCandidate = grabPoint.GetGrabPoint(LimbSide.Left, twistedHand);

            Assert.NotNull(untwistedCandidate);
            Assert.NotNull(twistedCandidate);
            Assert.True(
                untwistedCandidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected untwisted selected point {expectedClosestPoint}, observed {untwistedCandidate.GrabPointTransform.Origin}.");
            Assert.True(
                twistedCandidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected twisted selected point {expectedClosestPoint}, observed {twistedCandidate.GrabPointTransform.Origin}.");
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, untwistedHand.Basis, Vector3.Zero),
                untwistedCandidate.GrabPointTransform.Basis);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, twistedHand.Basis, Vector3.Zero),
                twistedCandidate.GrabPointTransform.Basis);
            AssertBasisDifferent(untwistedCandidate.GrabPointTransform.Basis, twistedCandidate.GrabPointTransform.Basis);
            AssertBasisDifferent(untwistedCandidate.HandTarget.Basis, twistedCandidate.HandTarget.Basis);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies non-identity rotation offsets phase the cylindrical frame from the authored grab frame rather than the
    /// raw hand frame alone.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithRotationOffset_UsesAuthoredGrabFramePhase()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = new(Basis.Identity, new Vector3(0.04f, 0.1f, 0.0f));
        var authoredRotationOffset = new Vector3(0.0f, 0.63f, 0.0f);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Basis rawHandPhaseBasis = CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, Vector3.Zero);
            Basis authoredPhaseBasis = CreateExpectedCylindricalAxisBasis(
                grabPoint.GlobalTransform.Basis,
                hand.Basis,
                authoredRotationOffset);
            AssertBasisApproximatelyEqual(authoredPhaseBasis, candidate.GrabPointTransform.Basis);
            AssertBasisDifferent(rawHandPhaseBasis, candidate.GrabPointTransform.Basis);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies a degenerate preferred X projection falls back to a stable secondary hand axis without NaN and without
    /// item-roll dependence.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_DegeneratePreferredProjection_UsesStableRollIndependentFallback()
    {
        using var animation = new Animation();
        CylindricalGrabPoint unrolledGrabPoint = CreateGrabPoint(animation);
        CylindricalGrabPoint rolledGrabPoint = CreateGrabPoint(animation);
        Transform3D hand = new(
            new Basis(Vector3.Up, Vector3.Right, Vector3.Forward).Orthonormalized(),
            new Vector3(0.04f, 0.1f, 0.0f));

        try
        {
            unrolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            rolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            rolledGrabPoint.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, -1.24f, 0.0f)), Vector3.Zero);

            GrabPointCandidate? unrolledCandidate = unrolledGrabPoint.GetGrabPoint(LimbSide.Left, hand);
            GrabPointCandidate? rolledCandidate = rolledGrabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(unrolledCandidate);
            Assert.NotNull(rolledCandidate);
            AssertFinite(unrolledCandidate.GrabPointTransform.Basis);
            AssertFinite(rolledCandidate.GrabPointTransform.Basis);
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.GrabPointTransform, rolledCandidate.GrabPointTransform);
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.HandTarget, rolledCandidate.HandTarget);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(unrolledGrabPoint.GlobalTransform.Basis, hand.Basis, Vector3.Zero),
                unrolledCandidate.GrabPointTransform.Basis);
        }
        finally
        {
            unrolledGrabPoint.Free();
            rolledGrabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies composed hand target and authored offset reconstruct the hand-referenced cylindrical frame.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_ReconstructsHandReferencedCylindricalFrame()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = new(
            Basis.FromEuler(new Vector3(0.18f, 0.52f, -0.27f)).Orthonormalized(),
            new Vector3(0.045f, -0.06f, 0.0f));
        Vector3 authoredOffset = new(0.012f, -0.018f, 0.026f);
        var authoredRotationOffset = new Vector3(-0.31f, 0.44f, 0.22f);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            grabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(candidate);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            AssertCandidateTransformsApproximatelyEqual(candidate.GrabPointTransform, reconstructedGrabPoint);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, authoredRotationOffset),
                reconstructedGrabPoint.Basis);
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies candidate hand target and authored offset reconstruct the roll-invariant selected frame.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithRolledMarker_ReconstructsRollInvariantSelectedFrame()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, -0.08f, 0.0f);
        Transform3D hand = CreateHandTransform(expectedClosestPoint + new Vector3(0.035f, 0.0f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, 2.2f, 0.0f)), Vector3.Zero);
            grabPoint.GrabPointPositionOffsetFromHand = new Vector3(0.03f, 0.02f, -0.025f);
            grabPoint.GrabPointRotationOffsetFromHand = new Vector3(0.4f, -0.15f, 0.25f);

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Right, hand);

            Assert.NotNull(candidate);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            AssertCandidateTransformsApproximatelyEqual(candidate.GrabPointTransform, reconstructedGrabPoint);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, grabPoint.GrabPointRotationOffsetFromHand),
                reconstructedGrabPoint.Basis);
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected reconstruction to recover selected point {expectedClosestPoint}, observed {reconstructedGrabPoint.Origin}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies roll invariance for a parented, rotated cylinder while preserving the actual global local-Y axis.
    /// </summary>
    [Headless]
    [Fact]
    public async Task GetGrabPoint_ParentedTiltedAndRolledAroundLocalY_UsesAxisButIgnoresRoll()
    {
        using var animation = new Animation();
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D unrolledParent = new()
        {
            Name = "UnrolledTiltedCylinderParent"
        };
        Node3D rolledParent = new()
        {
            Name = "RolledTiltedCylinderParent"
        };
        CylindricalGrabPoint unrolledGrabPoint = CreateGrabPoint(animation);
        CylindricalGrabPoint rolledGrabPoint = CreateGrabPoint(animation);
        Basis parentBasis = Basis.FromEuler(new Vector3(0.35f, 0.72f, -0.41f)).Orthonormalized();
        Vector3 parentOrigin = new(0.2f, -0.1f, 0.35f);
        Vector3 localSelectedPoint = new(0.0f, 0.12f, 0.0f);
        Vector3 radialOffset = parentBasis.X.Normalized() * 0.04f;
        Vector3 authoredOffset = new(-0.015f, 0.025f, 0.035f);
        var authoredRotationOffset = new Vector3(-0.2f, 0.16f, 0.49f);

        try
        {
            unrolledParent.Transform = new Transform3D(parentBasis, parentOrigin);
            rolledParent.Transform = new Transform3D(parentBasis, parentOrigin);
            rolledGrabPoint.Transform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, -1.21f, 0.0f)), Vector3.Zero);
            unrolledParent.AddChild(unrolledGrabPoint);
            rolledParent.AddChild(rolledGrabPoint);
            sceneTree.Root.AddChild(unrolledParent);
            sceneTree.Root.AddChild(rolledParent);
            unrolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            rolledGrabPoint.PalmFacingMinimumDot = -1.0f;
            unrolledGrabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            rolledGrabPoint.GrabPointPositionOffsetFromHand = authoredOffset;
            unrolledGrabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;
            rolledGrabPoint.GrabPointRotationOffsetFromHand = authoredRotationOffset;
            unrolledParent.ForceUpdateTransform();
            rolledParent.ForceUpdateTransform();
            unrolledGrabPoint.ForceUpdateTransform();
            rolledGrabPoint.ForceUpdateTransform();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
            Vector3 expectedClosestPoint = unrolledGrabPoint.GlobalTransform * localSelectedPoint;
            Transform3D hand = CreateHandTransform(expectedClosestPoint, -radialOffset);

            GrabPointCandidate? unrolledCandidate = unrolledGrabPoint.GetGrabPoint(LimbSide.Left, hand);
            GrabPointCandidate? rolledCandidate = rolledGrabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(unrolledCandidate);
            Assert.NotNull(rolledCandidate);
            Assert.True(
                rolledCandidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected rolled tilted candidate to select {expectedClosestPoint}, observed {rolledCandidate.GrabPointTransform.Origin}.");
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.GrabPointTransform, rolledCandidate.GrabPointTransform);
            AssertCandidateTransformsApproximatelyEqual(unrolledCandidate.HandTarget, rolledCandidate.HandTarget);
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(rolledGrabPoint.GlobalTransform.Basis, hand.Basis, authoredRotationOffset),
                rolledCandidate.GrabPointTransform.Basis);
            Assert.True(
                Mathf.Abs(rolledCandidate.GrabPointTransform.Basis.Y.Dot(rolledGrabPoint.GlobalTransform.Basis.Orthonormalized().Y.Normalized())) >= 1.0f - BasisTolerance,
                $"Expected selected Y axis to follow the actual cylinder local-Y line {rolledGrabPoint.GlobalTransform.Basis.Orthonormalized().Y.Normalized()} without requiring oriented polarity, observed {rolledCandidate.GrabPointTransform.Basis.Y}.");
        }
        finally
        {
            unrolledParent.QueueFree();
            rolledParent.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies any authored along-axis offset is preserved in the returned candidate offset.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_WithAuthoredAxialOffset_PreservesCandidateOffset()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.08f, 0.1f, 0.0f), Vector3.Left);
        Vector3 authoredOffsetWorld = new(-0.04f, 0.07f, 0.0f);

        try
        {
            Vector3 authoredOffsetLocal = WorldVectorToHandLocal(authoredOffsetWorld, hand.Basis);
            grabPoint.GrabPointPositionOffsetFromHand = authoredOffsetLocal;

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Assert.Equal(authoredOffsetLocal, candidate.GrabPointPositionOffsetFromHand);
            Vector3 candidateOffsetWorld = hand.Basis.Orthonormalized() * candidate.GrabPointPositionOffsetFromHand;
            Assert.True(
                Mathf.Abs(candidateOffsetWorld.Dot(Vector3.Up) - 0.07f) <= PositionToleranceMetres,
                $"Expected candidate offset to preserve local-Y axial component, observed {candidateOffsetWorld}.");
            Assert.True(
                candidateOffsetWorld.DistanceTo(authoredOffsetWorld) <= PositionToleranceMetres,
                $"Expected candidate offset to preserve authored local offset, observed {candidateOffsetWorld}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies near-end grabs reconstruct the clamped selected point rather than encoding a beyond-end point.
    /// </summary>
    [Headless]
    [Fact]
    public void GetGrabPoint_NearEndWithAuthoredAxialOffset_ReconstructsClampedSelectedPoint()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Transform3D hand = CreateHandTransform(new Vector3(0.07f, 0.14f, 0.0f), new Vector3(-0.05f, 0.06f, 0.0f));
        Vector3 authoredOffsetWorld = new(-0.03f, 0.08f, 0.0f);
        Vector3 expectedClampedPoint = new(0.0f, 0.2f, 0.0f);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabPoint.GrabPointPositionOffsetFromHand = WorldVectorToHandLocal(authoredOffsetWorld, hand.Basis);

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClampedPoint) <= PositionToleranceMetres,
                $"Expected selected point clamped to segment end {expectedClampedPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClampedPoint) <= PositionToleranceMetres,
                $"Expected offset composition to reconstruct clamped end {expectedClampedPoint}, observed {reconstructedGrabPoint.Origin}.");
            Assert.InRange(
                reconstructedGrabPoint.Origin.Y,
                (-LengthMetres * 0.5f) - PositionToleranceMetres,
                (LengthMetres * 0.5f) + PositionToleranceMetres);

            Vector3 candidateOffsetWorld = hand.Basis.Orthonormalized() * candidate.GrabPointPositionOffsetFromHand;
            Assert.True(
                candidateOffsetWorld.DistanceTo(authoredOffsetWorld) <= PositionToleranceMetres,
                $"Expected candidate offset to preserve authored offset instead of stripping the axial component, observed {candidateOffsetWorld}.");
        }
        finally
        {
            grabPoint.Free();
        }
    }

    /// <summary>
    /// Verifies parent and child rotations are composed before selecting the cylinder axis.
    /// </summary>
    [Headless]
    [Fact]
    public async Task GetGrabPoint_ParentChildRotationsCancel_UsesActualGlobalYAxisForSelectionAndClamping()
    {
        using var animation = new Animation();
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        SceneTree sceneTree = TestUtils.GetSceneTree();
        Node3D root = new()
        {
            Name = "CancellingRotationCylinderRoot"
        };
        var parentBasis = Basis.FromEuler(new Vector3(0.0f, 0.0f, Mathf.Pi * 0.5f));
        var childLocalBasis = Basis.FromEuler(new Vector3(0.0f, 0.0f, -Mathf.Pi * 0.5f));
        Vector3 expectedClampedPoint = Vector3.Up * (LengthMetres * 0.5f);
        Vector3 handOrigin = expectedClampedPoint + new Vector3(0.04f, 0.04f, 0.0f);
        Transform3D hand = CreateHandTransform(handOrigin, expectedClampedPoint - handOrigin);

        try
        {
            root.Transform = new Transform3D(parentBasis, Vector3.Zero);
            grabPoint.Transform = new Transform3D(childLocalBasis, Vector3.Zero);
            root.AddChild(grabPoint);
            sceneTree.Root.AddChild(root);
            grabPoint.PalmFacingMinimumDot = -1.0f;
            root.ForceUpdateTransform();
            grabPoint.ForceUpdateTransform();
            await TestUtils.WaitForNextFrameAsync(sceneTree);

            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            AssertBasisApproximatelyEqual(Basis.Identity, grabPoint.GlobalTransform.Basis.Orthonormalized());
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClampedPoint) <= PositionToleranceMetres,
                $"Expected selected point clamped along actual global local Y to {expectedClampedPoint}, observed {candidate.GrabPointTransform.Origin}.");
            AssertBasisApproximatelyEqual(
                CreateExpectedCylindricalAxisBasis(grabPoint.GlobalTransform.Basis, hand.Basis, grabPoint.GrabPointRotationOffsetFromHand),
                candidate.GrabPointTransform.Basis);
            Transform3D reconstructedGrabPoint = candidate.HandTarget * candidate.GrabPointOffsetFromHand;
            Assert.True(
                reconstructedGrabPoint.Origin.DistanceTo(expectedClampedPoint) <= PositionToleranceMetres,
                $"Expected raw authored offset composition to reconstruct globally clamped point {expectedClampedPoint}, observed {reconstructedGrabPoint.Origin}.");
        }
        finally
        {
            root.QueueFree();
            await TestUtils.WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies grabbable freshness accepts stable dynamic selected transforms that differ from the marker centre.
    /// </summary>
    [Headless]
    [Fact]
    public void GrabbableNode_GrabWithDynamicSelectedTransform_AcceptsFreshCandidate()
    {
        using var animation = new Animation();
        GrabbableNode grabbable = new()
        {
            Name = "CylindricalDynamicTransformGrabbable"
        };
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.12f, 0.0f);
        Transform3D hand = CreateHandTransform(new Vector3(0.05f, 0.12f, 0.0f), Vector3.Left);

        try
        {
            grabbable.AddChild(grabPoint);
            grabbable.RefreshComponents();

            GrabPointCandidate? candidate = ((IGrabbable)grabbable).GetGrabPoint(LimbSide.Left, hand);

            Assert.NotNull(candidate);
            Assert.True(
                candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
                $"Expected selected transform at closest point {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
            Assert.True(grabbable.Grab(candidate));
            Assert.True(grabbable.IsGrabbed);
        }
        finally
        {
            grabbable.Free();
        }
    }

    /// <summary>
    /// Verifies grabbable freshness accepts marker roll-only drift around the cylinder length axis.
    /// </summary>
    [Headless]
    [Fact]
    public void GrabbableNode_GrabAfterRollOnlyDriftAroundCylinderY_AcceptsFreshCandidate()
    {
        using var animation = new Animation();
        GrabbableNode grabbable = new()
        {
            Name = "CylindricalRollOnlyFreshnessGrabbable"
        };
        CylindricalGrabPoint grabPoint = CreateGrabPoint(animation);
        Vector3 expectedClosestPoint = new(0.0f, 0.12f, 0.0f);
        Transform3D hand = CreateHandTransform(expectedClosestPoint + new Vector3(0.04f, 0.0f, 0.0f), Vector3.Left);

        try
        {
            grabPoint.PalmFacingMinimumDot = -1.0f;
            grabbable.AddChild(grabPoint);
            grabbable.RefreshComponents();
            GrabPointCandidate? candidate = ((IGrabbable)grabbable).GetGrabPoint(LimbSide.Left, hand);
            Assert.NotNull(candidate);

            grabPoint.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(0.0f, 1.1f, 0.0f)), Vector3.Zero);

            Assert.True(grabbable.Grab(candidate));
            Assert.True(grabbable.IsGrabbed);
        }
        finally
        {
            grabbable.Free();
        }
    }

    private static CylindricalGrabPoint CreateGrabPoint(Animation animation) =>
        new()
        {
            LengthMetres = LengthMetres,
            ReachDistanceMetres = ReachDistanceMetres,
            PalmFacingMinimumDot = 0.75f,
            PalmLocalDirection = Vector3.Down,
            GrabAnimation = animation,
        };

    private static Transform3D CreateHandTransform(Vector3 origin, Vector3 palmWorldDirection)
    {
        Vector3 yAxis = -palmWorldDirection.Normalized();
        Vector3 helperAxis = Mathf.Abs(yAxis.Dot(Vector3.Up)) > 0.9f ? Vector3.Right : Vector3.Up;
        Vector3 xAxis = helperAxis.Cross(yAxis).Normalized();
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();

        return new Transform3D(new Basis(xAxis, yAxis, zAxis), origin);
    }

    private static Vector3 WorldVectorToHandLocal(Vector3 worldVector, Basis handBasis) =>
        worldVector * handBasis.Orthonormalized();

    private static void AssertCandidateMatchesGrabPoint(
        GrabPointCandidate? candidate,
        CylindricalGrabPoint grabPoint,
        Animation animation,
        Transform3D expectedHandTransform,
        Vector3 expectedClosestPoint)
    {
        Assert.NotNull(candidate);
        Assert.Same(grabPoint, candidate.Source);
        Assert.Same(animation, candidate.Animation);
        Assert.Equal(expectedHandTransform, candidate.HandTransform);
        Assert.True(
            candidate.GrabPointTransform.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
            $"Expected candidate grab point at selected closest point {expectedClosestPoint}, observed {candidate.GrabPointTransform.Origin}.");
        AssertBasisApproximatelyEqual(
            CreateExpectedCylindricalAxisBasis(
                grabPoint.GlobalTransform.Basis,
                expectedHandTransform.Basis,
                candidate.GrabPointRotationOffsetFromHand),
            candidate.GrabPointTransform.Basis);
        Assert.True(
            candidate.HandTarget.Origin.DistanceTo(expectedClosestPoint) <= PositionToleranceMetres,
            $"Expected hand target origin at closest point {expectedClosestPoint}, observed {candidate.HandTarget.Origin}.");
        Assert.True(
            Mathf.Abs(candidate.AcquisitionDistance - expectedHandTransform.Origin.DistanceTo(expectedClosestPoint)) <= PositionToleranceMetres,
            $"Expected acquisition distance to selected closest point, observed {candidate.AcquisitionDistance}.");
        AssertBasisApproximatelyEqual(candidate.GrabPointTransform.Basis, candidate.HandTarget.Basis);
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

    private static void AssertBasisDifferent(Basis first, Basis second)
    {
        Assert.True(
            first.X.DistanceTo(second.X) > BasisTolerance || first.Z.DistanceTo(second.Z) > BasisTolerance,
            $"Expected basis phase to differ, but observed matching X/Z axes {first.X}/{first.Z}.");
    }

    private static void AssertFinite(Basis basis)
    {
        AssertFinite(basis.X);
        AssertFinite(basis.Y);
        AssertFinite(basis.Z);
    }

    private static void AssertFinite(Vector3 vector)
    {
        Assert.False(float.IsNaN(vector.X) || float.IsInfinity(vector.X), $"Expected finite X component, observed {vector}.");
        Assert.False(float.IsNaN(vector.Y) || float.IsInfinity(vector.Y), $"Expected finite Y component, observed {vector}.");
        Assert.False(float.IsNaN(vector.Z) || float.IsInfinity(vector.Z), $"Expected finite Z component, observed {vector}.");
    }

    private static Basis CreateExpectedCylindricalAxisBasis(
        Basis grabPointBasis,
        Basis handBasis,
        Vector3 grabPointRotationOffsetFromHand)
    {
        Basis authoredGrabFrameBasis = (handBasis.Orthonormalized() * Basis.FromEuler(grabPointRotationOffsetFromHand)).Orthonormalized();
        Vector3 yAxis = grabPointBasis.Orthonormalized().Y.Normalized();
        if (authoredGrabFrameBasis.Y.Dot(yAxis) < 0.0f)
        {
            yAxis = -yAxis;
        }

        Vector3 projectedXAxis = ProjectOntoPlane(authoredGrabFrameBasis.X, yAxis);

        if (projectedXAxis.LengthSquared() > 0.000001f)
        {
            return CreateBasisFromXAxis(projectedXAxis.Normalized(), yAxis);
        }

        Vector3 projectedZAxis = ProjectOntoPlane(authoredGrabFrameBasis.Z, yAxis);
        if (projectedZAxis.LengthSquared() > 0.000001f)
        {
            Vector3 zAxis = projectedZAxis.Normalized();
            Vector3 xAxis = yAxis.Cross(zAxis).Normalized();

            return CreateBasisFromXAxis(xAxis, yAxis);
        }

        Vector3 referenceAxis = Mathf.Abs(yAxis.Dot(Vector3.Up)) > 0.98f ? Vector3.Right : Vector3.Up;
        Vector3 fallbackXAxis = ProjectOntoPlane(referenceAxis, yAxis).Normalized();

        return CreateBasisFromXAxis(fallbackXAxis, yAxis);
    }

    private static Vector3 ProjectOntoPlane(Vector3 axis, Vector3 planeNormal) =>
        axis - (planeNormal * axis.Dot(planeNormal));

    private static Basis CreateBasisFromXAxis(Vector3 xAxis, Vector3 yAxis)
    {
        Vector3 zAxis = xAxis.Cross(yAxis).Normalized();

        return new Basis(xAxis, yAxis, zAxis).Orthonormalized();
    }

    private static void AssertCandidateTransformsApproximatelyEqual(Transform3D expected, Transform3D actual)
    {
        Assert.True(
            expected.Origin.DistanceTo(actual.Origin) <= PositionToleranceMetres,
            $"Expected transform origin {expected.Origin}, observed {actual.Origin}.");
        AssertBasisApproximatelyEqual(expected.Basis, actual.Basis);
    }
}
