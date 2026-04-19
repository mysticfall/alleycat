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
[GlobalClass]
public partial class ArmIKController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-4f;
    private const float SegmentEpsilon = 1e-4f;

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
    /// Overall shoulder correction dampening weight.
    /// 0 = no correction; 1 = full delta applied. Values below 1 dampen the correction.
    /// </summary>
    [Export]
    public float ShoulderWeight
    {
        get; set;
    } = 0.55f;

    /// <summary>
    /// Shoulder raise strength for arm elevation.
    /// Kept for integration test compatibility — controls adaptive weight contribution.
    /// </summary>
    [Export]
    public float ElevationWeight
    {
        get; set;
    } = 0.55f;

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
    private Basis _restLookBasis = Basis.Identity;
    private float _upperSegmentLength;
    private float _lowerSegmentLength;
    private float _restArmLength;

    private bool _bonesResolved;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
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
        Vector3 lateral = Side == ArmSide.Left
            ? new Vector3(-1f, 0f, 0f)
            : new Vector3(1f, 0f, 0f);
        Vector3 posterior = new(0f, 0f, -1f); // -Z is posterior (Skeleton3D uses +Z as forward)

        float lateralness = Mathf.Abs(armDirBody.Dot(lateral));
        float blend = Smoothstep(0.5f, 0.85f, lateralness);

        Vector3 desired = lateral.Lerp(posterior, blend).Normalized();
        Vector3 baselinePole = desired - (desired.Dot(armDirBody) * armDirBody);

        if (baselinePole.LengthSquared() < DegenerateThreshold)
        {
            // Fall back to bodyUp projected perpendicular to armDir
            baselinePole = Vector3.Up - (Vector3.Up.Dot(armDirBody) * armDirBody);
        }

        baselinePole = baselinePole.Normalized();

        // Phase 3 (hand-rotation adjustment) is deferred to a subsequent delivery phase.

        // Phase 4 -- Pole Target Placement
        Vector3 poleDirGlobal = bodyBasis * baselinePole;

        ApplyShoulderCorrectionPreIK(shoulderPos, handPos, poleDirGlobal, bodyUp, bodyRight);

        Vector3 midpoint = (shoulderPos + handPos) * 0.5f;
        float currentArmLength = shoulderPos.DistanceTo(handPos);
        float ratioBasedOffset = currentArmLength * PoleOffsetRatio;
        float offset = Mathf.Max(MinimumPoleOffset, ratioBasedOffset);

        float compressionRatioThreshold = Mathf.Clamp(CompressionRatioForRestPoleFloor, 0.1f, 1.0f);
        bool isArmCompressed = currentArmLength <= (_restArmLength * compressionRatioThreshold);
        float handToShoulderVerticalInBody = (handPos - shoulderPos).Dot(bodyUp);
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
    /// Performs smooth Hermite interpolation between 0 and 1.
    /// </summary>
    private static float Smoothstep(float edge0, float edge1, float x)
    {
        float t = Mathf.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);

        return t * t * (3f - (2f * t));
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

        Vector3 restShoulderToElbow = lowerArmGlobalRestPose.Origin - upperArmGlobalRestPose.Origin;

        if (restShoulderToElbow.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

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
        Vector3 restUpperArmDirectionBody = (restBodyBasisInverse * restShoulderToElbow).Normalized();

        if (restUpperArmDirectionBody.LengthSquared() < DegenerateThreshold)
        {
            return false;
        }

        _upperSegmentLength = (lowerArmGlobalRestPose.Origin - upperArmGlobalRestPose.Origin).Length();
        _lowerSegmentLength = (handGlobalRestPose.Origin - lowerArmGlobalRestPose.Origin).Length();

        if (_upperSegmentLength <= SegmentEpsilon || _lowerSegmentLength <= SegmentEpsilon)
        {
            return false;
        }

        _restArmLength = _upperSegmentLength + _lowerSegmentLength;

        // Build and cache the rest look-at basis using body-up as reference.
        Vector3 bodyUp = new(0f, 1f, 0f); // Up in body space is always +Y.
        _restLookBasis = ShoulderCorrectionComputer.BuildLookAtBasis(restUpperArmDirectionBody, bodyUp);

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

        // Build current look-at basis using body-up as reference.
        Vector3 bodyUpLocal = new(0f, 1f, 0f);
        Basis currentLookBasis = ShoulderCorrectionComputer.BuildLookAtBasis(currentUpperDirectionBody, bodyUpLocal);

        // Compute pose-adaptive weight: use ElevationWeight as the base dampening factor.
        float adaptiveWeight = ShoulderCorrectionComputer.ComputeAdaptiveWeight(
            currentUpperDirectionBody,
            ElevationWeight);

        // Apply overall ShoulderWeight as a master dampener on top.
        float effectiveWeight = adaptiveWeight * Mathf.Clamp(ShoulderWeight / Mathf.Max(ElevationWeight, 0.01f), 0f, 2f);
        effectiveWeight = Mathf.Clamp(effectiveWeight, 0f, 1f);

        Quaternion correction = ShoulderCorrectionComputer.ComputeCorrection(
            _restLookBasis,
            currentLookBasis,
            effectiveWeight);

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
