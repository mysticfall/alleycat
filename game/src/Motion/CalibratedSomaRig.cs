using System.Text.Json;
using Godot;

namespace AlleyCat.Motion;

/// <summary>Calibrated helper rotations with the imported skin's presentation frames; feedback is explicitly approximate.</summary>
public sealed class CalibratedSomaRig
{
    private sealed record Mapping(int Bone, int Source, Transform3D Helper, Transform3D TargetRest, string Track, Basis PresentationRest);
    private readonly SomaMotionData _data;
    private readonly Skeleton3D _skeleton;
    private readonly Mapping[] _mapping;
    private readonly Transform3D[] _world;
    private readonly Transform3D[] _input;
    private readonly Basis[] _observed;
    private readonly bool[] _represented;
    private readonly Basis _axis = SomaMotionData.BlenderToModel;
    private readonly float _scale;
    /// <inheritdoc/>
    public int RepresentedCount => _mapping.Length;

    /// <inheritdoc/>
    public CalibratedSomaRig(SomaMotionData data, Skeleton3D skeleton, bool preserveFingerPoses = false)
    {
        _data = data;
        _skeleton = skeleton;
        _world = new Transform3D[data.Names.Length];
        _input = new Transform3D[data.Names.Length];
        _observed = new Basis[data.Names.Length];
        _represented = new bool[data.Names.Length];
        _scale = skeleton.MotionScale / 0.938770651817322f;
        Dictionary<string, Basis> presentationRests = ReadPresentationRests(skeleton);
        _mapping = [.. data.Calibration.GetProperty("mapping").EnumerateArray().Select(m => new Mapping(
            skeleton.FindBone(m.GetProperty("target").GetString()!), m.GetProperty("source").GetInt32(),
            SomaMotionData.ReadMatrix(m.GetProperty("helper")), SomaMotionData.ReadMatrix(m.GetProperty("targetRest")), m.GetProperty("track").GetString()!,
            presentationRests.TryGetValue(m.GetProperty("target").GetString()!, out Basis rest) ? rest
                : throw new InvalidDataException($"Missing imported skin bind for {m.GetProperty("target").GetString()}.")))
            .OrderBy(m => m.Bone)];
        if (_mapping.Any(m => m.Bone < 0))
        {
            throw new InvalidDataException("Calibrated MakeHuman target bone missing.");
        }
        foreach (Mapping m in _mapping)
        {
            _represented[m.Source] = true;
            if (preserveFingerPoses)
            {
                int left = skeleton.FindBone("LeftHand"), right = skeleton.FindBone("RightHand");
                for (int parent = skeleton.GetBoneParent(m.Bone); parent >= 0; parent = skeleton.GetBoneParent(parent))
                {
                    if (parent == left || parent == right)
                    {
                        _ = _preservedBones.Add(m.Bone);
                        break;
                    }
                }
            }
        }
    }

    private readonly HashSet<int> _preservedBones = [];

    private static Dictionary<string, Basis> ReadPresentationRests(Skeleton3D skeleton)
    {
        var rests = new Dictionary<string, Basis>();
        foreach (Node node in skeleton.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mesh = (MeshInstance3D)node;
            Skin? skin = mesh.Skin;
            if (skin is null || mesh.GetNodeOrNull<Skeleton3D>(mesh.Skeleton) != skeleton)
            {
                continue;
            }
            if (!(skeleton.GlobalTransform.AffineInverse() * mesh.GlobalTransform).IsEqualApprox(Transform3D.Identity))
            {
                throw new InvalidDataException("Calibrated SOMA playback requires meshes in the skeleton's coordinate frame.");
            }
            for (int i = 0; i < skin.GetBindCount(); i++)
            {
                string name = skin.GetBindName(i).ToString();
                if (name.Length == 0)
                {
                    name = skeleton.GetBoneName(skin.GetBindBone(i));
                }
                // Rest Fixer changes the skeleton silhouette, but inverse binds retain the original mesh
                // presentation in the new bone axes. Using Skeleton3D rest here would reapply that silhouette.
                Basis rest = skin.GetBindPose(i).AffineInverse().Basis.Orthonormalized();
                if (rests.TryGetValue(name, out Basis existing) && !existing.IsEqualApprox(rest))
                {
                    throw new InvalidDataException($"Conflicting imported skin presentation frames for {name}.");
                }
                rests[name] = rest;
            }
        }
        return rests;
    }

