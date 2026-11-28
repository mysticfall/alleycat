using Godot;

namespace AlleyCat.Animations;

/// <summary>
/// The native-body writer for catalogue idles (ANIM-003 TR13, TR19). It is the first modifier of the native reference
/// skeleton, so it runs after every animation mixer and before installed IK, which keep their order after it.
/// <list type="bullet">
/// <item><see cref="NativeIdleMode.Idle" />: writes every keyed bone from the idle clip, replacing whatever a mixer
/// applied this tick. The MotionBricks retargeter must be inactive meanwhile.</item>
/// <item><see cref="NativeIdleMode.Bridge" />: while a MotionBricks bridge presents SOMA-mapped bones through the
/// retargeter, blends the bones outside the SOMA map from their last presented values to the target values.</item>
/// <item><see cref="NativeIdleMode.Off" />: performs no writes.</item>
/// </list>
/// </summary>
public partial class NativeIdleModifier : SkeletonModifier3D, IBasePoseModifier
{
    private bool[] _mapped = [];
    private Transform3D[] _scratch = [], _last = [], _start = [], _target = [];
    private bool[] _wrote = [];
    private int[] _bridgeBones = [];
    private int _bridgeCount;
    private bool _captureStart, _liveTarget;

    /// <summary>The current write mode.</summary>
    public NativeIdleMode Mode
    {
        get; private set;
    }

    /// <summary>The idle clip written in <see cref="NativeIdleMode.Idle" />.</summary>
    public NativeIdleClip? Clip
    {
        get; private set;
    }

    /// <summary>Clip playback time in seconds, set by the owner before each tick.</summary>
    public double Time
    {
        get; set;
    }

    /// <summary>Through-bridge weight of bones outside the SOMA map, set by the owner before each tick.</summary>
    public float BridgeWeight
    {
        get; set;
    }

    /// <summary>Number of modification passes that wrote bones; diagnostic only.</summary>
    public long WritingPasses
    {
        get; private set;
    }

    /// <summary>Raised at the start of each modification pass, while bones still hold the mixer output; diagnostic only.</summary>
    internal event Action<NativeIdleModifier>? Processing;

    /// <summary>Raised after each modification pass, before later modifiers run; diagnostic only.</summary>
    internal event Action<NativeIdleModifier>? Processed;

    /// <summary>Binds the skeleton layout. <paramref name="mapped" /> marks bones the retargeter writes.</summary>
    public void Bind(Skeleton3D skeleton, IEnumerable<int> mapped)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(mapped);
        int count = skeleton.GetBoneCount();
        _mapped = new bool[count];
        foreach (int bone in mapped)
        {
            _mapped[bone] = true;
        }

        _scratch = new Transform3D[count];
        _last = new Transform3D[count];
        _start = new Transform3D[count];
        _target = new Transform3D[count];
        _wrote = new bool[count];
        _bridgeBones = new int[count];
    }

    /// <summary>Whether a bone is written by the MotionBricks retargeter rather than by this modifier in a bridge.</summary>
    public bool IsMapped(int bone) => _mapped[bone];

    /// <summary>Plays an idle clip on every keyed bone.</summary>
    public void PlayIdle(NativeIdleClip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Clip = clip;
        Time = time;
        Mode = NativeIdleMode.Idle;
    }

    /// <summary>
    /// Starts a bridge blend of unmapped bones. Start values are the last values this modifier presented, or the
    /// incoming pose for bones it did not write. Targets are the idle clip's frame 0, or the incoming pose when the
    /// bridge leads into locomotion (<paramref name="target" /> is <see langword="null" />).
    /// </summary>
    public void BeginBridge(NativeIdleClip? source, NativeIdleClip? target)
    {
        _bridgeCount = 0;
        for (int bone = 0; bone < _mapped.Length; bone++)
        {
            if (!_mapped[bone] && ((source?.Keys(bone) ?? false) || (target?.Keys(bone) ?? false) || _wrote[bone]))
            {
                _bridgeBones[_bridgeCount++] = bone;
            }
        }

        _liveTarget = target is null;
        if (target is not null)
        {
            Skeleton3D? skeleton = GetSkeleton();
            for (int bone = 0; bone < _target.Length; bone++)
            {
                _target[bone] = skeleton?.GetBonePose(bone) ?? Transform3D.Identity;
            }

            target.Sample(0, _target);
        }

        _captureStart = true;
        BridgeWeight = 0;
        Mode = NativeIdleMode.Bridge;
    }

    /// <summary>Stops all writes; the retargeter and mixers own the body.</summary>
    public void Stop()
    {
        Mode = NativeIdleMode.Off;
        Array.Clear(_wrote);
    }

    /// <inheritdoc />
    public override void _ProcessModificationWithDelta(double delta)
    {
        Skeleton3D? skeleton = GetSkeleton();
        if (skeleton is null || _mapped.Length != skeleton.GetBoneCount())
        {
            return;
        }

        Processing?.Invoke(this);

        if (Mode == NativeIdleMode.Idle && Clip is not null)
        {
            foreach (int bone in Clip.KeyedBones)
            {
                _scratch[bone] = skeleton.GetBonePose(bone);
            }

            Clip.Sample(Time, _scratch);
            foreach (int bone in Clip.KeyedBones)
            {
                Write(skeleton, bone, _scratch[bone]);
            }

            WritingPasses++;
        }
        else if (Mode == NativeIdleMode.Bridge)
        {
            float weight = Math.Clamp(BridgeWeight, 0, 1);
            for (int i = 0; i < _bridgeCount; i++)
            {
                int bone = _bridgeBones[i];
                Transform3D incoming = skeleton.GetBonePose(bone);
                if (_captureStart)
                {
                    _start[bone] = _wrote[bone] ? _last[bone] : incoming;
                }

                Transform3D target = _liveTarget ? incoming : _target[bone];
                Write(skeleton, bone, Blend(_start[bone], target, weight));
            }

            _captureStart = false;
            WritingPasses++;
        }

        Processed?.Invoke(this);
    }

    private void Write(Skeleton3D skeleton, int bone, Transform3D pose)
    {
        skeleton.SetBonePosePosition(bone, pose.Origin);
        skeleton.SetBonePoseRotation(bone, pose.Basis.GetRotationQuaternion());
        _last[bone] = pose;
        _wrote[bone] = true;
    }

    private static Transform3D Blend(Transform3D a, Transform3D b, float weight) => new(
        new Basis(a.Basis.GetRotationQuaternion().Slerp(b.Basis.GetRotationQuaternion(), weight).Normalized()),
        a.Origin.Lerp(b.Origin, weight));
}

/// <summary>Write modes of <see cref="NativeIdleModifier" />.</summary>
public enum NativeIdleMode
{
    /// <summary>No writes.</summary>
    Off,

    /// <summary>The idle clip owns every keyed bone.</summary>
    Idle,

    /// <summary>Bones outside the SOMA map blend through a MotionBricks bridge.</summary>
    Bridge
}
