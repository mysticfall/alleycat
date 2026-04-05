using Godot;

namespace AlleyCat.Characters;

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
public partial class ArmIkController : SkeletonModifier3D
{
    private const float DegenerateThreshold = 1e-4f;

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

    private Skeleton3D _skeleton = null!;

    private int _hipsIdx;
    private int _neckIdx;
    private int _leftShoulderIdx;
    private int _rightShoulderIdx;
    private int _upperArmIdx;
    private bool _bonesResolved;

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        if (!_bonesResolved && !ResolveBones())
        {
            return;
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

        Vector3 bodyUp = (neckPos - hipsPos).Normalized();
        Vector3 bodyRight = (rShoulderPos - lShoulderPos).Normalized();

        // Orthonormalise: remove component of bodyRight along bodyUp
        bodyRight = (bodyRight - (bodyRight.Dot(bodyUp) * bodyUp)).Normalized();
        Vector3 bodyForward = bodyRight.Cross(bodyUp);

        // Godot convention: -Z is forward
        Basis bodyBasis = Basis.Identity;
        bodyBasis.Column0 = bodyRight;
        bodyBasis.Column1 = bodyUp;
        bodyBasis.Column2 = -bodyForward;

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
        Vector3 midpoint = (shoulderPos + handPos) * 0.5f;
        float armLength = (handPos - shoulderPos).Length();
        float offset = armLength * 0.5f;

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

        _upperArmIdx = skeleton.FindBone($"{sidePrefix}UpperArm");

        return true;
    }

    private Vector3 BoneGlobalPosition(int boneIdx) =>
        _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(boneIdx).Origin;
}
