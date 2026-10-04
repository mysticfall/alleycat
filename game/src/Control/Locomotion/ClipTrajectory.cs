using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Root displacement and yaw change of one clip between two source times, expressed in the root frame at the start
/// time, in runtime scale.
/// </summary>
/// <param name="Planar">Actor-semantic planar displacement <c>[right, forward]</c> in metres.</param>
/// <param name="Yaw">Signed yaw change in radians; positive turns left.</param>
public readonly record struct ClipTrajectorySlice(Vector2 Planar, float Yaw);

/// <summary>
/// Declared interpolation error bounds of a <see cref="ClipTrajectory"/>: the largest difference between a table
/// query at any time and the evaluated clip at that time.
/// </summary>
/// <param name="SourceRootPlanar">
/// Root planar position bound in source-unit metres, against the embedded root track; consumers multiply it by the
/// bound skeleton's <see cref="Skeleton3D.MotionScale"/>, as they do the positions.
/// </param>
/// <param name="Yaw">Root yaw bound in radians; unscaled.</param>
/// <param name="Ball">
/// Ball-joint position bound in the bound rig's runtime metres, against the evaluated post-FK ball positions.
/// </param>
public readonly record struct ClipTrajectoryErrorBounds(float SourceRootPlanar, float Yaw, float Ball)
{
    /// <summary>Whether every bound is finite and non-negative.</summary>
    public bool IsValid => IsBound(SourceRootPlanar) && IsBound(Yaw) && IsBound(Ball);

    private static bool IsBound(float value) => float.IsFinite(value) && value >= 0f;
}

/// <summary>
/// Dense root trajectory and per-foot ball-joint samples of one locomotion clip, sampled at the clip's native key
/// rate.
/// </summary>
/// <remarks>
/// <para>
/// Root planar samples are stored in source units, exactly as the clip's root position track authors them, and every
/// root query returns runtime scale: planar positions are multiplied by <see cref="MotionScale"/>, the bound
/// <see cref="Skeleton3D.MotionScale"/> that Godot applies to root motion (ANIM-002 scale rule). Yaw is unscaled. A
/// package library is shared across rigs with different motion scales, so the root data of one table can be re-bound
/// through <see cref="WithMotionScale"/>.
/// </para>
/// <para>
/// Ball-joint samples are rig-specific runtime data (NAV-001 Technical Requirement 7): they are evaluated on the bound
/// skeleton, stored in that rig's runtime metres, tagged with the <see cref="BallMotionScale"/> they were evaluated at,
/// and returned unscaled. They are never derived by scaling source units, because Godot scales position tracks but
/// not bone rest offsets. Re-binding the root data does not change them; a <see cref="ClipTrajectoryTable"/> rejects
/// ball data evaluated at a scale other than its own.
/// </para>
/// <para>
/// Sample <c>i</c> is at source time <c>min(i / KeyRateHz, DurationSeconds)</c>; queries between samples interpolate
/// linearly, within the declared <see cref="ErrorBounds"/>. Query times are clamped to <c>[0, DurationSeconds]</c>;
/// looping callers wrap time themselves.
/// </para>
/// <para>
/// Frames: planar root positions are actor-semantic <c>[right, forward]</c> in the clip-start frame, relative to the
/// first sample. Yaw is relative to the first sample. Ball positions are relative to the Root bone, in the Root
/// bone's own (yawed) frame, using actor-local Godot axes: +X right, +Y up, -Z forward.
/// </para>
/// </remarks>
public sealed class ClipTrajectory
{
    private readonly ImmutableArray<Vector3> _leftBall;
    private readonly ImmutableArray<Vector3> _rightBall;

    /// <summary>Creates a trajectory from native-rate samples.</summary>
    /// <param name="identity">Package animation identity.</param>
    /// <param name="role">Opaque package role.</param>
    /// <param name="capability">
    /// Declared semantic capability, or <see langword="null" /> for a clip outside the standing-locomotion catalogue
    /// (Idle).
    /// </param>
    /// <param name="durationSeconds">Clip length in source seconds.</param>
    /// <param name="keyRateHz">Native key rate.</param>
    /// <param name="sourceRootPlanar">Root planar samples in source units.</param>
    /// <param name="rootYaw">Unwrapped root yaw samples in radians.</param>
    /// <param name="leftBall">Left ball samples in runtime units of the rig evaluated at <paramref name="ballMotionScale"/>.</param>
    /// <param name="rightBall">Right ball samples in runtime units of the rig evaluated at <paramref name="ballMotionScale"/>.</param>
    /// <param name="ballMotionScale">Motion scale of the rig the ball samples were evaluated on.</param>
    /// <param name="errorBounds">Declared interpolation error bounds.</param>
    /// <param name="motionScale">Scale applied to root planar queries; defaults to <paramref name="ballMotionScale"/>.</param>
    /// <exception cref="ArgumentException">A sample array, timing, scale or bound is inconsistent or non-finite.</exception>
    public ClipTrajectory(
        string identity,
        string role,
        StandingLocomotionCapability? capability,
        double durationSeconds,
        double keyRateHz,
        ImmutableArray<Vector2> sourceRootPlanar,
        ImmutableArray<float> rootYaw,
        ImmutableArray<Vector3> leftBall,
        ImmutableArray<Vector3> rightBall,
        float ballMotionScale,
        ClipTrajectoryErrorBounds errorBounds,
        float? motionScale = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            throw new ArgumentException("Clip duration must be finite and positive.", nameof(durationSeconds));
        }

