using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rigging;

/// <summary>
/// Bilaterally distributes final hand axial motion onto the authored forearm twist helper bones.
/// </summary>
/// <remarks>
/// The helper topology is <c>LowerArm -> ForearmTwist -> Hand</c>. From the same per-side
/// authority sample the writer extracts only the signed principal axial twist of the hand-pose
/// delta about the lower-arm rest longitudinal axis, writes the weighted axial twist to the
/// twist helper as a complete local transform, and re-asserts the hand's authoritative global
/// pose in the same pass, so the re-parented chain never corrupts the authoritative hand pose.
/// The swing component of the hand rotation is never driven anywhere. An unready, stale, or
/// invalid side preflights and rests its single helper with the compensating hand write.
/// </remarks>
[Tool]
[GlobalClass]
public partial class ForearmTwistModifier : SkeletonModifier3D
{
    private const int SideCount = 2;

    /// <summary>The provisional axial twist distribution weight.</summary>
    public const float DefaultTwistWeight = 0.50f;

    private readonly SideBinding?[] _bindings = new SideBinding?[SideCount];
    private readonly ForearmTwistBindingStatus[] _bindingStatuses = new ForearmTwistBindingStatus[SideCount];
    private readonly bool[] _bindingWarningsIssued = new bool[SideCount];
    private readonly ForearmTwistAuthoritySample[] _authoritySamples = new ForearmTwistAuthoritySample[SideCount];

    private Skeleton3D? _boundSkeleton;

    /// <summary>Gets or sets the left lower-arm bone name.</summary>
    [Export]
    public string LeftLowerArmBoneName { get; set; } = "LeftLowerArm";

    /// <summary>Gets or sets the right lower-arm bone name.</summary>
    [Export]
    public string RightLowerArmBoneName { get; set; } = "RightLowerArm";

    /// <summary>Gets or sets the left hand bone name.</summary>
    [Export]
    public string LeftHandBoneName { get; set; } = "LeftHand";

    /// <summary>Gets or sets the right hand bone name.</summary>
    [Export]
    public string RightHandBoneName { get; set; } = "RightHand";

    /// <summary>Gets or sets the left forearm twist helper bone name.</summary>
    [Export]
    public string LeftForearmTwistBoneName { get; set; } = "LeftForearmTwist";

    /// <summary>Gets or sets the right forearm twist helper bone name.</summary>
    [Export]
    public string RightForearmTwistBoneName { get; set; } = "RightForearmTwist";

    /// <summary>
    /// Gets or sets the twist distribution weight, clamped inclusively from 0 to 1.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float TwistWeight
    {
        get;
        set => field = ClampTwistWeight(value);
    } = DefaultTwistWeight;

    /// <summary>Clamps a twist distribution weight inclusively from 0 to 1.</summary>
    public static float ClampTwistWeight(float weight) => Mathf.Clamp(weight, 0.0f, 1.0f);

    /// <summary>Gets the last binding status for one side.</summary>
    public ForearmTwistBindingStatus GetBindingStatus(LimbSide side)
        => IsSupportedSide(side) ? _bindingStatuses[(int)side] : ForearmTwistBindingStatus.UnsupportedSide;

    /// <summary>Gets whether a side is bound and eligible to write its helper bone.</summary>
    public bool IsSideValid(LimbSide side) => GetBindingStatus(side) == ForearmTwistBindingStatus.Valid;

    /// <summary>Gets the latest authority sample submitted for a side, including its adapter-issued traversal token.</summary>
    public ForearmTwistAuthoritySample GetLastAuthoritySample(LimbSide side)
        => IsSupportedSide(side) ? _authoritySamples[(int)side] : default;

    /// <summary>
    /// Submits a side's atomic canonical-pose authority sample from the immediately preceding IK-owned stage.
    /// </summary>
    public void SubmitAuthoritySample(LimbSide side, ForearmTwistAuthoritySample sample)
    {
        if (IsSupportedSide(side))
        {
            _authoritySamples[(int)side] = sample;
        }
    }

    /// <inheritdoc />
    public override void _SkeletonChanged(Skeleton3D oldSkeleton, Skeleton3D newSkeleton)
    {
        _ = oldSkeleton;
        _ = newSkeleton;
        ResetBindingState();
    }

