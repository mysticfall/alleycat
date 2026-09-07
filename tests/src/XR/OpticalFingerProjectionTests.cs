using AlleyCat.Rigging;
using AlleyCat.XR.HandTracking;
using Godot;
using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Unit coverage of the shared calibrated anatomical projection seam (XR-002 TR20-TR29, TR49): synthetic
/// binding states and joint-sample batteries — synthesised with the same cumulative local-flexion pattern as
/// the modifier integration fixtures — verify the predicted destination-local rotations against independent
/// spec-written equations and the per-destination freeze ladder, without any skeleton.
/// </summary>
public sealed class OpticalFingerProjectionTests
{
    private const float RotationToleranceRadians = 1e-4f;

    private static readonly LimbSide[] _sides = [LimbSide.Left, LimbSide.Right];

    /// <summary>
    /// With identity neutrals, +X local hinge axes, and local frames <c>(l=+Y, b=+Z(Back), h=+X)</c>, every
    /// destination given per-joint flexion <c>theta_j</c> about the delivered source hinge <c>+X</c> projects to
    /// <c>rotation(+X, theta_j)</c> — hinge flexion (TR21), roll-free proximal swing (TR22), thumb hinges
    /// (TR27), and the metacarpal correspondence transfer (TR28.7) agree on the synthetic frame.
    /// </summary>
    [Fact]
    public void IdentityNeutralBinding_ProjectsFlexionAboutLocalHingeForAllFifteenDestinations()
    {
        foreach (LimbSide side in _sides)
        {
            OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
            Dictionary<XRHandJoint, Quaternion> flex = CreateFlexRotations();
            XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(side, flex);

            var poses = new OpticalFingerProjectedPose[15];
            Assert.True(binding.TryProject(side, samples, poses));

            foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
            {
                int destinationIndex = DestinationIndex(joint);
                Assert.True(
                    poses[destinationIndex].IsValid,
                    $"{side}/{joint} must project validly with a fully valid synthetic sample set.");
                AssertRotationApproximately(
                    new Quaternion(Vector3.Right, FlexAngle(joint)),
                    poses[destinationIndex].Rotation,
                    $"{side}/{joint}");
            }
        }
    }

