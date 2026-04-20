using Godot;

namespace AlleyCat.IK.Anchors.Authoring;

/// <summary>
/// Editor-only authoring root for baking arm-pole anchors into a resource.
/// </summary>
[Tool]
[GlobalClass]
public partial class ArmPoleAnchorSetAuthoringRoot : Node3D
{
    private const float DegenerateThreshold = 1e-4f;

    private bool _isBakeResetPending;

    /// <summary>
    /// Gets or sets the path to the source skeleton.
    /// </summary>
    [Export]
    public NodePath SkeletonPath
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets the path to the pose container whose direct children are baked.
    /// </summary>
    [Export]
    public NodePath PoseContainerPath
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets which side the authored poses represent.
    /// </summary>
    [Export]
    public ArmSide AuthoredSide
    {
        get;
        set;
    } = ArmSide.Right;

    /// <summary>
    /// Gets or sets the output resource path to write.
    /// </summary>
    [Export(PropertyHint.File, "*.tres")]
    public string OutputResourcePath
    {
        get;
        set;
    } = string.Empty;

    /// <summary>
    /// Gets or sets epsilon used by inverse-distance weighting.
    /// </summary>
    [Export]
    public float WeightEpsilonRadians
    {
        get;
        set;
    } = 0.01f;

    /// <summary>
    /// Gets or sets reach-delta weighting multiplier.
    /// </summary>
    [Export]
    public float ReachWeight
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a value indicating whether to trigger bake in editor.
    /// </summary>
    [Export]
    public bool BakeNow
    {
        get;
        set
        {
            field = value;

            if (!field || _isBakeResetPending)
            {
                return;
            }

            _isBakeResetPending = true;

            try
            {
                if (Engine.IsEditorHint())
                {
                    BakeAnchors();
                }

                field = false;
                NotifyPropertyListChanged();
            }
            finally
            {
                _isBakeResetPending = false;
            }
        }
    }

    private void BakeAnchors()
    {
        if (SkeletonPath.IsEmpty)
        {
            ReportError("SkeletonPath is empty.");
            return;
        }

        if (PoseContainerPath.IsEmpty)
        {
            ReportError("PoseContainerPath is empty.");
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputResourcePath))
        {
            ReportError("OutputResourcePath is empty.");
            return;
        }

        Skeleton3D? skeleton = GetNodeOrNull<Skeleton3D>(SkeletonPath);

        if (skeleton is null)
        {
            ReportError($"SkeletonPath '{SkeletonPath}' did not resolve to Skeleton3D.");
            return;
        }

        Node? poseContainer = GetNodeOrNull<Node>(PoseContainerPath);

        if (poseContainer is null)
        {
            ReportError($"PoseContainerPath '{PoseContainerPath}' did not resolve to a node.");
            return;
        }

        if (!TryResolveRequiredBones(skeleton, AuthoredSide, out BoneIndices bones))
        {
            return;
        }

        Vector3 hipsPosition = BoneGlobalPosition(skeleton, bones.Hips);
        Vector3 neckPosition = BoneGlobalPosition(skeleton, bones.Neck);
        Vector3 leftShoulderPosition = BoneGlobalPosition(skeleton, bones.LeftShoulder);
        Vector3 rightShoulderPosition = BoneGlobalPosition(skeleton, bones.RightShoulder);

        if (!TryBuildBodyBasis(
                hipsPosition,
                neckPosition,
                leftShoulderPosition,
                rightShoulderPosition,
                out Basis bodyBasis))
        {
            ReportError("Failed to build body basis from skeleton bones (Hips/Neck/Shoulders). Check pose degeneracy.");
            return;
        }

        float restUpperLength = (skeleton.GetBoneGlobalRest(bones.LowerArm).Origin -
                                 skeleton.GetBoneGlobalRest(bones.UpperArm).Origin)
            .Length();
        float restLowerLength = (skeleton.GetBoneGlobalRest(bones.Hand).Origin -
                                 skeleton.GetBoneGlobalRest(bones.LowerArm).Origin)
            .Length();
        float restArmLength = restUpperLength + restLowerLength;

        if (restArmLength <= DegenerateThreshold)
        {
            ReportError("Failed to compute rest arm length from skeleton rest pose.");
            return;
        }

        Vector3 shoulderPosition = BoneGlobalPosition(skeleton, bones.Shoulder);
        Basis bodyInverse = bodyBasis.Inverse();
        List<ArmPoleAnchorResource> bakedAnchors = [];

        foreach (Node child in poseContainer.GetChildren())
        {
            if (child is not ArmPoleAnchorAuthoringPose pose)
            {
                continue;
            }

            if (!pose.TryGetMarkers(out Node3D handMarker, out Node3D poleMarker))
            {
                ReportError($"Pose '{pose.Name}' has invalid marker paths.");
                return;
            }

            Vector3 armVector = handMarker.GlobalPosition - shoulderPosition;

            if (armVector.LengthSquared() < DegenerateThreshold)
            {
                ReportError($"Pose '{pose.Name}' produced degenerate arm vector (hand at shoulder).");
                return;
            }

            Vector3 armDirBody = (bodyInverse * armVector).Normalized();
            float reachRatio = armVector.Length() / Mathf.Max(restArmLength, DegenerateThreshold);
            Vector3 poleBody = bodyInverse * (poleMarker.GlobalPosition - shoulderPosition);
            Vector3 polePerpendicular = ProjectPerpendicular(poleBody, armDirBody);

            if (polePerpendicular.LengthSquared() < DegenerateThreshold)
            {
                ReportError($"Pose '{pose.Name}' produced degenerate pole intent (pole aligned with arm).");
                return;
            }

            Vector3 poleIntent = polePerpendicular.Normalized();

            if (AuthoredSide == ArmSide.Left)
            {
                armDirBody = MirrorX(armDirBody);
                poleIntent = MirrorX(poleIntent);
            }

            bakedAnchors.Add(new ArmPoleAnchorResource
            {
                Name = pose.AnchorName,
                ArmDirBody = armDirBody,
                ReachRatio = reachRatio,
                PoleIntentBody = poleIntent
            });
        }

