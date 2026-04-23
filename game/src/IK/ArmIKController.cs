using AlleyCat.IK.Anchors;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Which arm this controller drives.
/// </summary>
public enum ArmSide
{
    /// <summary>
    /// The left arm.
    /// </summary>
    Left,

    /// <summary>
    /// The right arm.
    /// </summary>
    Right
}

/// <summary>
/// Computes elbow pole-target positions each frame for VR arm IK.
/// Attach as a direct child of a <see cref="Skeleton3D"/> node.
/// </summary>
[Tool]
[GlobalClass]
public partial class ArmIKController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-4f;
    private const float SegmentEpsilon = 1e-4f;
    private const float NormalisationEpsilon = 1e-3f;

    /// <summary>
    /// The VR hand target for this arm.
    /// </summary>
    [Export]
    public Node3D? HandTarget
    {
        get; set;
    }

    /// <summary>
    /// The pole-target marker whose position this script drives.
    /// </summary>
    [Export]
    public Node3D? PoleTarget
    {
        get; set;
    }

    /// <summary>
    /// Which arm this controller drives.
    /// </summary>
    [Export]
    public ArmSide Side
    {
        get; set;
    }

    /// <summary>
    /// Configurable arm pole-anchor set used to predict the baseline elbow pole direction.
    /// </summary>
    [Export]
    public ArmPoleAnchorSetResource? PoleAnchorSet
    {
        get; set;
    }

    /// <summary>
    /// Overall dampening weight for shoulder correction. 0 = no correction; 1 = full anatomical correction.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.01")]
    public float ShoulderWeight
    {
        get; set;
    } = 0.2f;

    /// <summary>
    /// Maximum shoulder elevation angle, in degrees, when the arm points straight up (arm-Y = 1).
    /// Tunes how strongly the shoulder lifts at overhead poses. Actual applied angle is scaled by
    /// elevation factor and <see cref="ShoulderWeight"/>.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,180.0,0.5")]
    public float MaxElevationAngleDegrees
    {
        get; set;
    } = 160f;

    /// <summary>
    /// Additional shoulder elevation angle, in degrees, added when the arm points straight up (arm-Y = 1).
    /// Ramps smoothly from 0 at horizontal and below to the full value at fully overhead. Tune this upward
    /// if the shoulder does not visibly lift more at arms-up than at T-pose. Applied on top of
    /// <see cref="MaxElevationAngleDegrees"/> and subject to <see cref="ShoulderWeight"/> dampening.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,300.0,0.5")]
    public float MaxOverheadElevationBoostDegrees
    {
        get; set;
    } = 120f;

    /// <summary>
    /// Maximum shoulder protraction angle, in degrees, when the arm points straight forward (arm-Z = 1).
    /// Tunes how strongly the shoulder moves forward at arms-forward poses. Actual applied angle is scaled by
    /// protraction factor and <see cref="ShoulderWeight"/>.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,60.0,0.5")]
    public float MaxProtractionAngleDegrees
    {
        get; set;
    } = 20f;

    /// <summary>
    /// Lateral bias of the anatomical neutral arm direction in body space (0 = straight down, 0.95 ≈ horizontal).
    /// Lower values produce more shoulder lift at T-pose rest and overhead poses. Higher values reduce it.
    /// Tune this to compensate for baseline rest-pose shoulder height.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,0.95,0.01")]
    public float AnatomicalNeutralLateralBias
    {
        get; set;
    } = 0.15f;

    /// <summary>
    /// Fraction of elevation that is suppressed when the arm points straight forward (arm-Z = 1).
    /// 0 = elevation unaffected by arm-forward component (current behaviour).
    /// 1 = elevation fully suppressed at arms-forward.
    /// Typical values are 0.2–0.4. Damping scales linearly with the positive forward component of the arm.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,1.0,0.01")]
    public float ForwardElevationDamping
    {
        get; set;
    } = 0.3f;

    /// <summary>
    /// Pole offset as a fraction of shoulder-to-target distance.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,1.0,0.01")]
    public float PoleOffsetRatio
    {
        get; set;
    } = 0.5f;

    /// <summary>
    /// Lower bound for elbow pole offset distance.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,0.5,0.001")]
    public float MinimumPoleOffset
    {
        get; set;
    } = 0.12f;

    /// <summary>
    /// Extra distance added to the rest-arm-based minimum pole offset floor.
    /// </summary>
    [Export(PropertyHint.Range, "0.0,0.2,0.001")]
    public float RestArmHalfPoleOffsetMargin
    {
        get; set;
    } = 0.1f;

    /// <summary>
    /// Arm compression ratio threshold for applying the rest-arm pole offset floor.
    /// </summary>
    [Export(PropertyHint.Range, "0.1,1.0,0.01")]
    public float CompressionRatioForRestPoleFloor
    {
        get; set;
    } = 0.6f;

    /// <summary>
    /// When true, logs diagnostic information when a frame-to-frame angular jump in
    /// the computed pole direction exceeds a threshold. Intended for temporary
    /// development diagnosis; should be disabled in normal play.
    /// </summary>
    [Export]
    public bool DebugLogPoleJumps
    {
        get; set;
    }

    /// <summary>
    /// Angular jump threshold (in degrees) above which a diagnostic log entry is
    /// emitted when <see cref="DebugLogPoleJumps"/> is true.
    /// </summary>
    [Export(PropertyHint.Range, "1.0,90.0,0.5")]
    public float DebugPoleJumpThresholdDegrees
    {
        get; set;
    } = 15.0f;

    private Skeleton3D _skeleton = null!;

    private int _hipsIdx;
    private int _neckIdx;
    private int _leftShoulderIdx;
    private int _rightShoulderIdx;
    private int _shoulderIdx;
    private int _upperArmIdx;
    private int _lowerArmIdx;
    private int _handIdx;

    private Basis _shoulderRestBasisInBody = Basis.Identity;
    private float _upperSegmentLength;
    private float _lowerSegmentLength;
    private float _restArmLength;

    private bool _bonesResolved;
    private bool _hasWarnedMissingPoleAnchorSet;

    private Vector3? _previousPoleDirBody;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        if (!Active)
        {
            return;
        }

        if (!_bonesResolved)
        {
            if (!ResolveBones())
            {
                return;
            }

            _bonesResolved = true;
        }

        if (HandTarget is null || PoleTarget is null)
        {
            return;
        }

        // Phase 0 -- Body Reference Frame
        Vector3 hipsPos = BoneGlobalPosition(_hipsIdx);
        Vector3 neckPos = BoneGlobalPosition(_neckIdx);
        Vector3 lShoulderPos = BoneGlobalPosition(_leftShoulderIdx);
        Vector3 rShoulderPos = BoneGlobalPosition(_rightShoulderIdx);

        if (!TryBuildBodyBasis(
                hipsPos,
                neckPos,
                lShoulderPos,
                rShoulderPos,
                out Basis bodyBasis))
        {
            return;
        }

        Vector3 bodyUp = bodyBasis.Column1;
        Vector3 bodyRight = bodyBasis.Column0;

        Basis bodyBasisInverse = bodyBasis.Inverse();

        // Phase 1 -- Arm Direction in Body Space
        Vector3 shoulderPos = BoneGlobalPosition(_upperArmIdx);
        Vector3 handPos = HandTarget.GlobalPosition;
        Vector3 armDirGlobal = (handPos - shoulderPos).Normalized();
        Vector3 armDirBody = bodyBasisInverse * armDirGlobal;

        // Phase 2 -- Baseline Pole Direction
        if (!TryComputeBaselinePoleDirectionBody(armDirBody, bodyBasis, shoulderPos, handPos, out Vector3 baselinePole))
        {
            // Resource-driven no-op: without a valid anchor set and no prior valid baseline,
            // leave pole target unchanged rather than introducing hardcoded direction defaults.
            return;
        }

        // Phase 3 (hand-rotation adjustment) is deferred to a subsequent delivery phase.

        // Phase 4 -- Pole Target Placement
        Vector3 poleDirGlobal = bodyBasis * baselinePole;

        ApplyShoulderCorrectionPreIK(shoulderPos, handPos, poleDirGlobal, bodyUp, bodyRight);

        Vector3 solvedShoulderPos = BoneGlobalPosition(_upperArmIdx);
        Vector3 midpoint = (solvedShoulderPos + handPos) * 0.5f;
        float currentArmLength = solvedShoulderPos.DistanceTo(handPos);
        float ratioBasedOffset = currentArmLength * PoleOffsetRatio;
        float offset = Mathf.Max(MinimumPoleOffset, ratioBasedOffset);

        float compressionRatioThreshold = Mathf.Clamp(CompressionRatioForRestPoleFloor, 0.1f, 1.0f);
        bool isArmCompressed = currentArmLength <= (_restArmLength * compressionRatioThreshold);
        float handToShoulderVerticalInBody = (handPos - solvedShoulderPos).Dot(bodyUp);
        bool isFoldedReachLikeCompression = handToShoulderVerticalInBody <= 0f;
        if (isArmCompressed && isFoldedReachLikeCompression)
        {
            float restArmMinimumOffset = (_restArmLength * 0.5f) + RestArmHalfPoleOffsetMargin;
            float compressedFloor = Mathf.Max(MinimumPoleOffset, restArmMinimumOffset);
            offset = Mathf.Max(offset, compressedFloor);
        }

        PoleTarget.GlobalPosition = midpoint + (poleDirGlobal * offset);
    }

    /// <summary>
    /// Computes the baseline elbow pole direction in body basis as a continuous function of
    /// the arm direction (unit vector in body basis).
    /// </summary>
    /// <remarks>
    /// The function is a C-infinity smooth inverse-distance-weighted (IDW) blend over configurable
    /// key-pose anchors expressed in the body basis. Each anchor defines a reference arm
    /// direction and an associated pole intent (not required to be unit length or perpendicular
    /// to the arm direction). Per-anchor raw weights are computed as
    /// <c>1 / (angularDistance² + ε²)</c> with <c>ε = 0.01 rad</c>, so the nearest anchor
    /// dominates by a factor of ~10 000 when the arm direction coincides with that anchor's
    /// reference. Weights are normalised, and each anchor's pole intent is first projected onto
    /// the plane perpendicular to the arm direction before being accumulated into the weighted
    /// sum. Because every term in the accumulation is already perpendicular to the arm
    /// direction, the resulting combined vector is automatically perpendicular and cannot
    /// collapse to near-zero due to an accidental alignment between the pre-projection weighted
    /// sum and the arm direction — eliminating the projection-collapse failure mode that a
    /// post-projection structure exhibits near such alignments. The combined vector is then
    /// smoothly normalised via the standard <c>sqrt(|v|² + ε²)</c> divisor using
    /// <see cref="NormalisationEpsilon"/>.
    /// </remarks>
    /// <param name="armDirBody">Unit vector from shoulder to hand expressed in the body basis.</param>
    /// <param name="bodyBasis">Body reference frame (only used for diagnostic logging).</param>
    /// <param name="shoulderPos">Global shoulder position (only used for diagnostic logging).</param>
    /// <param name="handPos">Global hand-target position (only used for diagnostic logging).</param>
    /// <param name="baselinePole">Resolved baseline pole direction when available.</param>
    /// <returns>
    /// True when a baseline pole direction is available. False when pole-anchor resources are
    /// invalid and no previous valid baseline exists.
    /// </returns>
    private bool TryComputeBaselinePoleDirectionBody(
        Vector3 armDirBody,
        Basis bodyBasis,
        Vector3 shoulderPos,
        Vector3 handPos,
        out Vector3 baselinePole)
    {
        baselinePole = Vector3.Zero;

        ArmPoleAnchorResource[]? anchors = PoleAnchorSet?.Anchors;

        int anchorCount = anchors?.Length ?? 0;

        if (anchorCount <= 0)
        {
            return TryUsePreviousPoleDirection(
                "PoleAnchorSet is missing or has no anchors. Assign a non-empty ArmPoleAnchorSetResource.",
                out baselinePole);
        }

        float epsilon = Mathf.Max(1e-4f, PoleAnchorSet!.WeightEpsilonRadians);
        float reachWeight = PoleAnchorSet.ReachWeight;
        float currentArmLength = shoulderPos.DistanceTo(handPos);
        float currentReachRatio = currentArmLength / Mathf.Max(_restArmLength, 1e-4f);

        // First pass: compute raw IDW weights and their sum.
        Span<float> rawWeights = stackalloc float[anchorCount];
        float weightSum = 0f;

        for (int i = 0; i < anchorCount; i++)
        {
            ArmPoleAnchorResource anchor = anchors![i];

            Vector3 mirroredAnchorArmDir = PoleAnchorSet.MirrorXForSide(anchor.ArmDirBody.Normalized(), Side);
            float cosAngle = Mathf.Clamp(armDirBody.Dot(mirroredAnchorArmDir), -1f, 1f);
            float angularDistance = Mathf.Acos(cosAngle);
            float reachDelta = currentReachRatio - anchor.ReachRatio;
            float weightedReachDelta = reachWeight * reachDelta;
            float denom = (angularDistance * angularDistance)
                + (weightedReachDelta * weightedReachDelta)
                + (epsilon * epsilon);
            float w = 1f / denom;

            rawWeights[i] = w;
            weightSum += w;
        }

        if (weightSum <= DegenerateThreshold)
        {
            return TryUsePreviousPoleDirection(
                "PoleAnchorSet produced a degenerate zero-weight blend.",
                out baselinePole);
        }

        // Second pass: normalise weights and accumulate per-anchor perpendicular pole intents.
        // Projecting each anchor's pole intent onto the plane perpendicular to armDirBody before
        // the weighted sum guarantees that the combined vector is itself perpendicular, which
        // avoids the projection-collapse failure mode that can occur when the un-projected
        // weighted sum happens to align with armDirBody.
        float invWeightSum = 1f / weightSum;
        Vector3 combined = Vector3.Zero;
        Vector3 rawPole = Vector3.Zero;

        for (int i = 0; i < anchorCount; i++)
        {
            ArmPoleAnchorResource? anchor = anchors![i];
            if (anchor is null)
            {
                continue;
            }

            float normalisedWeight = rawWeights[i] * invWeightSum;
            Vector3 poleIntent = PoleAnchorSet.MirrorXForSide(anchor.PoleIntentBody, Side);
            Vector3 poleIntentPerp = poleIntent - (poleIntent.Dot(armDirBody) * armDirBody);

            combined += poleIntentPerp * normalisedWeight;

            // rawPole is retained purely as a diagnostic reference (the un-projected weighted
            // sum). It is not fed into the final baseline pole direction.
            rawPole += poleIntent * normalisedWeight;
        }

        float combinedLength = combined.Length();

        // Smooth normalisation: sqrt(|v|² + ε²) asymptotes to |v| away from zero and shrinks
        // smoothly toward zero as |v| → 0. Prevents a hard degenerate-case branch.
        float normScale = 1f / Mathf.Sqrt((combinedLength * combinedLength)
            + (NormalisationEpsilon * NormalisationEpsilon));
        baselinePole = combined * normScale;

        if (DebugLogPoleJumps && _previousPoleDirBody is { } previousPoleDirBody)
        {
            float dot = Mathf.Clamp(previousPoleDirBody.Dot(baselinePole), -1f, 1f);
            float jumpDegrees = Mathf.RadToDeg(Mathf.Acos(dot));

            if (jumpDegrees > DebugPoleJumpThresholdDegrees)
            {
                Vector3 bodyRight = bodyBasis.Column0;
                Vector3 bodyUp = bodyBasis.Column1;
                Vector3 bodyForward = -bodyBasis.Column2;

                string prefix = $"[ArmIKController:{Side} POLE_JUMP]";

                // Identify the top 3 anchors by normalised weight for diagnostic reporting.
                Span<int> topIndices = [-1, -1, -1];
                Span<float> topWeights = [-1f, -1f, -1f];
                for (int i = 0; i < anchorCount; i++)
                {
                    float normalisedWeight = rawWeights[i] * invWeightSum;
                    for (int slot = 0; slot < 3; slot++)
                    {
                        if (normalisedWeight > topWeights[slot])
                        {
                            for (int shift = 2; shift > slot; shift--)
                            {
                                topWeights[shift] = topWeights[shift - 1];
                                topIndices[shift] = topIndices[shift - 1];
                            }
                            topWeights[slot] = normalisedWeight;
                            topIndices[slot] = i;
                            break;
                        }
                    }
                }

                System.Text.StringBuilder topAnchorsLog = new();
                for (int slot = 0; slot < 3; slot++)
                {
                    int idx = topIndices[slot];
                    if (idx < 0)
                    {
                        continue;
                    }
                    ArmPoleAnchorResource? anchor = anchors![idx];
                    if (anchor is null)
                    {
                        continue;
                    }

                    Vector3 mirroredAnchorArmDir = PoleAnchorSet.MirrorXForSide(anchor.ArmDirBody.Normalized(), Side);
                    Vector3 mirroredPoleIntent = PoleAnchorSet.MirrorXForSide(anchor.PoleIntentBody, Side);
                    float cosAngle = Mathf.Clamp(armDirBody.Dot(mirroredAnchorArmDir), -1f, 1f);
                    float angularDistanceDeg = Mathf.RadToDeg(Mathf.Acos(cosAngle));
                    float reachDelta = currentReachRatio - anchor.ReachRatio;
                    _ = topAnchorsLog.AppendLine(
                        $"{prefix} top[{slot}] name={anchor.Name} "
                        + $"armDirRefMirrored=({mirroredAnchorArmDir.X:F4},{mirroredAnchorArmDir.Y:F4},{mirroredAnchorArmDir.Z:F4}) "
                        + $"poleRefMirrored=({mirroredPoleIntent.X:F4},{mirroredPoleIntent.Y:F4},{mirroredPoleIntent.Z:F4}) "
                        + $"anchorReachRatio={anchor.ReachRatio:F4} "
                        + $"reachDelta={reachDelta:F4} "
                        + $"angularDistanceDeg={angularDistanceDeg:F4} "
                        + $"normalisedWeight={topWeights[slot]:F6}");
                }

                GD.Print(
                    $"{prefix} armDirBody=({armDirBody.X:F4},{armDirBody.Y:F4},{armDirBody.Z:F4})\n"
                    + $"{prefix} currentReachRatio={currentReachRatio:F4}\n"
                    + $"{prefix} handPos(global)=({handPos.X:F4},{handPos.Y:F4},{handPos.Z:F4}) "
                    + $"shoulderPos(global)=({shoulderPos.X:F4},{shoulderPos.Y:F4},{shoulderPos.Z:F4})\n"
                    + $"{prefix} bodyRight=({bodyRight.X:F4},{bodyRight.Y:F4},{bodyRight.Z:F4}) "
                    + $"bodyUp=({bodyUp.X:F4},{bodyUp.Y:F4},{bodyUp.Z:F4}) "
                    + $"bodyForward=({bodyForward.X:F4},{bodyForward.Y:F4},{bodyForward.Z:F4})\n"
                    + topAnchorsLog
                    + $"{prefix} rawPole_DIAGNOSTIC_ONLY=({rawPole.X:F4},{rawPole.Y:F4},{rawPole.Z:F4})\n"
                    + $"{prefix} combined=({combined.X:F4},{combined.Y:F4},{combined.Z:F4}) "
                    + $"combinedLength={combinedLength:F4}\n"
                    + $"{prefix} previousBaselinePole=({previousPoleDirBody.X:F4},{previousPoleDirBody.Y:F4},{previousPoleDirBody.Z:F4}) "
                    + $"currentBaselinePole=({baselinePole.X:F4},{baselinePole.Y:F4},{baselinePole.Z:F4})\n"
                    + $"{prefix} angularJumpDegrees={jumpDegrees:F4}");
            }
        }

        _previousPoleDirBody = baselinePole;

        return true;
    }

    private bool TryUsePreviousPoleDirection(string warningMessage, out Vector3 baselinePole)
    {
        baselinePole = Vector3.Zero;

        if (!_hasWarnedMissingPoleAnchorSet)
        {
            GD.PushWarning($"[ArmIKController:{Side}] {warningMessage}");
            _hasWarnedMissingPoleAnchorSet = true;
        }

        if (_previousPoleDirBody is not { } previousPoleDirBody)
        {
            return false;
        }

        baselinePole = previousPoleDirBody;
        return true;
    }

    private bool ResolveBones()
    {
        Skeleton3D? skeleton = GetSkeleton();

        if (skeleton is null)
        {
            return false;
        }

        _skeleton = skeleton;

        _hipsIdx = skeleton.FindBone("Hips");
        _neckIdx = skeleton.FindBone("Neck");
        _leftShoulderIdx = skeleton.FindBone("LeftShoulder");
        _rightShoulderIdx = skeleton.FindBone("RightShoulder");

        string sidePrefix = Side == ArmSide.Left ? "Left" : "Right";

        _shoulderIdx = skeleton.FindBone($"{sidePrefix}Shoulder");
        _upperArmIdx = skeleton.FindBone($"{sidePrefix}UpperArm");
        _lowerArmIdx = skeleton.FindBone($"{sidePrefix}LowerArm");
        _handIdx = skeleton.FindBone($"{sidePrefix}Hand");

        return _hipsIdx >= 0
            && _neckIdx >= 0
            && _leftShoulderIdx >= 0
            && _rightShoulderIdx >= 0
            && _shoulderIdx >= 0
            && _upperArmIdx >= 0
            && _lowerArmIdx >= 0
            && _handIdx >= 0
            && TryCacheRestData();
    }

    private bool TryCacheRestData()
    {
        Transform3D hipsGlobalRestPose = _skeleton.GetBoneGlobalRest(_hipsIdx);
        Transform3D neckGlobalRestPose = _skeleton.GetBoneGlobalRest(_neckIdx);
        Transform3D leftShoulderGlobalRestPose = _skeleton.GetBoneGlobalRest(_leftShoulderIdx);
        Transform3D rightShoulderGlobalRestPose = _skeleton.GetBoneGlobalRest(_rightShoulderIdx);
        Transform3D shoulderGlobalRestPose = _skeleton.GetBoneGlobalRest(_shoulderIdx);
        Transform3D upperArmGlobalRestPose = _skeleton.GetBoneGlobalRest(_upperArmIdx);
        Transform3D lowerArmGlobalRestPose = _skeleton.GetBoneGlobalRest(_lowerArmIdx);
        Transform3D handGlobalRestPose = _skeleton.GetBoneGlobalRest(_handIdx);

        if (!TryBuildBodyBasis(
                hipsGlobalRestPose.Origin,
                neckGlobalRestPose.Origin,
                leftShoulderGlobalRestPose.Origin,
                rightShoulderGlobalRestPose.Origin,
                out Basis restBodyBasis))
        {
            return false;
        }

        Basis restBodyBasisInverse = restBodyBasis.Inverse();

        _upperSegmentLength = (lowerArmGlobalRestPose.Origin - upperArmGlobalRestPose.Origin).Length();
        _lowerSegmentLength = (handGlobalRestPose.Origin - lowerArmGlobalRestPose.Origin).Length();

        if (_upperSegmentLength <= SegmentEpsilon || _lowerSegmentLength <= SegmentEpsilon)
        {
            return false;
        }

        _restArmLength = _upperSegmentLength + _lowerSegmentLength;

        _shoulderRestBasisInBody = (restBodyBasisInverse * shoulderGlobalRestPose.Basis).Orthonormalized();

        return true;
    }

    private void ApplyShoulderCorrectionPreIK(
        Vector3 shoulderPosition,
        Vector3 handTargetPosition,
        Vector3 poleDirectionGlobal,
        Vector3 bodyUp,
        Vector3 bodyRight)
    {
        if (!TryEstimateUpperArmDirection(
                shoulderPosition,
                handTargetPosition,
                poleDirectionGlobal,
                bodyUp,
                bodyRight,
                out Vector3 expectedUpperDirectionGlobal))
        {
            return;
        }

        Basis skeletonGlobalBasisInverse = _skeleton.GlobalTransform.Basis.Inverse();
        Vector3 expectedUpperDirectionSkeleton = skeletonGlobalBasisInverse * expectedUpperDirectionGlobal;

        if (expectedUpperDirectionSkeleton.LengthSquared() < DegenerateThreshold)
        {
            return;
        }

        if (!TryBuildBodyBasis(
                _skeleton.GetBoneGlobalPose(_hipsIdx).Origin,
                _skeleton.GetBoneGlobalPose(_neckIdx).Origin,
                _skeleton.GetBoneGlobalPose(_leftShoulderIdx).Origin,
                _skeleton.GetBoneGlobalPose(_rightShoulderIdx).Origin,
                out Basis bodyBasisSkeleton))
        {
            return;
        }

        Vector3 currentUpperDirectionBody = (bodyBasisSkeleton.Inverse() * expectedUpperDirectionSkeleton).Normalized();

        if (currentUpperDirectionBody.LengthSquared() < DegenerateThreshold)
        {
            return;
        }

        Vector3 neutralDir = ShoulderCorrectionComputer.ComputeAnatomicalNeutralDirection(
            Side,
            AnatomicalNeutralLateralBias);

        Quaternion correction = ShoulderCorrectionComputer.ComputeCorrection(
            currentUpperDirectionBody,
            Side,
            neutralDir.Y,
            Mathf.DegToRad(MaxElevationAngleDegrees),
            Mathf.DegToRad(MaxOverheadElevationBoostDegrees),
            Mathf.DegToRad(MaxProtractionAngleDegrees),
            ForwardElevationDamping,
            Mathf.Clamp(ShoulderWeight, 0f, 1f));

        Basis correctionBasis = new(correction);
        Basis targetShoulderGlobalBasis = (bodyBasisSkeleton * (correctionBasis * _shoulderRestBasisInBody)).Orthonormalized();
        int shoulderParentBoneIndex = _skeleton.GetBoneParent(_shoulderIdx);
        Basis parentGlobalBasis = shoulderParentBoneIndex >= 0
            ? _skeleton.GetBoneGlobalPose(shoulderParentBoneIndex).Basis
            : Basis.Identity;
        Basis targetShoulderLocalBasis = (parentGlobalBasis.Inverse() * targetShoulderGlobalBasis).Orthonormalized();

        _skeleton.SetBonePoseRotation(
            _shoulderIdx,
            targetShoulderLocalBasis.GetRotationQuaternion().Normalized());
    }

    private static bool TryBuildBodyBasis(
        Vector3 hipsPosition,
        Vector3 neckPosition,
        Vector3 leftShoulderPosition,
        Vector3 rightShoulderPosition,
        out Basis bodyBasis)
    {
        bodyBasis = Basis.Identity;

        Vector3 bodyUp = neckPosition - hipsPosition;

        if (bodyUp.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        bodyUp = bodyUp.Normalized();

        Vector3 shoulderSpan = rightShoulderPosition - leftShoulderPosition;
        Vector3 bodyRight = shoulderSpan - (shoulderSpan.Dot(bodyUp) * bodyUp);

        if (bodyRight.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        bodyRight = bodyRight.Normalized();
        Vector3 bodyForward = bodyRight.Cross(bodyUp);

        if (bodyForward.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        bodyForward = bodyForward.Normalized();

        bodyBasis.Column0 = bodyRight;
        bodyBasis.Column1 = bodyUp;
        bodyBasis.Column2 = -bodyForward;

        bodyBasis = bodyBasis.Orthonormalized();

        return true;
    }

    private bool TryEstimateUpperArmDirection(
        Vector3 shoulderPosition,
        Vector3 handTargetPosition,
        Vector3 poleDirectionGlobal,
        Vector3 bodyUp,
        Vector3 bodyRight,
        out Vector3 expectedUpperDirectionGlobal)
    {
        expectedUpperDirectionGlobal = Vector3.Zero;

        Vector3 shoulderToHand = handTargetPosition - shoulderPosition;
        float distance = shoulderToHand.Length();

        if (distance <= SegmentEpsilon)
        {
            return false;
        }

        Vector3 directionToHand = shoulderToHand / distance;

        float minReach = Mathf.Abs(_upperSegmentLength - _lowerSegmentLength) + SegmentEpsilon;
        float maxReach = _upperSegmentLength + _lowerSegmentLength - SegmentEpsilon;

        if (maxReach <= minReach)
        {
            return false;
        }

        float clampedDistance = Mathf.Clamp(distance, minReach, maxReach);
        float a = ((_upperSegmentLength * _upperSegmentLength)
                   - (_lowerSegmentLength * _lowerSegmentLength)
                   + (clampedDistance * clampedDistance))
                  / (2f * clampedDistance);
        float hSquared = Mathf.Max((_upperSegmentLength * _upperSegmentLength) - (a * a), 0f);
        float h = Mathf.Sqrt(hSquared);

        Vector3 basePoint = shoulderPosition + (directionToHand * a);
        Vector3 polePerpendicular = ProjectPerpendicular(poleDirectionGlobal, directionToHand);

        if (polePerpendicular.LengthSquared() < DegenerateThreshold)
        {
            polePerpendicular = ProjectPerpendicular(bodyUp, directionToHand);
        }

        if (polePerpendicular.LengthSquared() < DegenerateThreshold)
        {
            Vector3 lateral = Side == ArmSide.Left ? -bodyRight : bodyRight;
            polePerpendicular = ProjectPerpendicular(lateral, directionToHand);
        }

        if (polePerpendicular.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        polePerpendicular = polePerpendicular.Normalized();
        Vector3 estimatedElbowPosition = basePoint + (polePerpendicular * h);
        Vector3 upperDirection = estimatedElbowPosition - shoulderPosition;

        if (upperDirection.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        expectedUpperDirectionGlobal = upperDirection.Normalized();
        return true;
    }

    private static Vector3 ProjectPerpendicular(Vector3 vector, Vector3 normal) =>
        vector - (vector.Dot(normal) * normal);

    private Vector3 BoneGlobalPosition(int boneIdx) =>
        _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIdx).Origin;
}
