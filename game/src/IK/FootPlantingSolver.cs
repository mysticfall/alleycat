using Godot;

namespace AlleyCat.IK;

/// <summary>Pin phase of one foot under IK-003 foot planting.</summary>
public enum FootPlantingPhase
{
    /// <summary>The foot follows its animated target.</summary>
    Free,

    /// <summary>The ball point is held at its captured planar pin.</summary>
    Pinned,

    /// <summary>The pin is blending back to the animated target.</summary>
    BlendingOut,
}

/// <summary>One tick's input to <see cref="FootPlantingSolver" />.</summary>
/// <param name="Planted">ANIM-002 planted state of this foot on this tick under the Planted Combination Rule.</param>
/// <param name="AnimatedFoot">Same-tick animated world transform of the foot bone.</param>
/// <param name="BallInFoot">Ball joint in foot-local space: the toe bone's rest-pose origin.</param>
/// <param name="LegRoot">Same-tick world position of the upper-leg joint.</param>
/// <param name="ChainLength">Rest length of the upper-leg → lower-leg → foot chain.</param>
/// <param name="DeltaSeconds">Time since the previous step.</param>
/// <param name="SkeletonOrigin">Same-tick world origin of the skeleton, used to detect a teleport between steps.</param>
public readonly record struct FootPlantingInput(
    bool Planted,
    Transform3D AnimatedFoot,
    Vector3 BallInFoot,
    Vector3 LegRoot,
    float ChainLength,
    double DeltaSeconds,
    Vector3 SkeletonOrigin = default);

/// <summary>One tick's output of <see cref="FootPlantingSolver" />.</summary>
/// <param name="Target">World foot target with its ball pinned and its world heading held.</param>
/// <param name="AnimatedBall">World ball point of the animated foot.</param>
/// <param name="TargetBall">World ball point of <paramref name="Target" />.</param>
/// <param name="Phase">Pin phase after this step.</param>
/// <param name="Pin">Planar world pin while pinned; otherwise the last pin.</param>
/// <param name="ReachUsage">Upper-leg-to-target distance over the chain rest length.</param>
/// <param name="ReachBlendOutStarted">Whether this step released the pin because it left the leg's reach.</param>
/// <param name="ContinuityReset">
/// Whether this step first forgot the pin because the gap since the previous step or the skeleton's travel since then
/// was too large to continue it.
/// </param>
/// <param name="CapturedHeading">Acquired anatomical world heading in radians, or NaN if degenerate.</param>
/// <param name="AnimatedHeading">Current anatomical world heading in radians, or NaN if degenerate.</param>
/// <param name="TargetHeading">Requested anatomical world heading in radians, or NaN if degenerate.</param>
public readonly record struct FootPlantingOutput(
    Transform3D Target,
    Vector3 AnimatedBall,
    Vector3 TargetBall,
    FootPlantingPhase Phase,
    Vector3 Pin,
    float ReachUsage,
    bool ReachBlendOutStarted,
    bool ContinuityReset,
    float CapturedHeading = float.NaN,
    float AnimatedHeading = float.NaN,
    float TargetHeading = float.NaN);

/// <summary>
/// Per-foot IK-003 foot-planting state machine. It is value-only: it produces a foot target from the same-tick
/// animated foot and never touches the actor or the skeleton.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Interval start captures the current output ball point's planar position as the pin, so there is no jump; an
/// interval without a preceding swing therefore pins at the current animated position.</item>
/// <item>While pinned, the ball's horizontal position and the anatomical world heading are held; animated ball height,
/// tilt and heel roll continue, with rotation compensated about the ball rather than the ankle.</item>
/// <item>Interval end blends both planar and heading offsets to zero over <see cref="BlendOutSeconds" /> with a
/// smoothstep, ending exactly on the current animated target.</item>
/// <item>A pin that would extend the leg beyond <c>max(chain length − reach margin, animated distance)</c> blends out
/// from the reachable boundary, and is not recaptured until the next planted interval.</item>
/// <item>Short gaps between steps continue the pin over the elapsed time. A gap longer than
/// <see cref="MaxContinuationGapSeconds" />, or skeleton travel since the previous step beyond the chain length (a
/// teleport), resets the solver first, as <see cref="Reset" /> does, so a planted foot pins at its current animated
/// position instead of popping from a stale pin clipped to reach.</item>
/// </list>
/// </remarks>
public sealed class FootPlantingSolver
{
    private Vector3 _pin;
    private Vector3 _offset;
    private Vector3 _blendStartOffset;
    private float _capturedHeading = float.NaN;
    private float _yawOffset;
    private float _blendStartYawOffset;
    private double _blendElapsed;
    private bool _previousPlanted;
    private bool _hasHistory;
    private Vector3 _lastSkeletonOrigin;

