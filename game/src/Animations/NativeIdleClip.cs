using Godot;

namespace AlleyCat.Animations;

/// <summary>
/// A catalogue idle clip bound to one native reference skeleton (ANIM-003 TR13). Samples the clip's bone tracks
/// directly as native local poses; no AnimationPlayer, AnimationTree or runtime retargeting is involved.
/// </summary>
public sealed class NativeIdleClip
{
    private readonly record struct Track(int Index, int Bone, bool Position);

    private readonly Track[] _tracks;
    private readonly bool[] _keyed;
    private readonly Animation _animation;
    private readonly float _motionScale;

    /// <summary>Binds an idle entry's clip to a skeleton. Tracks on absent bones are safe no-ops (TR11).</summary>
    public NativeIdleClip(AnimationEntry entry, Animation clip, Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(skeleton);
        Entry = entry;
        _animation = clip;
        // Godot scales position tracks by the skeleton's motion scale on playback; direct sampling must match.
        _motionScale = skeleton.MotionScale;
        Length = clip.Length;
        Loops = clip.LoopMode != Animation.LoopModeEnum.None;
        _keyed = new bool[skeleton.GetBoneCount()];
        List<Track> tracks = [];
        for (int i = 0; i < clip.GetTrackCount(); i++)
        {
            Animation.TrackType type = clip.TrackGetType(i);
            if (type is not (Animation.TrackType.Position3D or Animation.TrackType.Rotation3D) || !clip.TrackIsEnabled(i))
            {
                continue;
            }

            NodePath path = clip.TrackGetPath(i);
            if (path.GetSubNameCount() == 0)
            {
                continue;
            }

            int bone = skeleton.FindBone(path.GetSubName(0));
            if (bone < 0)
            {
                continue;
            }

            tracks.Add(new Track(i, bone, type == Animation.TrackType.Position3D));
            _keyed[bone] = true;
        }

        _tracks = [.. tracks];
        KeyedBones = [.. Enumerable.Range(0, _keyed.Length).Where(b => _keyed[b])];
    }

    /// <summary>The catalogue entry this clip plays.</summary>
    public AnimationEntry Entry
    {
        get;
    }

    /// <summary>Clip length in seconds.</summary>
    public double Length
    {
        get;
    }

    /// <summary>Whether playback wraps at the clip end.</summary>
    public bool Loops
    {
        get;
    }

    /// <summary>Skeleton bones keyed by at least one position or rotation track, in bone order.</summary>
    public IReadOnlyList<int> KeyedBones
    {
        get;
    }

    /// <summary>Whether a bone is keyed by the clip.</summary>
    public bool Keys(int bone) => bone >= 0 && bone < _keyed.Length && _keyed[bone];

    /// <summary>Wraps or clamps a playback time into the clip.</summary>
    public double Wrap(double time)
    {
        if (Length <= 0)
        {
            return 0;
        }

        if (!Loops)
        {
            return Math.Clamp(time, 0, Length);
        }

        double wrapped = time % Length;
        return wrapped < 0 ? wrapped + Length : wrapped;
    }

    /// <summary>
    /// Writes the clip's local position and rotation at a time into the keyed bones of a native local pose buffer.
    /// Unkeyed bones, and unkeyed channels of keyed bones, keep the caller-supplied values.
    /// </summary>
    public void Sample(double time, Span<Transform3D> local)
    {
        double at = Wrap(time);
        foreach (Track track in _tracks)
        {
            Transform3D pose = local[track.Bone];
            if (track.Position)
            {
                pose.Origin = _animation.PositionTrackInterpolate(track.Index, at) * _motionScale;
            }
            else
            {
                pose.Basis = new Basis(_animation.RotationTrackInterpolate(track.Index, at).Normalized());
            }

            local[track.Bone] = pose;
        }
    }
}
