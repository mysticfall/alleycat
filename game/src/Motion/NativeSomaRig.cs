using Godot;

namespace AlleyCat.Motion;

/// <summary>Writes absolute SOMA samples and observes final target rotations through both bind frames.</summary>
public sealed class NativeSomaRig
{
    private readonly SomaMotionData _data;
    private readonly Skeleton3D _target;
    private readonly Skeleton3D _source;
    private readonly RetargetModifier3D _modifier;
    private readonly record struct Channel(int Source, int Target, Basis Left, Basis Right);
    private readonly Channel[] _channels;

    /// <summary>Number of canonical rotations observable on the target; independent of forward ownership.</summary>
    public int RepresentedCount => _channels.Length;

    /// <summary>Bind only an installed native target, never a guessed skeleton in the actor subtree.</summary>
    public NativeSomaRig(SomaMotionData data, Skeleton3D skeleton)
    {
        _data = data;
        _target = skeleton;
        _modifier = skeleton.GetParent() as RetargetModifier3D ?? throw new InvalidDataException("Missing native target parent.");
        _source = _modifier.GetParent<Skeleton3D>();
        if (_source.GetBoneCount() != 77 || data.Rest.Length != 77)
        {
            throw new InvalidDataException("Expected the canonical 77-joint SOMA layout.");
        }
        List<Channel> channels = [];
        for (int i = 0; i < data.Rest.Length; i++)
        {
            int sp = _source.GetBoneParent(i);
            if (sp != data.Parents[i])
            {
                throw new InvalidDataException($"Source hierarchy mismatch for {data.Names[i]}.");
            }
            int b = skeleton.FindBone(_source.GetBoneName(i));
            if (b < 0)
            {
                continue;
            }
            int tp = skeleton.GetBoneParent(b);
            Basis s = sp < 0 ? Basis.Identity : _source.GetBoneGlobalRest(sp).Basis;
            Basis t = tp < 0 ? Basis.Identity : skeleton.GetBoneGlobalRest(tp).Basis;
            channels.Add(new Channel(i, b, s.Inverse() * t, skeleton.GetBoneGlobalRest(b).Basis.Inverse() * _source.GetBoneGlobalRest(i).Basis));
        }
        _channels = [.. channels];
        if (_channels.Length != 52 || _channels[0].Source != 0)
        {
            throw new InvalidDataException($"Expected 52 observable SOMA joints, found {_channels.Length}.");
        }
    }

    /// <summary>Relinquish the body; merely holding the previous source pose still drives the target.</summary>
    public void Relinquish() => _modifier.Active = false;

    /// <summary>Supply the canonical world root in source space; planar travel belongs to the actor.</summary>
    public void Apply(ReadOnlySpan<Transform3D> local) => Apply(local, Vector3.Zero);

    internal void Apply(ReadOnlySpan<Transform3D> local, Vector3 sourceLocalHips)
    {
        Transform3D root = _source.GlobalTransform.AffineInverse() * new Transform3D(SomaMotionData.ModelToWorld, Vector3.Zero) * local[0];
        root.Origin = new Vector3(sourceLocalHips.X, root.Origin.Y, sourceLocalHips.Z);
        _modifier.Active = true;
        _source.SetBonePose(0, root);
        for (int i = 1; i < local.Length; i++)
        {
            _source.SetBonePose(i, local[i]);
        }
    }

    // At mixer_applied, invert the native local position/rotation transfer for the tree's base
    // (not the final solved target). Downstream tracking will run once on either owner.
    internal Transform3D ReadTreeDestination(Span<Transform3D> local)
    {
        _data.Rest.AsSpan().CopyTo(local);
        float scale = _source.MotionScale / _target.MotionScale;
        foreach (Channel c in _channels)
        {
            Transform3D pose = _target.GetBonePose(c.Target);
            Vector3 offset = c.Left * (pose.Origin - _target.GetBoneRest(c.Target).Origin) * scale;
            local[c.Source] = new Transform3D((c.Left * pose.Basis * c.Right).Orthonormalized(),
                _source.GetBoneRest(c.Source).Origin + offset);
        }
        Transform3D sourceToModel = new Transform3D(SomaMotionData.ModelToWorld.Inverse(), Vector3.Zero) * _source.GlobalTransform;
        local[0] = sourceToModel * local[0];
        return sourceToModel.AffineInverse();
    }

    // The supplied root may contain travel already consumed by the collider. Read the actual
    // source root in its current frame rather than treating such demand as articulation.
    internal Transform3D PresentedRoot() => new Transform3D(SomaMotionData.ModelToWorld.Inverse(), Vector3.Zero)
        * _source.GlobalTransform * _source.GetBonePose(0);

    /// <summary>Call on target skeleton_updated; missing channels retain canonical local rest.</summary>
    public void Observe(Span<Transform3D> local)
    {
        _data.Rest.AsSpan().CopyTo(local);
        foreach (Channel c in _channels)
        {
            local[c.Source].Basis = (c.Left * _target.GetBonePose(c.Target).Basis * c.Right).Orthonormalized();
        }
        int hip = _channels[0].Target;
        Vector3 observed = _target.GetBoneGlobalPose(hip).Origin - _target.GetBoneGlobalRest(hip).Origin;
        Vector3 pos = _source.GetBoneGlobalRest(0).Origin + (observed * (_source.MotionScale / _target.MotionScale));
        Basis model = SomaMotionData.ModelToWorld.Inverse();
        local[0].Origin = model * (_target.GlobalTransform * pos);
        local[0].Basis = model * _target.GlobalBasis * local[0].Basis;
    }
}
