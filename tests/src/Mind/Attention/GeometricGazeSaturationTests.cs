using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;
/// <summary>
/// Independent vector-fixture coverage for the bounded geometric saturation helper (AI-009 spec gate 25). Oracles
/// are built from first principles with Godot quaternion/vector math on the test side; they never call into the
/// helper's internal math.
/// </summary>
public sealed class GeometricGazeSaturationTests
{
    private const float AnchorDistance = 2f;

    private static readonly Vector3 _eyeOrigin = new(0f, 1.5f, 0f);

    /// <summary>
    /// HEAD-2 mandatory counterexample: with the head commanded to −40° pitch, a target whose true solved-face-local
    /// direction is yaw 14° / pitch 0° must classify geometrically as inside a 17° cone. The scalar frame-subtraction
    /// mechanism misclassifies it as (18.029°, 1.414°) crossing the threshold.
    /// </summary>
    [Fact]
    public void Solve_HeadPitchMinus40WithTrueLocalYaw14_ClassifiesGeometricallyWhereScalarSubtractionFails()
    {
        Basis reference = RollFreeBasis(yawRadians: 0d, pitchRadians: Degrees(-40d));
        Vector3 anchor = _eyeOrigin + (reference * DirectionFromAngles(Degrees(14d), 0d) * AnchorDistance);

        // Scalar reproduction of the HEAD-2 mechanism, from first principles: extract the anchor direction in the
        // neutral (envelope) frame, then subtract the applied command angles per axis.
        Vector3 neutralLocal = anchor - _eyeOrigin;
        double scalarYaw = Math.Atan2(-neutralLocal.X, -neutralLocal.Z);
        double scalarPitch = Math.Atan2(
            neutralLocal.Y,
            Math.Sqrt((neutralLocal.X * neutralLocal.X) + (neutralLocal.Z * neutralLocal.Z)));
        double headRelativeYaw = scalarYaw - 0d;
        double headRelativePitch = scalarPitch - Degrees(-40d);

        // Exact scalar numbers: yaw 18.029° crosses the 17° bound even though the true face-local yaw is 14°.
        Assert.InRange(ToDegrees(headRelativeYaw), 18.02d, 18.04d);
        Assert.InRange(ToDegrees(headRelativePitch), 1.40d, 1.43d);
        Assert.True(ToDegrees(headRelativeYaw) > 17d, "Scalar subtraction must reproduce the reported misclassification.");

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.True(result.IsFeasible);
        Assert.Equal(0d, result.RotationFromReferenceRadians, 10);
        Assert.Equal(0d, result.ResidualRadians, 10);
        Assert.InRange(ToDegrees(result.FaceLocalTargetYawRadians), 13.999d, 14.001d);
        Assert.InRange(Math.Abs(ToDegrees(result.FaceLocalTargetPitchRadians)), 0d, 0.001d);
        Assert.True(ToDegrees(result.FaceLocalTargetYawRadians) < 17d, "Geometric classification stays below the cone bound.");
    }