    /// <inheritdoc/>
    public void Apply(ReadOnlySpan<Transform3D> local)
    {
        local.CopyTo(_input);
        // Canonical world -> actual skeleton frame. Child transforms remain source-local.
        Basis intoSkeleton = _skeleton.GlobalBasis.Inverse() * SomaMotionData.ModelToWorld;
        _input[0] = new Transform3D(intoSkeleton * local[0].Basis,
            intoSkeleton * (local[0].Origin - (SomaMotionData.ModelToWorld.Inverse() * _skeleton.GlobalPosition)));
        _data.WorldPose(_input, _world);
        Vector3 ground = _input[0].Origin;
        ground.Y = 0;
        foreach (Mapping m in _mapping)
        {
            // Player controller-grab animation owns fingers; optical tracking may further modify them.
            // They remain fully observed below, even when generated base-pose playback leaves them alone.
            if (_preservedBones.Contains(m.Bone))
            {
                continue;
            }
            Transform3D helper = HelperPose(m);
            Basis desired = (_axis * helper.Basis * m.TargetRest.Basis.Inverse() * _axis.Inverse() * m.PresentationRest).Orthonormalized();
            int parent = _skeleton.GetBoneParent(m.Bone);
            Transform3D parentPose = parent >= 0 ? _skeleton.GetBoneGlobalPose(parent) : Transform3D.Identity;
            _skeleton.SetBonePoseRotation(m.Bone, (parentPose.Basis.Inverse() * desired).Orthonormalized().GetRotationQuaternion());
            Vector3 position = _skeleton.GetBoneRest(m.Bone).Origin;
            if (m.Source == 0)
            {
                Vector3 global = _skeleton.GetBoneGlobalRest(m.Bone).Origin + (((_axis * (helper.Origin - m.TargetRest.Origin)) - ground) * _scale);
                position = parentPose.AffineInverse() * global;
            }
            _skeleton.SetBonePosePosition(m.Bone, position);
        }
    }

    private Transform3D HelperPose(Mapping m)
    {
        Transform3D source = _world[m.Source];
        Transform3D rest = _data.BlenderRest[m.Source];
        var physical = new Transform3D(_axis.Inverse() * source.Basis * _axis, _axis.Inverse() * source.Origin);
        // P_s * inverse(R_s) * R_h * B_h, expressed without Blender bone-axis rotations.
        var delta = new Transform3D(physical.Basis, physical.Origin - (physical.Basis * rest.Origin));
        Transform3D helper = delta * m.Helper;
        if (m.Track.Length == 0)
        {
            return helper;
        }
        JsonElement look = _data.Calibration.GetProperty("looks").GetProperty(m.Track);
        int sourceIndex = look.GetProperty("source").GetInt32();
        Transform3D lookSource = _world[sourceIndex];
        var blenderSource = new Transform3D(_axis.Inverse() * lookSource.Basis * _axis * _data.BlenderRest[sourceIndex].Basis, _axis.Inverse() * lookSource.Origin);
        Transform3D lookPose = blenderSource * SomaMotionData.ReadMatrix(look.GetProperty("offset"));
        Vector3 direction = (lookPose * new Vector3(0, look.GetProperty("length").GetSingle(), 0)) - helper.Origin;
        Vector3 y = helper.Basis.Y.Normalized();
        Vector3 z = -(direction - (y * direction.Dot(y))).Normalized();
        if (z.LengthSquared() > 0.5f)
        {
            helper.Basis = new Basis(y.Cross(z).Normalized(), y, z);
        }
        return helper;
    }

    /// <summary>Read only at skeleton_updated. Never reads the last predicted pose.</summary>
    public void Observe(Span<Transform3D> local)
    {
        foreach (Mapping m in _mapping)
        {
            // Skin-ready, post-modifier orientations. Fixed-offset inversion for constrained
            // upperarms intentionally picks a deterministic representative, not an exact inverse.
            Basis target = _skeleton.GetBoneGlobalPose(m.Bone).Basis;
            _observed[m.Source] = (_axis.Inverse() * target * m.PresentationRest.Inverse() * _axis * m.TargetRest.Basis * m.Helper.Basis.Inverse()).Orthonormalized();
            _observed[m.Source] = (_axis * _observed[m.Source] * _axis.Inverse()).Orthonormalized();
        }
        for (int j = 0; j < local.Length; j++)
        {
            int parent = _data.Parents[j];
            // Jaw/eyes/Neck2 and all unrepresented finger/end channels keep source-rest
            // local rotation/offset, inheriting the observed parent through source FK.
            Basis global = _represented[j] ? _observed[j] : parent < 0 ? Basis.Identity : _world[parent].Basis;
            Basis rotation = parent < 0 ? global : _world[parent].Basis.Inverse() * global;
            Vector3 origin = _data.Rest[j].Origin;
            if (j == 0)
            {
                Mapping hips = _mapping.Single(m => m.Source == 0);
                Vector3 target = _skeleton.GetBoneGlobalPose(hips.Bone).Origin;
                Vector3 helperOffset = _axis * (hips.Helper.Origin - _data.BlenderRest[0].Origin);
                Vector3 sourcePosition = ((target - _skeleton.GetBoneGlobalRest(hips.Bone).Origin) / _scale)
                    + (_axis * hips.TargetRest.Origin) - (global * helperOffset);
                // Actual world actor translation after collision, not desired root travel.
                origin = SomaMotionData.ModelToWorld.Inverse() * (_skeleton.GlobalTransform * sourcePosition);
            }
            local[j] = new Transform3D(rotation.Orthonormalized(), origin);
            _world[j] = parent < 0 ? local[j] : _world[parent] * local[j];
        }
        local[0].Basis = (SomaMotionData.ModelToWorld.Inverse() * _skeleton.GlobalBasis * local[0].Basis).Orthonormalized();
    }
}
