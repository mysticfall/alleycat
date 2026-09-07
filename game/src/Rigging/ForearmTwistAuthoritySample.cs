using Godot;

namespace AlleyCat.Rigging;

/// <summary>Identifies the system that currently owns a canonical hand pose.</summary>
public enum ForearmTwistAuthorityKind
{
    /// <summary>No finite canonical hand pose is available.</summary>
    None,

    /// <summary>The authored animation pose owns the hand.</summary>
    Animation,

    /// <summary>An IK target provider owns the hand.</summary>
    IKProvider,

    /// <summary>A grab override owns the hand.</summary>
    Grab,

    /// <summary>A retained optical wrist owns the hand while live tracking is lost.</summary>
    FrozenTracking,
}

/// <summary>
/// Atomic, rigging-owned hand-pose authority input for <see cref="ForearmTwistModifier" />.
/// </summary>
/// <remarks>
/// This boundary deliberately contains no IK types. The producer supplies a canonical skeleton-local hand pose,
/// its influence and ownership metadata together after hand/copy/animation work has completed.
/// </remarks>
public readonly record struct ForearmTwistAuthoritySample(
    Transform3D CanonicalHandTransform,
    float Influence,
    bool Ready,
    ForearmTwistAuthorityKind AuthorityKind,
    ulong SourceIdentity,
    ulong AuthorityEpoch,
    ulong ModificationPassToken)
{
    /// <summary>Gets whether this sample can safely drive a helper calculation.</summary>
    public bool IsUsable => Ready && IsFinite(CanonicalHandTransform) && IsFinite(Influence);

    /// <summary>Creates an intentionally unready sample for a modification pass.</summary>
    public static ForearmTwistAuthoritySample Unready(ulong modificationPassToken)
        => new(
            Transform3D.Identity,
            0.0f,
            Ready: false,
            ForearmTwistAuthorityKind.None,
            0,
            0,
            modificationPassToken);

    /// <summary>Returns whether a canonical transform has finite position and basis components.</summary>
    public static bool IsFinite(Transform3D transform)
        => IsFinite(transform.Origin)
           && IsFinite(transform.Basis.X)
           && IsFinite(transform.Basis.Y)
           && IsFinite(transform.Basis.Z);

    private static bool IsFinite(Vector3 value)
        => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

    private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
}

/// <summary>Tracks per-side authority ownership independently of forearm twist maths.</summary>
public sealed class ForearmTwistAuthorityTemporalState
{
    private bool _initialised;
    private ForearmTwistAuthorityKind _authorityKind;
    private ulong _sourceIdentity;
    private ulong _authorityEpoch;

    /// <summary>Clears the observed authority key.</summary>
    public void Reset()
    {
        _initialised = false;
        _authorityKind = ForearmTwistAuthorityKind.None;
        _sourceIdentity = 0;
        _authorityEpoch = 0;
    }

    /// <summary>
    /// Observes a ready sample and returns whether its authority starts a fresh temporal branch.
    /// </summary>
    public bool Observe(ForearmTwistAuthoritySample sample)
    {
        if (!sample.IsUsable)
        {
            Reset();
            return false;
        }

        bool changed = !_initialised
            || _authorityKind != sample.AuthorityKind
            || _sourceIdentity != sample.SourceIdentity
            || _authorityEpoch != sample.AuthorityEpoch;
        _initialised = true;
        _authorityKind = sample.AuthorityKind;
        _sourceIdentity = sample.SourceIdentity;
        _authorityEpoch = sample.AuthorityEpoch;
        return changed;
    }
}

/// <summary>Issues and consumes one opaque token for each skeleton-modifier traversal.</summary>
public static class ForearmTwistModificationPass
{
    private static readonly Dictionary<ulong, PassState> _states = [];

    /// <summary>Begins the authority-adapter portion of one Skeleton3D traversal.</summary>
    public static ulong Begin(Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ulong skeletonID = skeleton.GetInstanceId();
        if (!_states.TryGetValue(skeletonID, out PassState? state))
        {
            state = new PassState();
            _states.Add(skeletonID, state);
        }

        Array.Clear(state.ConsumedSides);
        return ++state.IssuedToken;
    }

    /// <summary>Consumes the exact token issued by the immediately preceding authority-adapter execution.</summary>
    public static bool TryConsume(Skeleton3D skeleton, ulong token, LimbSide side)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        return side is LimbSide.Left or LimbSide.Right
            && _states.TryGetValue(skeleton.GetInstanceId(), out PassState? state)
            && token != 0
            && state.IssuedToken == token
            && !state.ConsumedSides[(int)side]
            && Consume(state, side);
    }

    /// <summary>Clears token state when a skeleton binding is released.</summary>
    public static void Reset(Skeleton3D skeleton)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        _ = _states.Remove(skeleton.GetInstanceId());
    }

    private static bool Consume(PassState state, LimbSide side) => state.ConsumedSides[(int)side] = true;

    private sealed class PassState
    {
        public ulong IssuedToken
        {
            get; set;
        }

        public bool[] ConsumedSides { get; } = new bool[2];
    }
}
