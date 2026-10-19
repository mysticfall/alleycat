using AlleyCat.Mind.Attention;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.Attention;

/// <summary>
/// Continuous selection and animated re-anchoring coverage for the shared fold-family prediction
/// frame: exact TR 21 reproduction on the orbit regime (including animated upstream faces), yaw
/// composition on the downward regime, the continuous strength-12 demand deepening, continuity of the
/// resolved origin in pitch and in yaw at deep pitch (the former |yaw| gate), animation-owned pose
/// re-anchoring, and finite degenerate failure. The grid FK comes from the independently tested
/// <see cref="DownwardFoldGeometry"/> compositions; the N2 fixture repair retains the alpha-zero
/// pivot (the pivot is read from the alpha-zero sample, never a full-fold leftover).
/// </summary>
public sealed class DownwardFoldPredictionFrameTests
{
    // Synthetic chain root→head mirroring the reference female's proportions (DownwardFoldGeometryTests
    // precedent): identity local rest rotations with upward translations tilted forward (local +Z).
    private static readonly Transform3D[] _restChain =
    [
        new Transform3D(Basis.Identity, new Vector3(0f, 0.98f, 0.01f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.0652f, 0.0131f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.0894f, 0.0151f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.2651f, 0.1225f)),
        new Transform3D(Basis.Identity, new Vector3(0f, 0.1022f, 0.0472f)),
    ];

    private static readonly float[] _caps =
    [
        Mathf.DegToRad(8f),
        Mathf.DegToRad(8f),
        Mathf.DegToRad(14f),
        Mathf.DegToRad(16f),
    ];

    /// <summary>
    /// The reference rig's 180°-yawed container frame: local +Z maps to envelope forward (−Z). The
    /// rig's own 180° skeleton yaw cancels against the authored viewpoint offset, so the neutral
    /// upstream face basis in the envelope frame is identity.
    /// </summary>
    private static readonly Transform3D _parent = new(
        new Basis(new Vector3(-1f, 0f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, -1f)),
        new Vector3(0.12f, 0f, -0.34f));