    /// <summary>
    /// The projection composes the effective neutral: with <c>N_j = rotation(+Y, 20°)</c> every hinge and swing
    /// destination becomes <c>N_j × rotation(+X, theta_j)</c> (XR-002 TR21-TR22).
    /// </summary>
    [Fact]
    public void NonIdentityNeutrals_ComposeOntoTheProjectedRotation()
    {
        Quaternion neutral = new(Vector3.Up, Mathf.DegToRad(20.0f));
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding(neutral);
        Dictionary<XRHandJoint, Quaternion> flex = CreateFlexRotations();
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(LimbSide.Left, flex);

        var poses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Left, samples, poses));

        foreach (XRHandJoint joint in NonThumbDestinations())
        {
            Quaternion expected = neutral * new Quaternion(Vector3.Right, FlexAngle(joint));
            AssertRotationApproximately(expected, poses[DestinationIndex(joint)].Rotation, $"{LimbSide.Left}/{joint}");
        }
    }

    /// <summary>
    /// A non-identity source neutral <c>S0</c> shifts the delta: <c>Delta = S0⁻¹ × S</c> (XR-002 TR20), so the
    /// projected hinge angle is <c>theta_j − phi</c>.
    /// </summary>
    [Fact]
    public void NonIdentitySourceNeutrals_ShiftTheProjectedHingeAngle()
    {
        float phi = Mathf.DegToRad(15.0f);
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding(sourceNeutral: new Quaternion(Vector3.Right, phi));
        Dictionary<XRHandJoint, Quaternion> flex = CreateFlexRotations();
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(LimbSide.Right, flex);

        var poses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Right, samples, poses));

        foreach (XRHandJoint joint in HingeDestinations())
        {
            Quaternion expected = new(Vector3.Right, FlexAngle(joint) - phi);
            AssertRotationApproximately(expected, poses[DestinationIndex(joint)].Rotation, $"{LimbSide.Right}/{joint}");
        }
    }

    /// <summary>The parent-relative quotient is invariant to any common world rotation of the sample set (XR-002 TR20).</summary>
    [Fact]
    public void CommonWorldRotation_LeavesProjectionUnchanged()
    {
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
        Dictionary<XRHandJoint, Quaternion> flex = CreateFlexRotations();

        Basis worldRotation = Basis.Identity
            .Rotated(Vector3.Up, 0.7f)
            .Rotated(Vector3.Right, -0.4f);
        XRHandJointSourceSample[] plain = SyntheticJointSamples.Create(LimbSide.Left, flex);
        XRHandJointSourceSample[] rotated = SyntheticJointSamples.Create(LimbSide.Left, flex, wristRotation: worldRotation);

        var plainPoses = new OpticalFingerProjectedPose[15];
        var rotatedPoses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Left, plain, plainPoses));
        Assert.True(binding.TryProject(LimbSide.Left, rotated, rotatedPoses));

        for (int index = 0; index < 15; index++)
        {
            Assert.Equal(plainPoses[index].Status, rotatedPoses[index].Status);
            if (plainPoses[index].IsValid)
            {
                AssertRotationApproximately(plainPoses[index].Rotation, rotatedPoses[index].Rotation, $"destination {index}");
            }
        }
    }

    /// <summary>An invalid non-thumb metacarpal freezes only its proximal destination — there is no wrist fallback (XR-002 TR35).</summary>
    [Fact]
    public void InvalidIndexMetacarpal_FreezesOnlyTheIndexProximal()
        => AssertSingleInvalidJointFreezesExactly(
            XRHandJoint.IndexMetacarpal,
            [XRHandJoint.IndexProximal]);

    /// <summary>An invalid proximal freezes its proximal and intermediate destinations (XR-002 TR35).</summary>
    [Fact]
    public void InvalidLittleProximal_FreezesProximalAndIntermediate()
        => AssertSingleInvalidJointFreezesExactly(
            XRHandJoint.LittleProximal,
            [XRHandJoint.LittleProximal, XRHandJoint.LittleIntermediate]);

    /// <summary>An invalid intermediate freezes its intermediate and distal destinations (XR-002 TR35).</summary>
    [Fact]
    public void InvalidMiddleIntermediate_FreezesIntermediateAndDistal()
        => AssertSingleInvalidJointFreezesExactly(
            XRHandJoint.MiddleIntermediate,
            [XRHandJoint.MiddleIntermediate, XRHandJoint.MiddleDistal]);

    /// <summary>An invalid distal freezes its distal destination only (XR-002 TR35).</summary>
    [Fact]
    public void InvalidRingDistal_FreezesDistalOnly()
        => AssertSingleInvalidJointFreezesExactly(
            XRHandJoint.RingDistal,
            [XRHandJoint.RingDistal]);

    /// <summary>
    /// An invalid wrist freezes exactly the thumb metacarpal and thumb proximal; the thumb distal and every
    /// non-thumb chain continue (XR-002 TR28-TR29).
    /// </summary>
    [Fact]
    public void InvalidWrist_FreezesThumbMetacarpalAndProximalOnly()
        => AssertSingleInvalidJointFreezesExactly(
            XRHandJoint.Wrist,
            [XRHandJoint.ThumbMetacarpal, XRHandJoint.ThumbProximal]);

    /// <summary>Whole-hand sample loss freezes all fifteen destinations through the same ladder (XR-002 TR36).</summary>
    [Fact]
    public void WholeHandLoss_FreezesAllFifteenDestinations()
    {
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(
            LimbSide.Left,
            CreateFlexRotations(),
            invalidJoints: [.. Enum.GetValues<XRHandJoint>()]);

        var poses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Left, samples, poses));

        for (int index = 0; index < 15; index++)
        {
            Assert.Equal(OpticalFingerProjectionStatus.FrozenInvalidSource, poses[index].Status);
        }
    }

    /// <summary>An invalid calibration record freezes exactly its destinations; every other destination stays valid (XR-002 TR34).</summary>
    [Fact]
    public void InvalidCalibrationRecord_FreezesExactlyItsDestinations()
    {
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(LimbSide.Right, CreateFlexRotations());

        SyntheticCalibration calibration = CreateValidCalibration();
        calibration.Valid[RecordIndex(LimbSide.Right, XRHandJoint.MiddleIntermediate)] = false;
        binding.StageCalibration(calibration.Records, calibration.Valid, calibration.Metacarpal);

        var poses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Right, samples, poses));

        Assert.Equal(
            OpticalFingerProjectionStatus.FrozenInvalidCalibration,
            poses[DestinationIndex(XRHandJoint.MiddleIntermediate)].Status);
        foreach (XRHandJoint joint in XRHandJoints.DestinationJoints.Where(j => j != XRHandJoint.MiddleIntermediate))
        {
            Assert.True(
                poses[DestinationIndex(joint)].IsValid,
                $"{LimbSide.Right}/{joint} must stay valid when only the middle-intermediate record is invalid.");
        }
    }

    /// <summary>
    /// A degenerate hinge delta — a π rotation about an axis perpendicular to the hinge — fails only the
    /// affected hinge destination, not its chain siblings or the swing destinations (XR-002 TR21).
    /// </summary>
    [Fact]
    public void DegenerateHingeDelta_FreezesOnlyThatHingeDestination()
    {
        OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
        Dictionary<XRHandJoint, Quaternion> flex = CreateFlexRotations();

        // The middle intermediate's relation becomes rotation(+Y, π): w≈0 and v·h_s=0, so m is degenerate.
        flex[XRHandJoint.MiddleIntermediate] = new Quaternion(Vector3.Up, Mathf.Pi);
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(LimbSide.Left, flex);

        var poses = new OpticalFingerProjectedPose[15];
        Assert.True(binding.TryProject(LimbSide.Left, samples, poses));

        Assert.Equal(
            OpticalFingerProjectionStatus.FrozenDegenerateMapping,
            poses[DestinationIndex(XRHandJoint.MiddleIntermediate)].Status);
        foreach (XRHandJoint joint in XRHandJoints.DestinationJoints.Where(j => j != XRHandJoint.MiddleIntermediate))
        {
            Assert.True(poses[DestinationIndex(joint)].IsValid, $"{LimbSide.Left}/{joint} must stay live.");
        }
    }

    /// <summary>An unstaged binding or calibration fails closed without producing projections (XR-002 TR34).</summary>
    [Fact]
    public void UnstagedBindingOrCalibration_FailsClosed()
    {
        XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(LimbSide.Left, CreateFlexRotations());
        var poses = new OpticalFingerProjectedPose[15];

        OpticalFingerProjectionBinding unstaged = new();
        Assert.False(unstaged.TryProject(LimbSide.Left, samples, poses));

        // Binding staged but calibration missing: still fail closed.
        OpticalFingerProjectionBinding boundWithoutCalibration = CreateBinding(Repeat(Quaternion.Identity, RecordCount));
        Assert.False(boundWithoutCalibration.TryProject(LimbSide.Left, samples, poses));

        // Once the calibration stages, the projection runs.
        SyntheticCalibration calibration = CreateValidCalibration();
        boundWithoutCalibration.StageCalibration(calibration.Records, calibration.Valid, calibration.Metacarpal);
        Assert.True(boundWithoutCalibration.TryProject(LimbSide.Left, samples, poses));
    }

    /// <summary>Side-neutral copies expose the 15 effective neutrals the gesture-profile derivation consumes.</summary>
    [Fact]
    public void TryCopySideEffectiveNeutrals_ReturnsTheSideSliceInCanonicalOrder()
    {
        Quaternion rightNeutral = new(Vector3.Up, 0.3f);
        var bilateralNeutrals = new Quaternion[RecordCount];
        for (int index = 0; index < RecordCount; index++)
        {
            bilateralNeutrals[index] = index < 15 ? Quaternion.Identity : rightNeutral;
        }

        OpticalFingerProjectionBinding binding = CreateBinding(bilateralNeutrals);
        StageValidCalibration(binding);

        var neutrals = new Quaternion[15];
        Assert.True(binding.TryCopySideEffectiveNeutrals(LimbSide.Left, neutrals));
        Assert.All(neutrals, neutral => Assert.Equal(Quaternion.Identity, neutral));

        Assert.True(binding.TryCopySideEffectiveNeutrals(LimbSide.Right, neutrals));
        Assert.All(neutrals, neutral => Assert.Equal(rightNeutral, neutral));

        Assert.False(new OpticalFingerProjectionBinding().TryCopySideEffectiveNeutrals(LimbSide.Left, neutrals));
    }

    private static void AssertSingleInvalidJointFreezesExactly(XRHandJoint invalidJoint, XRHandJoint[] expectedFrozen)
    {
        foreach (LimbSide side in _sides)
        {
            OpticalFingerProjectionBinding binding = CreateSyntheticBinding();
            XRHandJointSourceSample[] samples = SyntheticJointSamples.Create(
                side,
                CreateFlexRotations(),
                invalidJoints: [invalidJoint]);

            var poses = new OpticalFingerProjectedPose[15];
            Assert.True(binding.TryProject(side, samples, poses));

            foreach (XRHandJoint joint in XRHandJoints.DestinationJoints)
            {
                bool expectFrozen = expectedFrozen.Contains(joint);
                OpticalFingerProjectionStatus expectedStatus = expectFrozen
                    ? OpticalFingerProjectionStatus.FrozenInvalidSource
                    : OpticalFingerProjectionStatus.Valid;
                Assert.True(
                    poses[DestinationIndex(joint)].Status == expectedStatus,
                    $"{side}/{joint} freeze state for invalid {invalidJoint}: expected {expectedStatus}, got " +
                    $"{poses[DestinationIndex(joint)].Status}.");
            }
        }
    }

    private static IEnumerable<XRHandJoint> NonThumbDestinations()
        => XRHandJoints.DestinationJoints.Where(joint => joint is not XRHandJoint.ThumbMetacarpal);

    private static IEnumerable<XRHandJoint> HingeDestinations()
        => XRHandJoints.DestinationJoints.Where(joint => joint is not XRHandJoint.ThumbMetacarpal)
            .Where(joint => joint is not (XRHandJoint.IndexProximal or XRHandJoint.MiddleProximal
                or XRHandJoint.RingProximal or XRHandJoint.LittleProximal));

    private static Dictionary<XRHandJoint, Quaternion> CreateFlexRotations()
        => XRHandJoints.TrackedJoints.Where(joint => joint != XRHandJoint.Wrist)
            .ToDictionary(joint => joint, joint => new Quaternion(Vector3.Right, FlexAngle(joint)));

    /// <summary>A distinct positive flexion per joint so cross-destination wiring errors cannot cancel out.</summary>
    private static float FlexAngle(XRHandJoint joint)
        => Mathf.DegToRad(8.0f + (3.0f * ((int)joint % 7)));

    private static int DestinationIndex(XRHandJoint joint)
        => Array.IndexOf(XRHandJoints.DestinationJoints, joint);

    private static int RecordIndex(LimbSide side, XRHandJoint joint)
        => OpticalFingerTrackingCalibrationProfile.TryGetRecordIndex(side, joint, out int index)
            ? index
            : throw new InvalidOperationException($"{side}/{joint} has no calibration record index.");

    private static int RecordCount => OpticalFingerTrackingCalibrationProfile.RecordCount;

    private static T[] Repeat<T>(T value, int count)
    {
        var values = new T[count];
        Array.Fill(values, value);
        return values;
    }

    internal readonly record struct SyntheticCalibration(
        ResolvedOpticalFingerCalibration[] Records,
        bool[] Valid,
        ResolvedThumbMetacarpalCalibration[] Metacarpal);

    private static SyntheticCalibration CreateValidCalibration(Quaternion? sourceNeutral = null)
    {
        Quaternion s0 = sourceNeutral ?? Quaternion.Identity;
        return new SyntheticCalibration(
            Repeat(
                new ResolvedOpticalFingerCalibration(s0, Quaternion.Identity, Quaternion.Identity),
                RecordCount),
            Repeat(true, RecordCount),
            Repeat(new ResolvedThumbMetacarpalCalibration(
                Quaternion.Identity,
                1.0f,
                OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateStart,
                OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalHingeGateEnd,
                OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateStart,
                OpticalFingerTrackingCalibrationProfile.DefaultMetacarpalBendGateEnd), 2));
    }

    private static OpticalFingerProjectionBinding CreateBinding(Quaternion[] bilateralNeutrals)
    {
        var binding = new OpticalFingerProjectionBinding();
        Assert.True(
            binding.TryStageBinding(
                bilateralNeutrals,
                Repeat(Vector3.Right, RecordCount),
                Repeat(
                    new FingerAnatomicalFrame(Vector3.Up, Vector3.Back, Vector3.Right),
                    RecordCount),
                Repeat(new AuthoredThumbCorrespondenceFrame(Vector3.Up, Vector3.Right, Vector3.Back), 2),
                out string bindingError),
            bindingError);
        return binding;
    }

    private static void StageValidCalibration(OpticalFingerProjectionBinding binding, Quaternion? sourceNeutral = null)
    {
        SyntheticCalibration calibration = CreateValidCalibration(sourceNeutral);
        binding.StageCalibration(calibration.Records, calibration.Valid, calibration.Metacarpal);
    }

    internal static OpticalFingerProjectionBinding CreateSyntheticBinding(
        Quaternion? neutral = null,
        Quaternion? sourceNeutral = null)
    {
        OpticalFingerProjectionBinding binding = CreateBinding(Repeat(neutral ?? Quaternion.Identity, RecordCount));
        StageValidCalibration(binding, sourceNeutral);
        return binding;
    }

    private static void AssertRotationApproximately(Quaternion expected, Quaternion actual, string context)
    {
        float angle = expected.Normalized().AngleTo(actual.Normalized());
        Assert.True(
            angle <= RotationToleranceRadians,
            $"Rotation mismatch for {context}: expected {expected} ({expected.GetEuler()}), actual {actual} " +
            $"({actual.GetEuler()}), angle {angle}.");
    }
}