        if (!double.IsFinite(keyRateHz) || keyRateHz <= 0d)
        {
            throw new ArgumentException("Key rate must be finite and positive.", nameof(keyRateHz));
        }

        if (!errorBounds.IsValid)
        {
            throw new ArgumentException("Error bounds must be finite and non-negative.", nameof(errorBounds));
        }

        ValidateMotionScale(ballMotionScale, nameof(ballMotionScale));
        float rootScale = motionScale ?? ballMotionScale;
        ValidateMotionScale(rootScale, nameof(motionScale));
        int count = ExpectedSampleCount(durationSeconds, keyRateHz);
        RequireSamples(sourceRootPlanar, count, nameof(sourceRootPlanar), static value => value.IsFinite());
        RequireSamples(rootYaw, count, nameof(rootYaw), float.IsFinite);
        RequireSamples(leftBall, count, nameof(leftBall), static value => value.IsFinite());
        RequireSamples(rightBall, count, nameof(rightBall), static value => value.IsFinite());

        Identity = identity;
        Role = role;
        Capability = capability;
        DurationSeconds = durationSeconds;
        KeyRateHz = keyRateHz;
        MotionScale = rootScale;
        BallMotionScale = ballMotionScale;
        ErrorBounds = errorBounds;
        SourceRootPlanar = sourceRootPlanar;
        RootYaw = rootYaw;
        _leftBall = leftBall;
        _rightBall = rightBall;
    }

    private ClipTrajectory(ClipTrajectory source, float motionScale)
    {
        Identity = source.Identity;
        Role = source.Role;
        Capability = source.Capability;
        DurationSeconds = source.DurationSeconds;
        KeyRateHz = source.KeyRateHz;
        MotionScale = motionScale;
        BallMotionScale = source.BallMotionScale;
        ErrorBounds = source.ErrorBounds;
        SourceRootPlanar = source.SourceRootPlanar;
        RootYaw = source.RootYaw;
        _leftBall = source._leftBall;
        _rightBall = source._rightBall;
    }

    /// <summary>Package animation identity (the library animation name).</summary>
    public string Identity
    {
        get;
    }

    /// <summary>Opaque package role.</summary>
    public string Role
    {
        get;
    }

    /// <summary>
    /// Semantic capability the package declares for the clip, or <see langword="null" /> for a clip outside the
    /// standing-locomotion catalogue (Idle).
    /// </summary>
    public StandingLocomotionCapability? Capability
    {
        get;
    }

    /// <summary>Clip length in source seconds.</summary>
    public double DurationSeconds
    {
        get;
    }

    /// <summary>Declared native key rate of the clip's root tracks, in samples per second.</summary>
    public double KeyRateHz
    {
        get;
    }

    /// <summary>Scale applied to root planar samples by every root query.</summary>
    public float MotionScale
    {
        get;
    }

    /// <summary>Motion scale of the rig the ball samples were evaluated on; ball queries do not rescale.</summary>
    public float BallMotionScale
    {
        get;
    }

    /// <summary>Declared interpolation error bounds.</summary>
    public ClipTrajectoryErrorBounds ErrorBounds
    {
        get;
    }

    /// <summary>Declared root planar error bound in runtime metres: the source bound × <see cref="MotionScale"/>.</summary>
    public float RootPlanarErrorBound => ErrorBounds.SourceRootPlanar * MotionScale;

    /// <summary>Number of samples.</summary>
    public int SampleCount => RootYaw.Length;

    /// <summary>Source-unit root planar samples, as authored (not relative to the first sample).</summary>
    public ImmutableArray<Vector2> SourceRootPlanar
    {
        get;
    }

    /// <summary>Unwrapped root yaw samples in radians, as authored.</summary>
    public ImmutableArray<float> RootYaw
    {
        get;
    }

    /// <summary>Runtime-scale root displacement and yaw change over the whole clip.</summary>
    public ClipTrajectorySlice Endpoint => Slice(0d, DurationSeconds);

    /// <summary>Runtime-unit ball samples of one foot, evaluated at <see cref="BallMotionScale"/>.</summary>
    public ImmutableArray<Vector3> BallSamples(StandingLocomotionAnatomicalSide foot)
        => foot == StandingLocomotionAnatomicalSide.Left ? _leftBall : _rightBall;

    /// <summary>
    /// Returns the same samples with the root data bound to another motion scale. Ball samples are unchanged and keep
    /// their <see cref="BallMotionScale"/>.
    /// </summary>
    public ClipTrajectory WithMotionScale(float motionScale)
    {
        ValidateMotionScale(motionScale, nameof(motionScale));
        return new ClipTrajectory(this, motionScale);
    }

    /// <summary>Source time of sample <paramref name="index"/>.</summary>
    public double SampleTime(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, SampleCount);
        return SampleTimeOf(index, DurationSeconds, KeyRateHz);
    }

    /// <summary>Runtime-scale root planar position at a source time, relative to the first sample.</summary>
    public Vector2 RootPlanarAt(double timeSeconds)
    {
        Locate(timeSeconds, out int index, out float weight);
        Vector2 source = SourceRootPlanar[index].Lerp(SourceRootPlanar[index + 1], weight);
        return (source - SourceRootPlanar[0]) * MotionScale;
    }

    /// <summary>Root yaw at a source time, relative to the first sample, in radians (positive turns left).</summary>
    public float YawAt(double timeSeconds)
    {
        Locate(timeSeconds, out int index, out float weight);
        return Mathf.Lerp(RootYaw[index], RootYaw[index + 1], weight) - RootYaw[0];
    }

    /// <summary>
    /// Runtime-scale root displacement from <paramref name="startSeconds"/> to <paramref name="endSeconds"/>, expressed
    /// in the root frame at the start time: <c>R(-ψ(a))·(P(b) - P(a))</c>, with yaw change <c>ψ(b) - ψ(a)</c>.
    /// </summary>
    public ClipTrajectorySlice Slice(double startSeconds, double endSeconds)
    {
        Vector2 start = RootPlanarAt(startSeconds);
        float startYaw = YawAt(startSeconds);
        Vector2 displacement = RotateSemantic(RootPlanarAt(endSeconds) - start, -startYaw);
        return new ClipTrajectorySlice(displacement, YawAt(endSeconds) - startYaw);
    }

    /// <summary>
    /// Ball-joint position of one foot at a source time in the bound rig's runtime metres, relative to the Root bone in
    /// its own frame (actor-local axes: +X right, +Y up, -Z forward). Not rescaled.
    /// </summary>
    public Vector3 Ball(StandingLocomotionAnatomicalSide foot, double timeSeconds)
    {
        ImmutableArray<Vector3> samples = BallSamples(foot);
        Locate(timeSeconds, out int index, out float weight);
        return samples[index].Lerp(samples[index + 1], weight);
    }

    /// <summary>Rotates an actor-semantic <c>[right, forward]</c> vector by a yaw (positive turns left).</summary>
    public static Vector2 RotateSemantic(Vector2 value, float yaw)
    {
        (float sin, float cos) = MathF.SinCos(yaw);
        return new Vector2((cos * value.X) - (sin * value.Y), (sin * value.X) + (cos * value.Y));
    }

    /// <summary>Number of native-rate samples covering <c>[0, duration]</c>, including a final sample at the end.</summary>
    public static int ExpectedSampleCount(double durationSeconds, double keyRateHz)
    {
        double intervals = durationSeconds * keyRateHz;
        // Tolerate float-authored durations that sit a hair above a whole key interval.
        return (int)Math.Ceiling(intervals - 1e-4) + 1;
    }

    /// <summary>Source time of sample <paramref name="index"/> for a clip timing.</summary>
    public static double SampleTimeOf(int index, double durationSeconds, double keyRateHz)
        => Math.Min(index / keyRateHz, durationSeconds);

    private void Locate(double timeSeconds, out int index, out float weight)
    {
        double time = double.IsFinite(timeSeconds) ? Math.Clamp(timeSeconds, 0d, DurationSeconds) : 0d;
        index = Math.Clamp((int)Math.Floor(time * KeyRateHz), 0, SampleCount - 2);
        double start = SampleTimeOf(index, DurationSeconds, KeyRateHz);
        double end = SampleTimeOf(index + 1, DurationSeconds, KeyRateHz);
        weight = end > start ? (float)Math.Clamp((time - start) / (end - start), 0d, 1d) : 1f;
    }

    private static void ValidateMotionScale(float motionScale, string name)
    {
        if (!float.IsFinite(motionScale) || motionScale <= 0f)
        {
            throw new ArgumentException("Motion scale must be finite and positive.", name);
        }
    }

    private static void RequireSamples<T>(ImmutableArray<T> samples, int count, string name, Func<T, bool> isFinite)
    {
        if (samples.IsDefault || samples.Length != count)
        {
            throw new ArgumentException($"Expected {count} native-rate samples.", name);
        }

        foreach (T sample in samples)
        {
            if (!isFinite(sample))
            {
                throw new ArgumentException("Samples must be finite.", name);
            }
        }
    }
}