    /// <summary>Duration of the blend from a released pin back to the animated target.</summary>
    public double BlendOutSeconds { get; set; } = 0.15d;

    /// <summary>Distance short of full chain extension beyond which a pin is out of reach.</summary>
    public float ReachMarginMetres
    {
        get; set;
    }

    /// <summary>Longest gap between steps over which the pin state is continued rather than reset.</summary>
    public double MaxContinuationGapSeconds { get; set; } = 0.25d;

    /// <summary>Current pin phase.</summary>
    public FootPlantingPhase Phase
    {
        get; private set;
    }

    /// <summary>Forgets the pin and interval history, as on spawn or teleport.</summary>
    public void Reset()
    {
        Phase = FootPlantingPhase.Free;
        _pin = Vector3.Zero;
        _offset = Vector3.Zero;
        _blendStartOffset = Vector3.Zero;
        _capturedHeading = float.NaN;
        _yawOffset = 0f;
        _blendStartYawOffset = 0f;
        _blendElapsed = 0d;
        _previousPlanted = false;
        _hasHistory = false;
        _lastSkeletonOrigin = Vector3.Zero;
    }

    /// <summary>
    /// Returns what <see cref="Step" /> would output for <paramref name="input" /> without advancing any state.
    /// </summary>
    public FootPlantingOutput Preview(in FootPlantingInput input)
    {
        (FootPlantingPhase phase, Vector3 pin, Vector3 offset, Vector3 blendStart, double elapsed, bool planted) =
            (Phase, _pin, _offset, _blendStartOffset, _blendElapsed, _previousPlanted);
        (bool history, Vector3 skeletonOrigin) = (_hasHistory, _lastSkeletonOrigin);
        (float capturedHeading, float yawOffset, float blendStartYawOffset) =
            (_capturedHeading, _yawOffset, _blendStartYawOffset);
        FootPlantingOutput output = Step(input);
        (Phase, _pin, _offset, _blendStartOffset, _blendElapsed, _previousPlanted) =
            (phase, pin, offset, blendStart, elapsed, planted);
        (_hasHistory, _lastSkeletonOrigin) = (history, skeletonOrigin);
        (_capturedHeading, _yawOffset, _blendStartYawOffset) = (capturedHeading, yawOffset, blendStartYawOffset);
        return output;
    }