    /// <summary>
    /// Builds the frame over the given local poses (rest by default, an animated pose for the
    /// re-anchoring tests). The pivot is the alpha-zero sample's Neck joint — never a full-fold
    /// leftover (N2 fixture repair).
    /// </summary>
    private static DownwardFoldPredictionFrame BuildFrame(Transform3D[]? localPoses = null, Basis? upstreamFace = null)
    {
        Transform3D[] poses = localPoses ?? _restChain;
        float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(_parent, poses, _caps, out _);
        var gridOrigins = new Vector3[DownwardFoldGeometry.GridSampleCount];
        var jointWorlds = new Transform3D[poses.Length];
        float[] folds = new float[_caps.Length];
        Vector3 pivot = Vector3.Zero;
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            float alpha = sample / (float)DownwardFoldGeometry.GridSteps;
            for (int joint = 0; joint < folds.Length; joint++)
            {
                folds[joint] = foldSign * alpha * _caps[joint];
            }

            gridOrigins[sample] = DownwardFoldGeometry.ComposePosedFoldedChain(
                _parent, poses, folds, jointWorlds).Origin;
            if (sample == 0)
            {
                pivot = jointWorlds[3].Origin;
            }
        }

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
            sweepAngles[sample] = SignedSweep(unitDirections, 0,
                unitDirections[(sample * 3) + 0],
                unitDirections[(sample * 3) + 1],
                unitDirections[(sample * 3) + 2]);
        }

        for (int sample = 1; sample < sweepAngles.Length; sample++)
        {
            Assert.True(sweepAngles[sample] > sweepAngles[sample - 1],
                $"The synthetic family sweep must stay monotone (sample {sample}).");
        }

        return new DownwardFoldPredictionFrame(
            _parent,
            poses,
            _caps,
            foldSign,
            gridOrigins,
            unitDirections,
            sweepAngles,
            spineOrigin,
            gridOrigins[0],
            pivot,
            upstreamFace ?? Basis.Identity,
            Transform3D.Identity,
            Degrees(15d),
            Degrees(55d),
            alpha0ReconstructionMillimetres: 0d);
    }

    /// <summary>Animated stand-in: the Chest joint's local pose pitched 5° and the Neck joint yawed 3°.</summary>
    private static Transform3D[] AnimatedPoses()
    {
        var poses = new Transform3D[_restChain.Length];
        for (int i = 0; i < _restChain.Length; i++)
        {
            poses[i] = _restChain[i];
        }
        poses[1] = poses[1] with
        {
            Basis = poses[1].Basis * new Basis(Vector3.Right, Mathf.DegToRad(5f))
        };
        poses[3] = poses[3] with
        {
            Basis = poses[3].Basis * new Basis(Vector3.Up, Mathf.DegToRad(3f))
        };
        return poses;
    }

    /// <summary>Independent TR 21 oracle: <c>p + Ry(yaw)·Rx(pitch)·Uᵀ·(h − p)</c> in Godot quaternion math.</summary>
    private static Vector3 Tr21Orbit(double yawRadians, double pitchRadians, Basis upstream, Vector3 head, Vector3 pivot)
        => pivot
            + (new Basis(new Quaternion(Vector3.Up, (float)yawRadians) * new Quaternion(Vector3.Right, (float)pitchRadians))
            * (upstream.Inverse() * (head - pivot)));

    /// <summary>Independent yaw composition about the pivot: <c>p + Ry(yaw)·(point − p)</c>.</summary>
    private static Vector3 YawAboutPivot(double yawRadians, Vector3 point, Vector3 pivot)
        => pivot + (new Basis(new Quaternion(Vector3.Up, (float)yawRadians)) * (point - pivot));

    /// <inheritdoc/>
    [Fact]
    public void OrbitRegime_ReproducesTR21Exactly_ForNeutralUpwardAndZeroPitchYaw()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();
        Vector3 head = frame.UpstreamHeadOrigin;
        Vector3 pivot = frame.PivotPosition;

        foreach ((double yaw, double pitch) in new[]
                 {
                     (0d, 0d), (0d, Degrees(10d)), (Degrees(30d), 0d), (Degrees(-35d), Degrees(25d)),
                     (Degrees(75d), Degrees(0.5d))
                 })
        {
            Assert.True(
                frame.TryPredictHeadOrigin(yaw, pitch, out Vector3 resolved, out DownwardFoldSelection selection),
                $"The orbit-regime resolution must apply at (yaw {ToDegrees(yaw):F1}°, pitch {ToDegrees(pitch):F1}°).");
            Assert.Equal("orbit_path", selection.Reason);
            Assert.Equal(0d, selection.Alpha, 9);
            Vector3 expected = Tr21Orbit(yaw, pitch, Basis.Identity, head, pivot);
            Assert.True(
                (resolved - expected).Length() < 1e-4f,
                $"Orbit regime at ({ToDegrees(yaw):F1}°, {ToDegrees(pitch):F1}°): resolved {resolved} vs TR 21 " +
                $"{expected}, delta {(resolved - expected).Length() * 1000d:F4} mm.");
        }
    }

    /// <inheritdoc/>
    [Fact]
    public void OrbitRegime_ReproducesTR21Exactly_ForAnimatedUpstreamFace()
    {
        var animatedUpstream = new Basis(
            new Quaternion(Vector3.Up, (float)Degrees(7d)) * new Quaternion(Vector3.Right, (float)-Degrees(4d)));
        DownwardFoldPredictionFrame frame = BuildFrame(upstreamFace: animatedUpstream);
        Vector3 head = frame.UpstreamHeadOrigin;
        Vector3 pivot = frame.PivotPosition;

        foreach ((double yaw, double pitch) in new[] { (0d, 0d), (Degrees(20d), Degrees(5d)), (Degrees(-40d), 0d) })
        {
            Assert.True(frame.TryPredictHeadOrigin(yaw, pitch, out Vector3 resolved, out _));
            Vector3 expected = Tr21Orbit(yaw, pitch, animatedUpstream, head, pivot);
            Assert.True(
                (resolved - expected).Length() < 1e-4f,
                $"Animated orbit regime at ({ToDegrees(yaw):F1}°, {ToDegrees(pitch):F1}°): resolved {resolved} vs " +
                $"{expected}, delta {(resolved - expected).Length() * 1000d:F4} mm — the U-mixed lever rotation must be exact.");
        }
    }

    /// <summary>
    /// The downward regime composes the fold family with the command yaw about the pivot — the corrected
    /// attainable geometry at ANY yaw, verified against the independent yaw-composed family FK.
    /// </summary>
    [Fact]
    public void DownwardRegime_ComposesYawWithFoldFamily_AtEveryYaw()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();
        Vector3 pivot = frame.PivotPosition;

        foreach (double yaw in new[] { 0d, Degrees(30d), Degrees(-30d) })
        {
            Assert.True(
                frame.TryPredictHeadOrigin(yaw, Degrees(-55d), out Vector3 resolved, out DownwardFoldSelection selection),
                "The deep downward resolution must apply.");
            Assert.Equal("fold_governed", selection.Reason);
            Assert.True(selection.Alpha > 0.5d, "The deep demand must select a deep family member.");
            Assert.True(selection.Alpha <= 1d, "The selection must stay inside the certified family.");

            float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(_parent, _restChain, _caps, out _);
            float[] folds = new float[_caps.Length];
            for (int joint = 0; joint < folds.Length; joint++)
            {
                folds[joint] = foldSign * (float)selection.Alpha * _caps[joint];
            }

            var worlds = new Transform3D[_restChain.Length];
            Vector3 foldPoint = DownwardFoldGeometry.ComposePosedFoldedChain(_parent, _restChain, folds, worlds).Origin;
            Vector3 expected = YawAboutPivot(yaw, foldPoint, pivot);
            Assert.True(
                (resolved - expected).Length() < 1e-3f,
                $"Downward regime at yaw {ToDegrees(yaw):F1}°: resolved {resolved} vs yaw-composed fold " +
                $"{expected}, delta {(resolved - expected).Length() * 1000d:F4} mm.");
        }
    }

    /// <summary>
    /// The predeclared user-selected strength-14 deepening, continuous form: no deepening within the
    /// eye-comfort band, the proportional continuous fraction between the comfort and envelope bounds,
    /// and the full 14-steps' worth at the envelope boundary and beyond. Every selection stays the
    /// direction-matched fraction plus the deepening, clamped to the family.
    /// </summary>
    [Fact]
    public void DemandDeepening_FollowsContinuousStrength14()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();

        _ = frame.TryPredictHeadOrigin(0d, Degrees(-15d), out _, out DownwardFoldSelection atComfort);
        Assert.Equal(0d, atComfort.DemandExtraAlpha, 9);
        Assert.Equal(atComfort.DirectionOptimalAlpha, atComfort.Alpha, 9);

        // (20−15)/(55−15) = 0.125 → 14/32 · 0.125 = 0.0546875.
        _ = frame.TryPredictHeadOrigin(0d, Degrees(-20d), out _, out DownwardFoldSelection atMinus20);
        Assert.Equal(14d / 32d * 0.125d, atMinus20.DemandExtraAlpha, 9);
        Assert.Equal(atMinus20.DirectionOptimalAlpha + atMinus20.DemandExtraAlpha, atMinus20.Alpha, 9);

        // (35−15)/(55−15) = 0.5 → 7 steps' worth.
        _ = frame.TryPredictHeadOrigin(0d, Degrees(-35d), out _, out DownwardFoldSelection atMinus35);
        Assert.Equal(14d / 32d * 0.5d, atMinus35.DemandExtraAlpha, 9);

        // At the envelope boundary and beyond: the full user-selected strength. The selected alpha
        // is capped at one, but wider authored caps need not saturate for this particular command.
        _ = frame.TryPredictHeadOrigin(0d, Degrees(-55d), out _, out DownwardFoldSelection atEnvelope);
        Assert.Equal(14d / 32d, atEnvelope.DemandExtraAlpha, 9);
        _ = frame.TryPredictHeadOrigin(0d, Degrees(-85d), out _, out DownwardFoldSelection extreme);
        Assert.Equal(14d / 32d, extreme.DemandExtraAlpha, 9);
        Assert.Equal(Math.Min(1d, extreme.DirectionOptimalAlpha + (14d / 32d)), extreme.Alpha, 9);
    }

    /// <summary>
    /// Continuity in pitch through the engagement band: sweeping a fine pitch ladder from upward past
    /// the engagement boundary into the deep domain never steps the resolved origin — the grid's
    /// discrete samples are interpolation knots, not published poses.
    /// </summary>
    [Fact]
    public void ResolvedOrigin_IsContinuousInPitch()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();
        Vector3 previous = default;
        bool hasPrevious = false;
        for (double pitchDegrees = 10d; pitchDegrees >= -75d; pitchDegrees -= 0.25d)
        {
            Assert.True(frame.TryPredictHeadOrigin(0d, Degrees(pitchDegrees), out Vector3 resolved, out _));
            if (hasPrevious)
            {
                double step = (resolved - previous).Length() * 1000d;
                Assert.True(
                    step <= 3d,
                    $"Pitch sweep step {step:F3} mm at {pitchDegrees:F2}° exceeds the 3 mm continuity bound " +
                    "(grid samples are interpolation knots, never published snaps).");
            }

            previous = resolved;
            hasPrevious = true;
        }
    }

    /// <summary>
    /// PGI-B2 continuity: sweeping yaw across the former |yaw| = 0.5° gate at deep pitch — BOTH signs —
    /// moves the resolved origin only with the sweep (the yaw-composed fold rotates about the pivot),
    /// never by a construction branch jump.
    /// </summary>
    [Fact]
    public void ResolvedOrigin_IsContinuousInYawAcrossFormerGate_AtDeepPitch()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();
        foreach (double pitch in new[] { Degrees(-39.64d), Degrees(-55d) })
        {
            Vector3 previous = default;
            bool hasPrevious = false;
            double maxStep = 0;
            for (double yawDegrees = -6d; yawDegrees <= 6d; yawDegrees += 0.1d)
            {
                Assert.True(
                    frame.TryPredictHeadOrigin(Degrees(yawDegrees), pitch, out Vector3 resolved, out _));
                if (hasPrevious)
                {
                    maxStep = Math.Max(maxStep, (resolved - previous).Length() * 1000d);
                }

                previous = resolved;
                hasPrevious = true;
            }

            Assert.True(
                maxStep <= 1.5d,
                $"Yaw sweep at pitch {ToDegrees(pitch):F2}°: max step {maxStep:F3} mm exceeds the 1.5 mm " +
                "continuity bound (the former gate jumped 55.48/89.20 mm by independent arithmetic).");
        }
    }

    /// <summary>
    /// Animated re-anchoring: the family composes over the supplied animation-owned poses — alpha = 0
    /// reconstructs the posed upstream head exactly, and the resolved fold point at any alpha is the
    /// posed-base FK (verified independently), never a rest-based composition.
    /// </summary>
    [Fact]
    public void AnimatedUpstream_FamilyReanchorsOntoPosedBase()
    {
        Transform3D[] poses = AnimatedPoses();
        DownwardFoldPredictionFrame frame = BuildFrame(localPoses: poses);

        // alpha = 0 is the plain posed composition.
        Assert.True(frame.TryPredictHeadOrigin(0d, 0d, out Vector3 resolved, out _));
        var worlds = new Transform3D[poses.Length];
        Vector3 posedHead = DownwardFoldGeometry.ComposePosedFoldedChain(
            _parent, poses, new float[_caps.Length], worlds).Origin;
        Assert.True(
            (frame.GridHeadOrigins[0] - posedHead).Length() < 1e-6f,
            "alpha = 0 over the posed base must reconstruct the posed upstream head exactly.");

        // A deep fold resolution uses the posed family, not the rest family.
        Assert.True(frame.TryPredictHeadOrigin(0d, Degrees(-55d), out _, out DownwardFoldSelection selection));
        float foldSign = DownwardFoldGeometry.ResolveForwardFoldSign(_parent, poses, _caps, out _);
        float[] folds = new float[_caps.Length];
        for (int joint = 0; joint < folds.Length; joint++)
        {
            folds[joint] = foldSign * (float)selection.Alpha * _caps[joint];
        }

        Vector3 posedFold = DownwardFoldGeometry.ComposePosedFoldedChain(_parent, poses, folds, worlds).Origin;
        Vector3 restFold = DownwardFoldGeometry.ComposePosedFoldedChain(_parent, _restChain, folds, worlds).Origin;
        Assert.True(
            (selection.FoldPointEnvelope - posedFold).Length() < 1e-4f,
            "The fold point must come from the posed family.");
        Assert.True(
            (selection.FoldPointEnvelope - restFold).Length() > 1e-3f,
            "The posed family must genuinely differ from the rest family (non-vacuous re-anchoring).");
        _ = resolved;
    }

    /// <summary>
    /// Finite failure: a degenerate reference direction (pivot and head coincident at the spine origin)
    /// reports the degenerate reason instead of producing a direction from zero vectors.
    /// </summary>
    [Fact]
    public void DegenerateReference_ReportsDegenerateWithoutThrowing()
    {
        DownwardFoldPredictionFrame frame = BuildFrame();
        Assert.True(frame.TryPredictHeadOrigin(0d, Degrees(-10d), out _, out DownwardFoldSelection healthy));
        Assert.NotEqual("degenerate_orbit_direction", healthy.Reason);

        // A frame whose upstream head sits exactly on the pivot at the spine origin has a degenerate
        // reference direction for every command.
        Transform3D[] flatPoses =
        [
            new Transform3D(Basis.Identity, Vector3.Zero),
            new Transform3D(Basis.Identity, Vector3.Zero),
            new Transform3D(Basis.Identity, Vector3.Zero),
            new Transform3D(Basis.Identity, Vector3.Zero),
            new Transform3D(Basis.Identity, Vector3.Zero),
        ];
        DownwardFoldPredictionFrame flat = BuildFlatFrame(flatPoses);
        Assert.False(flat.TryPredictHeadOrigin(0d, Degrees(-10d), out _, out DownwardFoldSelection flatSelection));
        Assert.Equal("degenerate_orbit_direction", flatSelection.Reason);
        Assert.False(flatSelection.Applied);
    }

    private static DownwardFoldPredictionFrame BuildFlatFrame(Transform3D[] poses)
    {
        float foldSign = 1f;
        var gridOrigins = new Vector3[DownwardFoldGeometry.GridSampleCount];
        double[] unitDirections = new double[DownwardFoldGeometry.GridSampleCount * 3];
        double[] sweepAngles = new double[DownwardFoldGeometry.GridSampleCount];
        for (int sample = 0; sample < gridOrigins.Length; sample++)
        {
            gridOrigins[sample] = Vector3.Zero;
            unitDirections[(sample * 3) + 1] = 1d;
            sweepAngles[sample] = sample * 0.01d;
        }

        return new DownwardFoldPredictionFrame(
            Transform3D.Identity,
            poses,
            _caps,
            foldSign,
            gridOrigins,
            unitDirections,
            sweepAngles,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Basis.Identity,
            Transform3D.Identity,
            Degrees(15d),
            Degrees(55d),
            0d);
    }

    /// <summary>
    /// Signed sagittal sweep angle against the base sample about the envelope X axis (test-side
    /// fixture parameterisation mirroring the frame's tabulated curve).
    /// </summary>
    private static double SignedSweep(
        double[] units, int baseSample, double ux, double uy, double uz)
    {
        double bx = units[(baseSample * 3) + 0];
        double by = units[(baseSample * 3) + 1];
        double bz = units[(baseSample * 3) + 2];
        double crossX = (by * uz) - (bz * uy);
        double cosine = (bx * ux) + (by * uy) + (bz * uz);
        // Signed about the envelope −X axis: positive in the co-forward (downward-folding) sense,
        // matching the production sweep parameterisation.
        return Math.Atan2(-crossX, cosine);
    }

    private static double Degrees(double degrees) => degrees * Math.PI / 180d;

    private static double ToDegrees(double radians) => radians * 180d / Math.PI;
}