    /// <summary>
    /// Fails a production installation when either authored side cannot be safely bound.
    /// </summary>
    /// <remarks>
    /// Production templates must carry the single-helper <c>LowerArm -> ForearmTwist -> Hand</c>
    /// chain per side; a second helper bone must not exist on either side.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when a required helper topology is absent or invalid.</exception>
    public void ValidateProductionTopology(Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        EnsureBound(skeleton);

        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            ForearmTwistBindingStatus status = GetBindingStatus(side);
            if (status != ForearmTwistBindingStatus.Valid)
            {
                throw new InvalidOperationException(BuildBindingFailureMessage(side, skeleton, status));
            }

            SideBinding binding = _bindings[(int)side]!;
            if (skeleton.GetBoneParent(binding.HandBoneIndex) != binding.HelperBoneIndex)
            {
                throw new InvalidOperationException(
                    $"Forearm twist modifier '{GetPath()}' requires the {side} production chain " +
                    $"LowerArm '{GetLowerArmBoneName(side)}' -> ForearmTwist '{GetHelperBoneName(side)}' -> " +
                    $"Hand '{GetHandBoneName(side)}' on skeleton '{skeleton.GetPath()}': the twist helper is " +
                    $"absent or mis-chained.");
            }
        }
    }

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        _ = delta;

        Skeleton3D? skeleton = _boundSkeleton;
        if (skeleton is null || !IsInstanceValid(skeleton))
        {
            skeleton = GetSkeleton();
            if (skeleton is null)
            {
                return;
            }
        }

        EnsureBound(skeleton);
        ProcessSide(skeleton, LimbSide.Left);
        ProcessSide(skeleton, LimbSide.Right);
    }

    private void EnsureBound(Skeleton3D skeleton)
    {
        if (ReferenceEquals(skeleton, _boundSkeleton))
        {
            return;
        }

        ResetBindingState();
        _boundSkeleton = skeleton;
        BindSide(skeleton, LimbSide.Left);
        BindSide(skeleton, LimbSide.Right);
    }

    private void BindSide(Skeleton3D skeleton, LimbSide side)
    {
        int sideIndex = (int)side;
        string lowerArmBoneName = GetLowerArmBoneName(side);
        string handBoneName = GetHandBoneName(side);
        string helperBoneName = GetHelperBoneName(side);
        int lowerArmBoneIndex = skeleton.FindBone(lowerArmBoneName);
        int handBoneIndex = skeleton.FindBone(handBoneName);
        int helperBoneIndex = skeleton.FindBone(helperBoneName);

        ForearmTwistBindingStatus status = ResolveBindingStatus(
            skeleton,
            lowerArmBoneIndex,
            handBoneIndex,
            helperBoneIndex);
        _bindingStatuses[sideIndex] = status;
        if (status != ForearmTwistBindingStatus.Valid)
        {
            WarnInvalidBindingOnce(side, skeleton, status);
            return;
        }

        Transform3D lowerArmRest = skeleton.GetBoneGlobalRest(lowerArmBoneIndex);
        Transform3D helperGlobalRest = skeleton.GetBoneGlobalRest(helperBoneIndex);
        _bindings[sideIndex] = new SideBinding(
            lowerArmBoneIndex,
            handBoneIndex,
            helperBoneIndex,
            lowerArmRest,
            helperGlobalRest,
            skeleton.GetBoneRest(helperBoneIndex));
    }

    private void ProcessSide(Skeleton3D skeleton, LimbSide side)
    {
        int sideIndex = (int)side;
        if (_bindingStatuses[sideIndex] != ForearmTwistBindingStatus.Valid)
        {
            return;
        }

        SideBinding binding = _bindings[sideIndex]!;
        ForearmTwistAuthoritySample sample = _authoritySamples[sideIndex];
        if (!sample.IsUsable || !ForearmTwistModificationPass.TryConsume(skeleton, sample.ModificationPassToken, side))
        {
            binding.ResetAuthorityTracking();
            WriteRestHelperPoses(skeleton, binding);
            return;
        }

        _ = binding.AuthorityState.Observe(sample);

        // Preflight the complete valid-ready chain before touching any bone. A failed affine
        // calculation retains the existing invalid-side rest policy, never a partial write.
        if (!ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
                binding.LowerArmGlobalRest,
                skeleton.GetBoneGlobalPose(binding.LowerArmBoneIndex),
                binding.HelperGlobalRest,
                skeleton.GetBoneGlobalRest(binding.HandBoneIndex),
                sample.CanonicalHandTransform,
                TwistWeight,
                out Transform3D twistLocal,
                out Transform3D handLocal))
        {
            binding.ResetAuthorityTracking();
            WriteRestHelperPoses(skeleton, binding);
            return;
        }

        skeleton.SetBonePose(binding.HelperBoneIndex, twistLocal);
        skeleton.SetBonePose(binding.HandBoneIndex, handLocal);
    }

    private static void WriteRestHelperPoses(Skeleton3D skeleton, SideBinding binding)
    {
        Transform3D handGlobal = skeleton.GetBoneGlobalPose(binding.HandBoneIndex);
        skeleton.SetBonePose(binding.HelperBoneIndex, binding.HelperLocalRest);
        Transform3D helperGlobalAfter = skeleton.GetBoneGlobalPose(binding.LowerArmBoneIndex) * binding.HelperLocalRest;
        skeleton.SetBonePose(binding.HandBoneIndex, helperGlobalAfter.AffineInverse() * handGlobal);
    }

    private static ForearmTwistBindingStatus ResolveBindingStatus(
        Skeleton3D skeleton,
        int lowerArmBoneIndex,
        int handBoneIndex,
        int helperBoneIndex)
    {
        if (lowerArmBoneIndex < 0)
        {
            return ForearmTwistBindingStatus.MissingLowerArm;
        }

        if (handBoneIndex < 0)
        {
            return ForearmTwistBindingStatus.MissingHand;
        }

        if (helperBoneIndex < 0)
        {
            return ForearmTwistBindingStatus.MissingHelper;
        }

        // The full chain is LowerArm -> ForearmTwist -> Hand: exactly one helper bone per side
        // with the hand re-parented onto it.
        if (skeleton.GetBoneParent(helperBoneIndex) != lowerArmBoneIndex
            || skeleton.GetBoneParent(handBoneIndex) != helperBoneIndex)
        {
            return ForearmTwistBindingStatus.InvalidHierarchy;
        }

        Transform3D lowerArmRest = skeleton.GetBoneGlobalRest(lowerArmBoneIndex);
        Transform3D handRest = skeleton.GetBoneGlobalRest(handBoneIndex);
        Transform3D helperRest = skeleton.GetBoneGlobalRest(helperBoneIndex);
        return ForearmTwistMath.TryComputeAffineTwistHelperLocalPoses(
            lowerArmRest,
            lowerArmRest,
            helperRest,
            handRest,
            handRest,
            0.0f,
            out _,
            out _)
            ? ForearmTwistBindingStatus.Valid
            : ForearmTwistBindingStatus.InvalidRestFrame;
    }

    private void WarnInvalidBindingOnce(LimbSide side, Skeleton3D skeleton, ForearmTwistBindingStatus status)
    {
        int sideIndex = (int)side;
        if (_bindingWarningsIssued[sideIndex])
        {
            return;
        }

        _bindingWarningsIssued[sideIndex] = true;
        if (GameLoggerResolver.TryResolve(out ILogger<ForearmTwistModifier>? logger) && logger is not null)
        {
            logger.LogWarning(
                "Forearm twist modifier failed to bind {Side} helper bone '{HelperBone}' on skeleton '{SkeletonPath}' with status {BindingStatus}; that side is disabled.",
                side,
                GetHelperBoneName(side),
                skeleton.GetPath(),
                status);
        }
    }

    private string BuildBindingFailureMessage(LimbSide side, Skeleton3D skeleton, ForearmTwistBindingStatus status)
        => $"Forearm twist modifier '{GetPath()}' cannot bind {side} helper bone '{GetHelperBoneName(side)}' " +
            $"on skeleton '{skeleton.GetPath()}': {status}.";

    private string GetLowerArmBoneName(LimbSide side) => side == LimbSide.Left ? LeftLowerArmBoneName : RightLowerArmBoneName;

    private string GetHandBoneName(LimbSide side) => side == LimbSide.Left ? LeftHandBoneName : RightHandBoneName;

    private string GetHelperBoneName(LimbSide side) => side == LimbSide.Left ? LeftForearmTwistBoneName : RightForearmTwistBoneName;

    private void ResetBindingState()
    {
        Skeleton3D? skeleton = _boundSkeleton;
        _boundSkeleton = null;
        if (skeleton is not null && IsInstanceValid(skeleton))
        {
            ForearmTwistModificationPass.Reset(skeleton);
        }
        Array.Clear(_bindings);
        Array.Fill(_bindingStatuses, ForearmTwistBindingStatus.Unvalidated);
        Array.Clear(_bindingWarningsIssued);
        Array.Clear(_authoritySamples);
    }

    private static bool IsSupportedSide(LimbSide side) => side is LimbSide.Left or LimbSide.Right;

    private sealed class SideBinding(
        int lowerArmBoneIndex,
        int handBoneIndex,
        int helperBoneIndex,
        Transform3D lowerArmGlobalRest,
        Transform3D helperGlobalRest,
        Transform3D helperLocalRest)
    {
        public int LowerArmBoneIndex { get; } = lowerArmBoneIndex;
        public int HandBoneIndex { get; } = handBoneIndex;
        public int HelperBoneIndex { get; } = helperBoneIndex;
        public Transform3D LowerArmGlobalRest { get; } = lowerArmGlobalRest;
        public Transform3D HelperGlobalRest { get; } = helperGlobalRest;
        public Transform3D HelperLocalRest { get; } = helperLocalRest;
        public ForearmTwistAuthorityTemporalState AuthorityState { get; } = new();
        public void ResetAuthorityTracking() => AuthorityState.Reset();
    }
}

/// <summary>Describes a bilateral forearm twist helper's cached binding result.</summary>
public enum ForearmTwistBindingStatus
{
    /// <summary>The side has not yet been bound.</summary>
    Unvalidated,

    /// <summary>The side has a complete, usable binding.</summary>
    Valid,

    /// <summary>The requested side is not a supported bilateral arm side.</summary>
    UnsupportedSide,

    /// <summary>The lower-arm bone is absent.</summary>
    MissingLowerArm,

    /// <summary>The hand bone is absent.</summary>
    MissingHand,

    /// <summary>The twist helper bone is absent.</summary>
    MissingHelper,

    /// <summary>The lower arm does not parent the helper, or the hand does not hang under the helper.</summary>
    InvalidHierarchy,

    /// <summary>The rest frame is finite but cannot provide a longitudinal axis.</summary>
    InvalidRestFrame,
}