    /// <summary>An interior target already inside the cone needs zero orientation change.</summary>
    [Fact]
    public void Solve_TargetInsideCone_ReturnsReferenceCommandUnchanged()
    {
        Basis reference = RollFreeBasis(Degrees(3d), Degrees(-12d));
        Vector3 anchor = _eyeOrigin + (reference * DirectionFromAngles(Degrees(10d), Degrees(-7d)) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.True(result.IsFeasible);
        Assert.Equal(0d, result.RotationFromReferenceRadians, 10);
        Assert.Equal(1, result.Iterations);
        Assert.InRange(ToDegrees(result.YawRadians), 2.999d, 3.001d);
        Assert.InRange(ToDegrees(result.PitchRadians), -12.001d, -11.999d);
        AssertEnvelopeAndRollFree(result, Bounds(75d, 75d, 40d, 55d));
    }

    /// <summary>A target exactly on the closed cone boundary counts as inside, without measurable correction.</summary>
    [Fact]
    public void Solve_TargetExactlyOnConeBoundary_CountsInsideWithoutMeaningfulCorrection()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (reference * DirectionFromAngles(Degrees(17d), Degrees(5d)) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.True(result.IsFeasible);
        Assert.True(result.RotationFromReferenceRadians < 1e-5d, "Boundary targets must not trigger a meaningful correction.");
        Assert.InRange(ToDegrees(result.FaceLocalTargetYawRadians), 16.999d, 17.001d);
        Assert.InRange(ToDegrees(result.FaceLocalTargetPitchRadians), 4.999d, 5.001d);
    }

    /// <summary>
    /// A single-axis out-of-cone target is corrected to sit exactly on the cone boundary with the least single-axis
    /// rotation, verified independently.
    /// </summary>
    [Fact]
    public void Solve_YawBeyondCone_CorrectsToExactBoundaryWithMinimalRotation()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (reference * DirectionFromAngles(Degrees(25d), 0d) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.True(result.IsFeasible);
        Assert.InRange(ToDegrees(result.FaceLocalTargetYawRadians), 16.9999d, 17.0001d);
        Assert.InRange(Math.Abs(ToDegrees(result.FaceLocalTargetPitchRadians)), 0d, 0.0001d);
        // At zero pitch the composition is uncoupled, so the least rotation equals the scalar excess exactly.
        Assert.InRange(ToDegrees(result.RotationFromReferenceRadians), 7.9999d, 8.0001d);
        AssertIndependentlyVerified(result, anchor, Bounds(17d, 17d, 10d, 15d));
    }

    /// <summary>
    /// Coupled-axis geometry: at −40° pitch, correcting a 25° face-local yaw to the 17° bound requires composing
    /// rotations (a 10.266° envelope-yaw correction), not the 8° scalar difference — and the scalar correction
    /// demonstrably leaves the gaze outside the cone.
    /// </summary>
    [Fact]
    public void Solve_CoupledYawAtDeepPitch_ComposesGeometryInsteadOfScalarSubtraction()
    {
        Basis reference = RollFreeBasis(0d, Degrees(-40d));
        Vector3 anchor = _eyeOrigin + (reference * DirectionFromAngles(Degrees(25d), 0d) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.True(result.IsFeasible);

        // Independent bisection oracle on Godot quaternion geometry: the envelope yaw whose composed face-local yaw
        // is exactly the 17° bound at −40° pitch.
        double oracleYaw = BisectEnvelopeYaw(anchor, Degrees(-40d), Degrees(17d), Degrees(75d));
        Assert.Equal(ToDegrees(oracleYaw), ToDegrees(result.YawRadians), 4);
        Assert.InRange(ToDegrees(result.YawRadians), 10.2d, 10.4d);

        // The scalar 8° correction leaves the composed face-local yaw measurably outside the cone.
        Basis scalarCorrected = RollFreeBasis(Degrees(25d) - Degrees(17d), Degrees(-40d));
        (double scalarLocalYaw, _) = FaceLocalAngles(scalarCorrected, anchor - _eyeOrigin);
        Assert.True(ToDegrees(scalarLocalYaw) > 17.5d, "The scalar correction must leave a coupled residual outside the cone.");

        // The helper's result verifies exactly at the boundary under independent composition.
        AssertIndependentlyVerified(result, anchor, Bounds(17d, 17d, 10d, 15d));
    }

    /// <summary>
    /// A target beyond the envelope is unresolvable: the helper pins to the envelope bound, reports infeasible with
    /// an explicit residual, and stops deterministically.
    /// </summary>
    [Fact]
    public void Solve_TargetBeyondEnvelope_ClampsToBoundAndReportsExplicitResidual()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(100d), 0d) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.False(result.IsFeasible);
        Assert.Equal(Degrees(75d), result.YawRadians, 10);
        Assert.Equal(0d, result.PitchRadians, 10);
        Assert.InRange(ToDegrees(result.ResidualRadians), 7.999d, 8.001d);
        Assert.True(result.Iterations <= 5, "Envelope pinning must stop within a few evaluations.");
        Assert.True(result.ResidualRadians > 0d);

        // Deterministic: the identical request resolves to the identical best-effort verdict.
        GazeSaturationResult repeat = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));
        Assert.Equal(result, repeat);
    }

    /// <summary>
    /// A diagonal target beyond the envelope pins to the envelope corner. Coupled geometry shrinks both apparent
    /// offsets at the deep −55° pitch corner (a 25° envelope shortfall reads ≈8.7° face-local yaw, inside the cone),
    /// so the explicit residual is the remaining pitch violation (1.563° measured).
    /// </summary>
    [Fact]
    public void Solve_DiagonalTargetBeyondEnvelope_PinsToEnvelopeCorner()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(100d), Degrees(-70d)) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));

        Assert.False(result.IsFeasible);
        Assert.Equal(Degrees(75d), result.YawRadians, 10);
        Assert.Equal(Degrees(-55d), result.PitchRadians, 10);
        Assert.InRange(ToDegrees(result.ResidualRadians), 1.50d, 1.63d);
        AssertEnvelopeAndRollFree(result, Bounds(75d, 75d, 40d, 55d));
    }

    /// <summary>
    /// Coupled multi-axis violation: the yaw channel overshoots slightly inside its bound during the coupled
    /// correction and stops there (zero excess stops further yaw motion), while the pitch channel converges exactly
    /// onto its bound. Mirrored requests resolve to exactly mirrored commands, and repeated solves are identical.
    /// </summary>
    [Fact]
    public void Solve_EquidistantAxisViolations_ResolveDeterministically()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 left = _eyeOrigin + (reference * DirectionFromAngles(Degrees(25d), Degrees(14d)) * AnchorDistance);
        Vector3 right = _eyeOrigin + (reference * DirectionFromAngles(Degrees(-25d), Degrees(14d)) * AnchorDistance);

        GazeSaturationResult leftResult = GeometricGazeSaturation.Solve(CreateRequest(left, reference));
        GazeSaturationResult rightResult = GeometricGazeSaturation.Solve(CreateRequest(right, reference));

        Assert.True(leftResult.IsFeasible);
        Assert.True(rightResult.IsFeasible);
        // Coupled correction: yaw lands just inside its bound (16.742° measured); pitch lands exactly on its bound.
        Assert.InRange(ToDegrees(leftResult.FaceLocalTargetYawRadians), 16.5d, 17.01d);
        Assert.InRange(ToDegrees(leftResult.FaceLocalTargetPitchRadians), 9.999d, 10.001d);
        Assert.InRange(ToDegrees(rightResult.FaceLocalTargetYawRadians), -17.01d, -16.5d);
        Assert.InRange(ToDegrees(rightResult.FaceLocalTargetPitchRadians), 9.999d, 10.001d);
        Assert.Equal(leftResult.YawRadians, -rightResult.YawRadians, 9);
        Assert.True(Math.Abs(leftResult.YawRadians + rightResult.YawRadians) < 1e-9d, "Mirrored requests must produce mirrored commands.");

        // Identical inputs resolve to bitwise-identical outputs across repeated solves.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(leftResult, GeometricGazeSaturation.Solve(CreateRequest(left, reference)));
            Assert.Equal(rightResult, GeometricGazeSaturation.Solve(CreateRequest(right, reference)));
        }
    }

    /// <summary>Sustained centring aims the face exactly at a reachable anchor, with zero residual.</summary>
    [Fact]
    public void Solve_CentringReachableTarget_AimsExactlyWithZeroResidual()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(30d), Degrees(-20d)) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference, GazeSaturationMode.Centring));

        Assert.True(result.IsFeasible);
        Assert.Equal(0d, result.ResidualRadians, 8);
        Assert.InRange(Math.Abs(ToDegrees(result.FaceLocalTargetYawRadians)), 0d, 0.001d);
        Assert.InRange(Math.Abs(ToDegrees(result.FaceLocalTargetPitchRadians)), 0d, 0.001d);
        Assert.True(result.RotationFromReferenceRadians > 0d);

        // Independent oracle: the returned face forward points along the anchor ray.
        Vector3 aimDirection = (anchor - result.PredictedEyeOrigin).Normalized();
        Assert.True(aimDirection.Dot(-result.FaceBasis.Z) > 1d - 1e-5d, "The face forward must aim at the anchor.");
    }

    /// <summary>Sustained centring on a target beyond the envelope minimises residual at the envelope bound.</summary>
    [Fact]
    public void Solve_CentringBeyondEnvelope_MinimisesResidualAtEnvelopeBound()
    {
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(100d), 0d) * AnchorDistance);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference, GazeSaturationMode.Centring));

        Assert.False(result.IsFeasible);
        Assert.Equal(Degrees(75d), result.YawRadians, 10);
        Assert.InRange(ToDegrees(result.ResidualRadians), 24.999d, 25.001d);
        Assert.True(result.ResidualRadians > 0d, "The unachievable aim must report an explicit residual.");
    }

    /// <summary>
    /// Near-target prediction: for a close anchor, the eye origin is predicted from the requested pose and authored
    /// viewpoint offset. Both solves command the same minimal single step, but the near solve's measured post-step
    /// face-local yaw sits measurably deeper inside the cone than the far-anchor control (16.719° versus 16.995°) —
    /// the parallax signature of the moving predicted origin. A solver using a fixed eye origin would report the
    /// far value for both. The returned pose also verifies against the independently recomposed predicted origin.
    /// </summary>
    [Fact]
    public void Solve_NearTarget_PredictsEyeOriginFromRequestedPoseAndViewpointOffset()
    {
        Basis upstream = RollFreeBasis(Degrees(10d), Degrees(-5d));
        Vector3 pivot = new(0f, 0.15f, 0f);
        Vector3 headOrigin = new(0f, 0.30f, 0f);
        Vector3 viewpointOffset = new(0.02f, 0.005f, -0.03f);
        Vector3 eyeAtReference = pivot
            + (upstream.Transposed() * (headOrigin - pivot))
            + viewpointOffset;

        GazeSaturationRequest CreateParallaxRequest(Vector3 anchor)
        {
            return new(
                upstream,
                headOrigin,
                pivot,
                viewpointOffset,
                anchor,
                Basis.Identity,
                Bounds(17d, 17d, 10d, 15d),
                Bounds(75d, 75d, 40d, 55d),
                GazeSaturationMode.Saturation);
        }

        GazeSaturationResult nearResult = GeometricGazeSaturation.Solve(
            CreateParallaxRequest(eyeAtReference + (DirectionFromAngles(Degrees(25d), Degrees(5d)) * 0.25f)));
        GazeSaturationResult farResult = GeometricGazeSaturation.Solve(
            CreateParallaxRequest(eyeAtReference + (DirectionFromAngles(Degrees(25d), Degrees(5d)) * 15f)));

        Assert.True(nearResult.IsFeasible);
        Assert.True(farResult.IsFeasible);
        Assert.Equal(farResult.YawRadians, nearResult.YawRadians, 6);
        Assert.True(
            Math.Abs(nearResult.FaceLocalTargetYawRadians - farResult.FaceLocalTargetYawRadians) > Degrees(0.2d),
            "The near-anchor measurement must carry the parallax of the moving predicted eye origin.");
        AssertIndependentlyVerified(
            nearResult,
            eyeAtReference + (DirectionFromAngles(Degrees(25d), Degrees(5d)) * 0.25f),
            Bounds(17d, 17d, 10d, 15d),
            upstream,
            headOrigin,
            pivot,
            viewpointOffset);
    }

    /// <summary>Every result orientation is roll-free by construction: its right axis stays perpendicular to the envelope up.</summary>
    [Fact]
    public void Solve_ResultOrientations_AreRollFreeAgainstEnvelopeUp()
    {
        Basis reference = RollFreeBasis(Degrees(20d), Degrees(-30d));
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(40d), Degrees(20d)) * AnchorDistance);

        GazeSaturationResult saturation = GeometricGazeSaturation.Solve(CreateRequest(anchor, reference));
        GazeSaturationResult centring = GeometricGazeSaturation.Solve(
            CreateRequest(anchor, reference, GazeSaturationMode.Centring));

        Assert.True(Math.Abs(saturation.FaceBasis.X.Dot(Vector3.Up)) < 1e-5d, "Saturation results must be roll-free.");
        Assert.True(Math.Abs(centring.FaceBasis.X.Dot(Vector3.Up)) < 1e-5d, "Centring results must be roll-free.");
    }

    /// <summary>Invalid requests fail fast: rolled commands are refused rather than silently stripped, along with degenerate geometry and bad bounds.</summary>
    [Fact]
    public void Solve_InvalidRequests_FailFastWithExplicitExceptions()
    {
        Basis rolled = new(
            new Quaternion(Vector3.Up, (float)Degrees(0.3d)) * new Quaternion(Vector3.Right, (float)Degrees(0.2d))
            * new Quaternion(Vector3.Forward, (float)Degrees(5d)));
        Vector3 anchor = _eyeOrigin + (DirectionFromAngles(Degrees(5d), 0d) * AnchorDistance);

        _ = Assert.Throws<ArgumentException>(
            () => GeometricGazeSaturation.Solve(CreateRequest(anchor, rolled)));

        Basis scaledUpstream = Basis.Identity;
        scaledUpstream.X *= 2f;
        _ = Assert.Throws<ArgumentException>(
            () => GeometricGazeSaturation.Solve(CreateRequest(anchor, Basis.Identity, upstream: scaledUpstream)));

        _ = Assert.Throws<ArgumentException>(
            () => GeometricGazeSaturation.Solve(CreateRequest(_eyeOrigin, Basis.Identity)));

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => GeometricGazeSaturation.Solve(CreateRequest(anchor, Basis.Identity, cone: Bounds(0d, 17d, 10d, 15d))));
        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => GeometricGazeSaturation.Solve(
                CreateRequest(anchor, Basis.Identity, envelope: Bounds(75d, 95d, 40d, 55d))));
    }

    private static GazeSaturationRequest CreateRequest(
        Vector3 anchor,
        Basis reference,
        GazeSaturationMode mode = GazeSaturationMode.Saturation,
        Basis? upstream = null,
        AngularBounds? cone = null,
        AngularBounds? envelope = null,
        DownwardFoldPredictionFrame? foldPrediction = null)
        => new(
            upstream ?? Basis.Identity,
            _eyeOrigin,
            _eyeOrigin,
            Vector3.Zero,
            anchor,
            reference,
            cone ?? Bounds(17d, 17d, 10d, 15d),
            envelope ?? Bounds(75d, 75d, 40d, 55d),
            mode,
            foldPrediction);

    /// <summary>
    /// Coupled fold prediction (production integration): with a fold frame supplied, a deep downward
    /// candidate's predicted eye origin is the frame's resolved fold origin plus the commanded basis
    /// applied to the viewpoint offset — the same construction the controller publishes — and
    /// demonstrably NOT the pivot-orbit prediction. Divergent geometries (orbit prediction with fold
    /// publication, or the reverse) would land lever-arm millimetres away.
    /// </summary>
    [Fact]
    public void Solve_WithFoldFrame_InGateDeepCandidatePredictsFoldEyeOrigin()
    {
        DownwardFoldPredictionFrame frame = BuildFoldFrame();
        Vector3 viewpointOffset = new(0f, 0.03f, 0.1f);
        // Anchor straight below the upstream head at 1 m: the centring target saturates the downward
        // envelope through the coupled fold geometry.
        Vector3 anchor = frame.UpstreamHeadOrigin + new Vector3(0f, -1f, 0f);
        GazeSaturationRequest request = new(
            Basis.Identity,
            frame.UpstreamHeadOrigin,
            frame.PivotPosition,
            viewpointOffset,
            anchor,
            RollFreeBasis(0d, Degrees(-40d)),
            Bounds(17d, 17d, 10d, 15d),
            Bounds(75d, 75d, 40d, 55d),
            GazeSaturationMode.Centring,
            frame);

        GazeSaturationResult result = GeometricGazeSaturation.Solve(request);

        Assert.InRange(ToDegrees(result.YawRadians), -0.001d, 0.001d);
        Assert.InRange(ToDegrees(result.PitchRadians), -55.001d, -54.999d);
        Assert.True(
            frame.TryPredictHeadOrigin(result.YawRadians, result.PitchRadians, out Vector3 frameOrigin, out DownwardFoldSelection selection),
            "The saturated downward candidate must resolve through the frame.");
        Assert.Equal("fold_governed", selection.Reason);

        Basis command = GeometricGazeSaturation.ComposeCommandBasis(result.YawRadians, result.PitchRadians);
        Vector3 expectedEye = frameOrigin + (command * viewpointOffset);
        Assert.True(
            (expectedEye - result.PredictedEyeOrigin).Length() < 1e-4d,
            $"Predicted eye origin {result.PredictedEyeOrigin} must equal the published-pose eye origin " +
            $"{expectedEye} (frame origin + commanded basis · viewpoint offset); delta " +
            $"{(expectedEye - result.PredictedEyeOrigin).Length() * 1000d:F4} mm.");

        // Independent orbit alternative: the superseded pivot-orbit prediction at the same command must
        // differ by lever-arm millimetres, proving the coupled frame drove the search geometry.
        Vector3 lever = frame.UpstreamHeadOrigin - frame.PivotPosition;
        Vector3 orbitHead = frame.PivotPosition
            + (result.FaceBasis * (request.UpstreamFaceBasis.Transposed() * lever));
        Vector3 orbitEye = orbitHead + (result.FaceBasis * viewpointOffset);
        Assert.True(
            (orbitEye - result.PredictedEyeOrigin).Length() > 0.005d,
            "The coupled prediction must use the fold origin, not the orbit (delta " +
            $"{(orbitEye - result.PredictedEyeOrigin).Length() * 1000d:F3} mm).");
    }

    /// <summary>
    /// Non-regression: for commands on the orbit regime (zero-pitch lateral targets), supplying the
    /// fold frame leaves the search equivalent to the frameless request — every candidate's predicted
    /// head origin reproduces the TR 21 orbit (within the frame's double/float composition rounding),
    /// so the converged commands and predictions agree to composition precision.
    /// </summary>
    [Fact]
    public void Solve_WithFoldFrame_OrbitRegimeTargets_MatchFramelessResults()
    {
        DownwardFoldPredictionFrame frame = BuildFoldFrame();
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = frame.UpstreamHeadOrigin
            + (reference * DirectionFromAngles(Degrees(25d), Degrees(3d)) * AnchorDistance);

        GazeSaturationRequest Frameless()
        {
            return new(
            Basis.Identity,
            frame.UpstreamHeadOrigin,
            frame.PivotPosition,
            Vector3.Zero,
            anchor,
            reference,
            Bounds(17d, 17d, 10d, 15d),
            Bounds(75d, 75d, 40d, 55d),
            GazeSaturationMode.Saturation);
        }

        GazeSaturationRequest WithFrame()
        {
            return Frameless() with
            {
                FoldPrediction = frame
            };
        }

        GazeSaturationResult withFrame = GeometricGazeSaturation.Solve(WithFrame());
        GazeSaturationResult withoutFrame = GeometricGazeSaturation.Solve(Frameless());

        Assert.True(withFrame.IsFeasible);
        Assert.Equal(withoutFrame.IsFeasible, withFrame.IsFeasible);
        Assert.True(Math.Abs(withoutFrame.YawRadians - withFrame.YawRadians) < 1e-7d,
            $"yaw {ToDegrees(withoutFrame.YawRadians):F6} vs {ToDegrees(withFrame.YawRadians):F6}");
        Assert.True(Math.Abs(withoutFrame.PitchRadians - withFrame.PitchRadians) < 1e-7d,
            $"pitch {ToDegrees(withoutFrame.PitchRadians):F6} vs {ToDegrees(withFrame.PitchRadians):F6}");
        Assert.True(
            (withoutFrame.PredictedEyeOrigin - withFrame.PredictedEyeOrigin).Length() < 1e-5d,
            $"eye origin delta {(withoutFrame.PredictedEyeOrigin - withFrame.PredictedEyeOrigin).Length() * 1e6d:F2} um");
        Assert.True(
            ToDegrees(withFrame.YawRadians) > 0.5d,
            "The corrected target must be a lateral command for this regime discriminator.");
    }

    /// <summary>
    /// Neutral commands keep the orbit prediction: at the neutral reference the frame resolves the
    /// orbit regime exactly and the solve behaves like the frameless request.
    /// </summary>
    [Fact]
    public void Solve_WithFoldFrame_NeutralReference_KeepsOrbitPrediction()
    {
        DownwardFoldPredictionFrame frame = BuildFoldFrame();
        Basis reference = RollFreeBasis(0d, 0d);
        Vector3 anchor = frame.UpstreamHeadOrigin
            + (reference * DirectionFromAngles(Degrees(8d), Degrees(4d)) * AnchorDistance);

        GazeSaturationRequest Frameless()
        {
            return new(
            Basis.Identity,
            frame.UpstreamHeadOrigin,
            frame.PivotPosition,
            Vector3.Zero,
            anchor,
            reference,
            Bounds(17d, 17d, 10d, 15d),
            Bounds(75d, 75d, 40d, 55d),
            GazeSaturationMode.Saturation);
        }

        GazeSaturationResult withFrame = GeometricGazeSaturation.Solve(Frameless() with
        {
            FoldPrediction = frame
        });
        GazeSaturationResult withoutFrame = GeometricGazeSaturation.Solve(Frameless());

        Assert.True(Math.Abs(withoutFrame.YawRadians - withFrame.YawRadians) < 1e-7d);
        Assert.True(Math.Abs(withoutFrame.PitchRadians - withFrame.PitchRadians) < 1e-7d);
        Assert.True(
            (withoutFrame.PredictedEyeOrigin - withFrame.PredictedEyeOrigin).Length() < 1e-5d);
    }

    /// <summary>
    /// Builds a fold frame on a synthetic reference-proportioned chain under the rig's 180°-yawed
    /// parent (local +Z tilts map to envelope forward), transported so the rest head lands on the
    /// fixture's upstream head origin. The pivot is the alpha-zero sample's Neck joint (N2 repair).
    /// </summary>
    private static DownwardFoldPredictionFrame BuildFoldFrame()
    {
        Transform3D parent = new(
            new Basis(new Vector3(-1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f)),
            new Vector3(0.12f, 0f, -0.34f));
        Transform3D[] chain =
        [
            new Transform3D(Basis.Identity, new Vector3(0f, 0.98f, 0.01f)),
            new Transform3D(Basis.Identity, new Vector3(0f, 0.0652f, 0.0131f)),
            new Transform3D(Basis.Identity, new Vector3(0f, 0.0894f, 0.0151f)),
            new Transform3D(Basis.Identity, new Vector3(0f, 0.2651f, 0.1225f)),
            new Transform3D(Basis.Identity, new Vector3(0f, 0.1022f, 0.0472f)),
        ];
        float[] caps =
        [
            Mathf.DegToRad(8f),
            Mathf.DegToRad(8f),
            Mathf.DegToRad(14f),
            Mathf.DegToRad(16f),
        ];


        float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(parent, chain, caps, out _);
        var gridOrigins = new Vector3[DownwardFoldGeometry.GridSampleCount];
        var jointWorlds = new Transform3D[chain.Length];
        float[] folds = new float[caps.Length];
        Vector3 pivot = Vector3.Zero;
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            float alpha = sample / (float)DownwardFoldGeometry.GridSteps;
            for (int joint = 0; joint < folds.Length; joint++)
            {
                folds[joint] = foldSign * alpha * caps[joint];
            }

            gridOrigins[sample] = DownwardFoldGeometry.ComposePosedFoldedChain(
                parent, chain, folds, jointWorlds).Origin;
            if (sample == 0)
            {
                pivot = jointWorlds[3].Origin;
            }
        }

        // No translation trick: the frame's own geometry (rest head, Neck-joint pivot, spine origin)
        // is exactly what the coupled requests must use — the continuous selection recomposes FK at
        // arbitrary alpha in these coordinates.
        Vector3 spineOrigin = new(0.12f, 0.98f, -0.35f);
        double[] unitDirections = new double[DownwardFoldGeometry.GridSampleCount * 3];
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            double dx = gridOrigins[sample].X - spineOrigin.X;
            double dy = gridOrigins[sample].Y - spineOrigin.Y;
            double dz = gridOrigins[sample].Z - spineOrigin.Z;
            double length = Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
            unitDirections[(sample * 3) + 0] = dx / length;
            unitDirections[(sample * 3) + 1] = dy / length;
            unitDirections[(sample * 3) + 2] = dz / length;
        }

        double[] sweepAngles = new double[DownwardFoldGeometry.GridSampleCount];
        for (int sample = 0; sample < sweepAngles.Length; sample++)
        {
            sweepAngles[sample] = SignedSweepOf(unitDirections, sample);
        }

        return new DownwardFoldPredictionFrame(
            parent,
            chain,
            caps,
            foldSign,
            gridOrigins,
            unitDirections,
            sweepAngles,
            spineOrigin,
            gridOrigins[0],
            pivot,
            Basis.Identity,
            Transform3D.Identity,
            Degrees(15d),
            Degrees(55d),
            alpha0ReconstructionMillimetres: 0d);
    }

    /// <summary>Signed sagittal sweep of a sample against sample 0 about the envelope X axis (fixture curve).</summary>
    private static double SignedSweepOf(double[] units, int sample)
    {
        double bx = units[0];
        double by = units[1];
        double bz = units[2];
        double ux = units[(sample * 3) + 0];
        double uy = units[(sample * 3) + 1];
        double uz = units[(sample * 3) + 2];
        double crossX = (by * uz) - (bz * uy);
        double cosine = (bx * ux) + (by * uy) + (bz * uz);
        // Signed about the envelope −X axis: positive in the co-forward (downward-folding) sense,
        // matching the production sweep parameterisation.
        return Math.Atan2(-crossX, cosine);
    }

    private static AngularBounds Bounds(
        double yawPositiveDegrees,
        double yawNegativeDegrees,
        double pitchPositiveDegrees,
        double pitchNegativeDegrees)
        => new(
            Degrees(yawPositiveDegrees),
            Degrees(yawNegativeDegrees),
            Degrees(pitchPositiveDegrees),
            Degrees(pitchNegativeDegrees));

    /// <summary>Builds D = R_Y(yaw) · R_X(pitch) from explicit Godot quaternion composition — independent of the helper's analytic matrices.</summary>
    private static Basis RollFreeBasis(double yawRadians, double pitchRadians)
        => new(new Quaternion(Vector3.Up, (float)yawRadians) * new Quaternion(Vector3.Right, (float)pitchRadians));

    /// <summary>The unit direction of a yaw/pitch pair under the AI-009 signed forward convention.</summary>
    private static Vector3 DirectionFromAngles(double yawRadians, double pitchRadians)
        => new(
            (float)(-Math.Cos(pitchRadians) * Math.Sin(yawRadians)),
            (float)Math.Sin(pitchRadians),
            (float)(-Math.Cos(pitchRadians) * Math.Cos(yawRadians)));

    /// <summary>Extracts face-local yaw/pitch with the AI-009 signed forward convention from Godot basis math.</summary>
    private static (double YawRadians, double PitchRadians) FaceLocalAngles(Basis face, Vector3 direction)
    {
        Vector3 local = face.Transposed() * direction;
        return (
            Math.Atan2(-local.X, -local.Z),
            Math.Atan2(local.Y, Math.Sqrt((local.X * local.X) + (local.Z * local.Z))));
    }

    /// <summary>
    /// Independent verification oracle: the returned orientation must satisfy the closed cone against the anchor,
    /// measured through Godot basis math and — when parallax inputs are supplied — the predicted eye origin
    /// recomputed from the returned pose.
    /// </summary>
    private static void AssertIndependentlyVerified(
        GazeSaturationResult result,
        Vector3 anchor,
        AngularBounds cone,
        Basis? upstream = null,
        Vector3? headOrigin = null,
        Vector3? pivot = null,
        Vector3? viewpointOffset = null)
    {
        Basis upstreamBasis = upstream ?? Basis.Identity;
        Vector3 pivotPosition = pivot ?? _eyeOrigin;
        Vector3 solvedHeadOrigin = headOrigin ?? _eyeOrigin;
        Vector3 offset = viewpointOffset ?? Vector3.Zero;
        Vector3 predictedEyeOrigin = pivotPosition
            + (result.FaceBasis * (upstreamBasis.Transposed() * (solvedHeadOrigin - pivotPosition)))
            + (result.FaceBasis * offset);
        Assert.True(
            (predictedEyeOrigin - result.PredictedEyeOrigin).Length() < 1e-4f,
            "The reported predicted eye origin must agree with independent recomposition.");

        (double localYaw, double localPitch) = FaceLocalAngles(result.FaceBasis, anchor - predictedEyeOrigin);
        Assert.InRange(ToDegrees(localYaw), -ToDegrees(cone.YawNegativeRadians), ToDegrees(cone.YawPositiveRadians));
        Assert.InRange(ToDegrees(localPitch), -ToDegrees(cone.PitchNegativeRadians), ToDegrees(cone.PitchPositiveRadians));
    }

    private static void AssertEnvelopeAndRollFree(GazeSaturationResult result, AngularBounds envelope)
    {
        Assert.InRange(result.YawRadians, -envelope.YawNegativeRadians, envelope.YawPositiveRadians);
        Assert.InRange(result.PitchRadians, -envelope.PitchNegativeRadians, envelope.PitchPositiveRadians);
        Assert.True(Math.Abs(result.FaceBasis.X.Dot(Vector3.Up)) < 1e-5d, "The right axis must stay perpendicular to the envelope up.");
    }

    /// <summary>
    /// Bisection oracle: finds the envelope yaw at the given pitch whose Godot-composed face-local yaw of the
    /// anchor equals <paramref name="boundYawRadians"/> exactly.
    /// </summary>
    private static double BisectEnvelopeYaw(
        Vector3 anchor,
        double pitchRadians,
        double boundYawRadians,
        double yawHighRadians)
    {
        double low = 0d;
        double high = yawHighRadians;
        for (int step = 0; step < 200; step++)
        {
            double middle = (low + high) / 2d;
            (double localYaw, _) = FaceLocalAngles(RollFreeBasis(middle, pitchRadians), anchor - _eyeOrigin);
            if (localYaw > boundYawRadians)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return (low + high) / 2d;
    }

    private static double Degrees(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