        if (bakedAnchors.Count == 0)
        {
            ReportError($"No valid ArmPoleAnchorAuthoringPose nodes found under '{poseContainer.GetPath()}'.");
            return;
        }

        if (!TryLoadOrCreateAnchorSet(OutputResourcePath, out ArmPoleAnchorSetResource anchorSet))
        {
            return;
        }

#pragma warning disable IDE0055
        anchorSet.Anchors = [..bakedAnchors];
#pragma warning restore IDE0055

        anchorSet.WeightEpsilonRadians = WeightEpsilonRadians;
        anchorSet.ReachWeight = ReachWeight;

        Error saveError = ResourceSaver.Save(anchorSet, OutputResourcePath);

        if (saveError != Error.Ok)
        {
            ReportError($"Failed to save '{OutputResourcePath}' (Error: {saveError}).");
            return;
        }

        GD.Print(
            $"{nameof(ArmPoleAnchorSetAuthoringRoot)} '{Name}': baked {bakedAnchors.Count} anchors " +
            $"to '{OutputResourcePath}' (AuthoredSide={AuthoredSide}, Epsilon={WeightEpsilonRadians:0.####}, ReachWeight={ReachWeight:0.####}).");
    }

    private bool TryResolveRequiredBones(Skeleton3D skeleton, ArmSide side, out BoneIndices bones)
    {
        bones = new BoneIndices
        {
            Hips = skeleton.FindBone("Hips"),
            Neck = skeleton.FindBone("Neck"),
            LeftShoulder = skeleton.FindBone("LeftShoulder"),
            RightShoulder = skeleton.FindBone("RightShoulder")
        };

        string sidePrefix = side == ArmSide.Left ? "Left" : "Right";

        bones.Shoulder = skeleton.FindBone($"{sidePrefix}Shoulder");
        bones.UpperArm = skeleton.FindBone($"{sidePrefix}UpperArm");
        bones.LowerArm = skeleton.FindBone($"{sidePrefix}LowerArm");
        bones.Hand = skeleton.FindBone($"{sidePrefix}Hand");

        if (bones.Hips < 0)
        {
            ReportError("Missing required bone 'Hips'.");
            return false;
        }

        if (bones.Neck < 0)
        {
            ReportError("Missing required bone 'Neck'.");
            return false;
        }

        if (bones.LeftShoulder < 0)
        {
            ReportError("Missing required bone 'LeftShoulder'.");
            return false;
        }

        if (bones.RightShoulder < 0)
        {
            ReportError("Missing required bone 'RightShoulder'.");
            return false;
        }

        if (bones.Shoulder < 0)
        {
            ReportError($"Missing required bone '{sidePrefix}Shoulder'.");
            return false;
        }

        if (bones.UpperArm < 0)
        {
            ReportError($"Missing required bone '{sidePrefix}UpperArm'.");
            return false;
        }

        if (bones.LowerArm < 0)
        {
            ReportError($"Missing required bone '{sidePrefix}LowerArm'.");
            return false;
        }

        if (bones.Hand < 0)
        {
            ReportError($"Missing required bone '{sidePrefix}Hand'.");
            return false;
        }

        return true;
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

    private static Vector3 ProjectPerpendicular(Vector3 vector, Vector3 normal) =>
        vector - (vector.Dot(normal) * normal);

    private static Vector3 MirrorX(Vector3 vector) =>
        new(-vector.X, vector.Y, vector.Z);

    private bool TryLoadOrCreateAnchorSet(string path, out ArmPoleAnchorSetResource anchorSet)
    {
        if (!ResourceLoader.Exists(path))
        {
            anchorSet = new ArmPoleAnchorSetResource();
            return true;
        }

        Resource? loadedResource = ResourceLoader.Load(path);

        if (loadedResource is null)
        {
            ReportError(
                $"OutputResourcePath '{path}' exists but could not be loaded. " +
                "Fix or remove the file, or choose a different ArmPoleAnchorSetResource .tres path.");
            anchorSet = null!;
            return false;
        }

        if (loadedResource is ArmPoleAnchorSetResource loadedAnchorSet)
        {
            anchorSet = loadedAnchorSet;
            return true;
        }

        ReportError(
            $"OutputResourcePath '{path}' loads as incompatible type '{loadedResource.GetType().FullName}' " +
            $"(Godot class '{loadedResource.GetClass()}'). " +
            "Bake aborted to avoid overwriting unrelated data. Choose a path to an ArmPoleAnchorSetResource .tres file.");

        anchorSet = null!;
        return false;
    }

    private void ReportError(string message) =>
        GD.PushError($"{nameof(ArmPoleAnchorSetAuthoringRoot)} '{Name}' ({GetPath()}): {message}");

    private sealed class BoneIndices
    {
        public int Hips
        {
            get;
            set;
        }

        public int Neck
        {
            get;
            set;
        }

        public int LeftShoulder
        {
            get;
            set;
        }

        public int RightShoulder
        {
            get;
            set;
        }

        public int Shoulder
        {
            get;
            set;
        }

        public int UpperArm
        {
            get;
            set;
        }

        public int LowerArm
        {
            get;
            set;
        }

        public int Hand
        {
            get;
            set;
        }
    }

    private static Vector3 BoneGlobalPosition(Skeleton3D skeleton, int boneIdx) =>
        skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(boneIdx).Origin;
}