    /// <summary>Advances one tick.</summary>
    public FootPlantingOutput Step(in FootPlantingInput input)
    {
        Vector3 animatedBall = input.AnimatedFoot * input.BallInFoot;
        float animatedHeading = Heading(input.AnimatedFoot.Basis * input.BallInFoot);
        bool reachBlendOut = false;
        double delta = Math.Max(input.DeltaSeconds, 0d);
        bool continuityReset = _hasHistory
            && (delta > MaxContinuationGapSeconds
                || input.SkeletonOrigin.DistanceTo(_lastSkeletonOrigin) > input.ChainLength);
        if (continuityReset)
        {
            Reset();
            delta = 0d;
        }

        _hasHistory = true;
        _lastSkeletonOrigin = input.SkeletonOrigin;

        if (!input.Planted)
        {
            if (Phase == FootPlantingPhase.Pinned)
            {
                BeginBlendOut();
            }
        }
        else if (!_previousPlanted)
        {
            // Interval start: pin the ball where it is currently output, including any unfinished blend-out. Only a
            // swing-to-planted edge captures, so a pin released within an interval is not recaptured in it.
            // An interrupted release captures the effective target (including its yaw-induced ball displacement),
            // not the unmodified animated pose or the previous tick's ankle position.
            Basis effectiveBasis = input.AnimatedFoot.Basis.Rotated(Vector3.Up, _yawOffset);
            _pin = Planar(animatedBall + _offset);
            _capturedHeading = Heading(effectiveBasis * input.BallInFoot);
            Phase = FootPlantingPhase.Pinned;
        }

        _previousPlanted = input.Planted;

        switch (Phase)
        {
            case FootPlantingPhase.Pinned:
                _offset = _pin - Planar(animatedBall);
                // While pinned, an undefined horizontal anatomical projection applies no yaw correction;
                // the planted metadata and captured finite heading remain unchanged. If acquisition itself
                // was undefined, the first finite projection establishes the heading. An interrupted temporal
                // release may still carry its existing decaying yaw until metadata allows reacquisition.
                if (float.IsNaN(_capturedHeading) && float.IsFinite(animatedHeading))
                {
                    _capturedHeading = animatedHeading;
                }

                _yawOffset = float.IsFinite(animatedHeading) && float.IsFinite(_capturedHeading)
                    ? Mathf.AngleDifference(animatedHeading, _capturedHeading)
                    : 0f;
                break;
            case FootPlantingPhase.BlendingOut:
                _blendElapsed += delta;
                double progress = BlendOutSeconds > 0d ? Math.Clamp(_blendElapsed / BlendOutSeconds, 0d, 1d) : 1d;
                if (progress >= 1d - 1e-9d)
                {
                    Phase = FootPlantingPhase.Free;
                    _offset = Vector3.Zero;
                    _yawOffset = 0f;
                }
                else
                {
                    double eased = progress * progress * (3d - (2d * progress));
                    _offset = _blendStartOffset * (float)(1d - eased);
                    _yawOffset = _blendStartYawOffset * (float)(1d - eased);
                }

                break;
            case FootPlantingPhase.Free:
            default:
                _offset = Vector3.Zero;
                _yawOffset = 0f;
                break;
        }

        Vector3 ankle = input.AnimatedFoot.Origin;
        float animatedDistance = ankle.DistanceTo(input.LegRoot);
        float limit = Math.Max(input.ChainLength - ReachMarginMetres, animatedDistance);
        Transform3D target = MakeTarget(input.AnimatedFoot, input.BallInFoot, animatedBall, _offset, _yawOffset);
        if (target.Origin.DistanceTo(input.LegRoot) > limit + 0.000001f)
        {
            // Both the planar pin offset AND the rotation about the ball move the ankle. Search along their
            // combined release path, rather than clipping an ankle translation calculated before yaw compensation.
            float low = 0f;
            float high = 1f;
            for (int iteration = 0; iteration < 20; iteration++)
            {
                float middle = (low + high) * 0.5f;
                Transform3D candidate = MakeTarget(input.AnimatedFoot, input.BallInFoot, animatedBall,
                    _offset * middle, _yawOffset * middle);
                if (candidate.Origin.DistanceTo(input.LegRoot) <= limit)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            _offset *= low;
            _yawOffset *= low;
            target = MakeTarget(input.AnimatedFoot, input.BallInFoot, animatedBall, _offset, _yawOffset);
            if (Phase == FootPlantingPhase.Pinned)
            {
                reachBlendOut = true;
                BeginBlendOut();
            }
        }

        float reachUsage = input.ChainLength > 0f ? target.Origin.DistanceTo(input.LegRoot) / input.ChainLength : 0f;
        return new FootPlantingOutput(
            target,
            animatedBall,
            target * input.BallInFoot,
            Phase,
            _pin,
            reachUsage,
            reachBlendOut,
            continuityReset,
            _capturedHeading,
            animatedHeading,
            Heading(target.Basis * input.BallInFoot));
    }

    private void BeginBlendOut()
    {
        Phase = FootPlantingPhase.BlendingOut;
        _blendStartOffset = _offset;
        _blendStartYawOffset = _yawOffset;
        _blendElapsed = 0d;
    }

    private static Transform3D MakeTarget(Transform3D animated, Vector3 ballInFoot, Vector3 animatedBall,
        Vector3 planarOffset, float yawOffset)
    {
        Basis basis = animated.Basis.Rotated(Vector3.Up, yawOffset);
        Vector3 ball = animatedBall + planarOffset;
        return new Transform3D(basis, ball - (basis * ballInFoot));
    }

    private static float Heading(Vector3 direction)
    {
        Vector3 planar = Planar(direction);
        return planar.LengthSquared() < 1e-10f ? float.NaN : Mathf.Atan2(-planar.X, -planar.Z);
    }

    private static Vector3 Planar(Vector3 value) => new(value.X, 0f, value.Z);
}