/// <summary>Shared synthesis of raw per-side joint-sample sets mirroring the modifier fixtures' stimulus pattern.</summary>
internal static class SyntheticJointSamples
{
    /// <summary>
    /// Builds a per-side sample set from cumulative local rotations: every joint's world basis is
    /// <c>wrist × cumulativeLocal</c> so the parent-relative quotient reproduces each injected local exactly.
    /// Acceptance mirrors the production provider gate: a joint is accepted when its own flags are usable AND
    /// its direct raw parent's own flags are usable — one level, own-flags based, never recursive.
    /// </summary>
    public static XRHandJointSourceSample[] Create(
        LimbSide side,
        IReadOnlyDictionary<XRHandJoint, Quaternion> localRotations,
        HashSet<XRHandJoint>? invalidJoints = null,
        Basis? wristRotation = null)
    {
        var samples = new XRHandJointSourceSample[20];
        Dictionary<XRHandJoint, Basis> cumulative = [];
        Basis wrist = wristRotation ?? Basis.Identity;

        foreach (XRHandJoint joint in XRHandJoints.TrackedJoints)
        {
            XRHandJoint? parent = XRHandJoints.GetRequiredSourceParent(joint);
            Basis localBasis = joint == XRHandJoint.Wrist
                ? wrist
                : new Basis(localRotations.GetValueOrDefault(joint, Quaternion.Identity));
            cumulative[joint] = parent is null ? wrist : cumulative[parent.Value] * localBasis;

            bool ownUsable = invalidJoints?.Contains(joint) != true;
            bool parentUsable = parent is null || invalidJoints?.Contains(parent.Value) != true;
            samples[(int)joint] = MakeSample(side, joint, cumulative[joint], ownUsable && parentUsable);
        }

        return samples;
    }

    private static XRHandJointSourceSample MakeSample(LimbSide side, XRHandJoint joint, Basis basis, bool accepted)
        => new(
            side,
            joint,
            HasTracker: true,
            HasTrackingData: true,
            RawFlags: 0,
            OrientationValid: accepted,
            OrientationTracked: accepted,
            PositionValid: accepted,
            PositionTracked: accepted,
            new Transform3D(basis, new Vector3(0.01f * (int)joint, 1.0f, -0.2f)),
            new Transform3D(basis, new Vector3(0.01f * (int)joint, 1.0f, -0.2f)),
            TrackerLocalTransformFinite: true,
            ProductionWorldTransformFinite: true,
            ProductionAccepted: accepted,
            accepted ? XRHandJointSourceRejection.None : XRHandJointSourceRejection.JointOrientationUnusable);
}
