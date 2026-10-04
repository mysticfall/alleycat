using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>Planner role of a clip at a seam, derived from the package's boundary kind and capability.</summary>
public enum SeamClipRole
{
    /// <summary>The idle package's standing clip.</summary>
    Idle,

    /// <summary>A transition from Idle into travel (<c>entry_*</c>).</summary>
    Entry,

    /// <summary>A cyclic travel clip.</summary>
    Cycle,

    /// <summary>A transition from travel back to Idle (<c>exit_*</c>).</summary>
    Exit,

    /// <summary>A confined manoeuvre: side-step or pivot.</summary>
    Confined,
}

/// <summary>
/// Seam-analysis view of one clip bound to one rig: its per-foot root-relative ball samples at the native key rate,
/// in the rig's runtime metres, and its ANIM-002 per-foot planted intervals.
/// </summary>
/// <remarks>
/// <para>
/// Ball frame: relative to the Root bone in its own frame, actor-local axes (+X right, +Y up, -Z forward), as
/// <see cref="ClipTrajectory.Ball"/> declares. Sample <c>i</c> is at <c>min(i / KeyRateHz, DurationSeconds)</c>, and
/// queries between samples interpolate linearly.
/// </para>
/// <para>
/// Playback: <see cref="WrapsPlayback"/> clips continue from their start after their end, so playback and a crossfade
/// window may cross the loop point. Cycles and Idle wrap, and so do confined manoeuvres whose planted intervals loop
/// (ANIM-002 designs side-steps and pivots to loop seamlessly, and the NPC tree plays them looping): a whole side-step
/// or pivot runs from one double-support phase through its steps to the next, across the loop point when the clip
/// starts mid-step. Entries and exits are played once, so a window must fit inside the clip.
/// </para>
/// </remarks>
public sealed class SeamClip
{
    private const double TimeTolerance = 1e-6d;

    private readonly ImmutableArray<Vector3> _leftBall;
    private readonly ImmutableArray<Vector3> _rightBall;

    /// <summary>Creates a seam clip from native-rate ball samples and planted intervals.</summary>
    /// <param name="role">Package role (unique within a seam table).</param>
    /// <param name="identity">Package animation identity.</param>
    /// <param name="kind">Planner role of the clip.</param>
    /// <param name="durationSeconds">Clip length in source seconds.</param>
    /// <param name="keyRateHz">Native key rate of the samples.</param>
    /// <param name="leftBall">Left ball samples, root-relative, in runtime metres.</param>
    /// <param name="rightBall">Right ball samples, root-relative, in runtime metres.</param>
    /// <param name="ballErrorBound">Declared interpolation error bound of the ball samples, in runtime metres.</param>
    /// <param name="intervals">The clip's ANIM-002 per-foot planted intervals.</param>
    /// <exception cref="ArgumentException">Timing, samples or intervals are inconsistent.</exception>
    public SeamClip(
        string role,
        string identity,
        SeamClipRole kind,
        double durationSeconds,
        double keyRateHz,
        ImmutableArray<Vector3> leftBall,
        ImmutableArray<Vector3> rightBall,
        float ballErrorBound,
        PlantedClipIntervals intervals)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(intervals);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0d)
        {
            throw new ArgumentException("Clip duration must be finite and positive.", nameof(durationSeconds));
        }

        if (!double.IsFinite(keyRateHz) || keyRateHz <= 0d)
        {
            throw new ArgumentException("Key rate must be finite and positive.", nameof(keyRateHz));
        }

        if (!float.IsFinite(ballErrorBound) || ballErrorBound < 0f)
        {
            throw new ArgumentException("Ball error bound must be finite and non-negative.", nameof(ballErrorBound));
        }

        int count = ClipTrajectory.ExpectedSampleCount(durationSeconds, keyRateHz);
        if (count < 2
            || leftBall.IsDefault
            || rightBall.IsDefault
            || leftBall.Length != count
            || rightBall.Length != count)
        {
            throw new ArgumentException($"Expected {count} native-rate ball samples per foot.", nameof(leftBall));
        }

        if (intervals.Identity != identity || Math.Abs(intervals.DurationSeconds - durationSeconds) > 1e-4d)
        {
            throw new ArgumentException(
                $"Planted intervals of '{intervals.Identity}' do not describe clip '{identity}'.",
                nameof(intervals));
        }

        Role = role;
        Identity = identity;
        Kind = kind;
        DurationSeconds = durationSeconds;
        KeyRateHz = keyRateHz;
        BallErrorBound = ballErrorBound;
        Intervals = intervals;
        _leftBall = leftBall;
        _rightBall = rightBall;
        SampleCount = count;
    }

    /// <summary>Package role.</summary>
    public string Role
    {
        get;
    }

    /// <summary>Package animation identity.</summary>
    public string Identity
    {
        get;
    }

    /// <summary>Planner role of the clip.</summary>
    public SeamClipRole Kind
    {
        get;
    }

    /// <summary>
    /// Declared capability for locomotion package clips; <see langword="null"/> for Idle, which declares no
    /// locomotion capability.
    /// </summary>
    public StandingLocomotionCapability? Capability
    {
        get;
        init;
    }

    /// <summary>Clip length in source seconds.</summary>
    public double DurationSeconds
    {
        get;
    }

    /// <summary>Native key rate of the ball samples.</summary>
    public double KeyRateHz
    {
        get;
    }

    /// <summary>Declared ball interpolation error bound in runtime metres.</summary>
    public float BallErrorBound
    {
        get;
    }

    /// <summary>The clip's ANIM-002 per-foot planted intervals.</summary>
    public PlantedClipIntervals Intervals
    {
        get;
    }

    /// <summary>
    /// Whether playback continues from the clip start after its end: cycles, Idle, and confined manoeuvres whose
    /// planted intervals loop.
    /// </summary>
    public bool WrapsPlayback => Kind is SeamClipRole.Cycle or SeamClipRole.Idle
        || (Kind == SeamClipRole.Confined && Intervals.IsLooping);

    /// <summary>Number of native-rate samples.</summary>
    public int SampleCount
    {
        get;
    }

    /// <summary>
    /// Number of distinct native-grid seam times: every sample for a clip played once, and every sample except the
    /// final one (which is the loop point, equal to time 0) for a wrapping clip.
    /// </summary>
    public int GridTimeCount => WrapsPlayback ? SampleCount - 1 : SampleCount;

    /// <summary>Native-grid time of grid index <paramref name="index"/>.</summary>
    public double GridTime(int index) => ClipTrajectory.SampleTimeOf(index, DurationSeconds, KeyRateHz);

    /// <summary>
    /// Creates the seam view of a locomotion package clip from its bound trajectory and planted intervals.
    /// </summary>
    /// <exception cref="ArgumentException">The trajectory, catalogue clip and intervals describe different clips.</exception>
    public static SeamClip FromTrajectory(
        StandingLocomotionClip clip,
        ClipTrajectory trajectory,
        PlantedClipIntervals intervals)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(trajectory);
        return trajectory.Role != clip.Role || trajectory.Identity != clip.Identity || trajectory.Capability != clip.Capability
            ? throw new ArgumentException(
                $"Trajectory '{trajectory.Role}' does not belong to catalogue clip '{clip.Role}'.",
                nameof(trajectory))
            : FromTrajectory(trajectory, RoleOf(clip), intervals);
    }

    /// <summary>
    /// Creates the seam view of any bound trajectory, including uncatalogued clips such as Idle that
    /// <see cref="ClipTrajectoryTableBuilder"/> builds without a locomotion capability.
    /// </summary>
    /// <param name="trajectory">The clip's bound trajectory; supplies timing, ball samples and the ball bound.</param>
    /// <param name="kind">Planner role of the clip.</param>
    /// <param name="intervals">The clip's ANIM-002 per-foot planted intervals.</param>
    /// <exception cref="ArgumentException">The trajectory and intervals describe different clips.</exception>
    public static SeamClip FromTrajectory(ClipTrajectory trajectory, SeamClipRole kind, PlantedClipIntervals intervals)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        ArgumentNullException.ThrowIfNull(intervals);
        return new SeamClip(
            trajectory.Role,
            trajectory.Identity,
            kind,
            trajectory.DurationSeconds,
            trajectory.KeyRateHz,
            trajectory.BallSamples(StandingLocomotionAnatomicalSide.Left),
            trajectory.BallSamples(StandingLocomotionAnatomicalSide.Right),
            trajectory.ErrorBounds.Ball,
            intervals)
        {
            Capability = trajectory.Capability,
        };
    }

    /// <summary>Planner role of a locomotion package clip from its boundary kind and capability.</summary>
    public static SeamClipRole RoleOf(StandingLocomotionClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.BoundaryKind == StandingLocomotionBoundaryKind.CyclicTravel)
        {
            return SeamClipRole.Cycle;
        }

        if (clip.BoundaryKind == StandingLocomotionBoundaryKind.ConfinedManoeuvre)
        {
            return SeamClipRole.Confined;
        }

        bool entry = clip.Capability is StandingLocomotionCapability.EntryForwardLeft
            or StandingLocomotionCapability.EntryForwardRight
            or StandingLocomotionCapability.EntryBackwardLeft
            or StandingLocomotionCapability.EntryBackwardRight;
        bool exit = clip.Capability is StandingLocomotionCapability.ExitForwardLeft
            or StandingLocomotionCapability.ExitForwardRight
            or StandingLocomotionCapability.ExitBackwardLeft
            or StandingLocomotionCapability.ExitBackwardRight;
        return entry
            ? SeamClipRole.Entry
            : exit
                ? SeamClipRole.Exit
                : throw new InvalidDataException($"Transition clip '{clip.Role}' is neither an entry nor an exit.");
    }

    /// <summary>Maps a playback position to source time: wrapping clips wrap, others clamp.</summary>
    public double Normalise(double timeSeconds)
    {
        if (!WrapsPlayback)
        {
            return Math.Clamp(timeSeconds, 0d, DurationSeconds);
        }

        double wrapped = timeSeconds % DurationSeconds;
        return wrapped < 0d ? wrapped + DurationSeconds : wrapped;
    }

    /// <summary>Whether a crossfade window of <paramref name="durationSeconds"/> starting at a time fits the clip.</summary>
    public bool WindowFits(double startSeconds, double durationSeconds)
        => WrapsPlayback || startSeconds + durationSeconds <= DurationSeconds + TimeTolerance;

    /// <summary>Root-relative ball position of one foot at a playback position, in runtime metres.</summary>
    public Vector3 Ball(StandingLocomotionAnatomicalSide foot, double timeSeconds) => Ball(foot, timeSeconds, false);

    /// <summary>
    /// Root-relative ball position of one foot at a playback position. With <paramref name="fromBelow"/>, a wrapping
    /// clip exactly at a loop point after its start returns the clip-end sample (the limit from below) rather than
    /// the start sample.
    /// </summary>
    public Vector3 Ball(StandingLocomotionAnatomicalSide foot, double timeSeconds, bool fromBelow)
    {
        ImmutableArray<Vector3> samples = foot == StandingLocomotionAnatomicalSide.Left ? _leftBall : _rightBall;
        if (fromBelow && WrapsPlayback && timeSeconds > TimeTolerance)
        {
            double laps = timeSeconds / DurationSeconds;
            if (Math.Abs(laps - Math.Round(laps)) * DurationSeconds <= TimeTolerance)
            {
                return samples[^1];
            }
        }

        double time = Normalise(timeSeconds);
        int index = Math.Clamp((int)Math.Floor(time * KeyRateHz), 0, SampleCount - 2);
        double start = GridTime(index);
        double end = GridTime(index + 1);
        float weight = end > start ? (float)Math.Clamp((time - start) / (end - start), 0d, 1d) : 1f;
        return samples[index].Lerp(samples[index + 1], weight);
    }

    /// <summary>Whether one foot is planted at a playback position under the clip's planted intervals.</summary>
    public bool IsPlanted(StandingLocomotionAnatomicalSide foot, double timeSeconds)
        => Intervals.IsPlanted(foot, Normalise(timeSeconds));

    /// <summary>
    /// Adds the offsets <c>τ</c> in <c>(0, windowSeconds)</c> of the clip's sample times when it plays from
    /// <paramref name="startSeconds"/>, including the loop point and the following laps of a wrapping clip.
    /// </summary>
    internal void AddKeyOffsets(double startSeconds, double windowSeconds, List<double> offsets)
    {
        int laps = WrapsPlayback ? (int)Math.Ceiling(windowSeconds / DurationSeconds) + 1 : 1;
        for (int lap = 0; lap < laps; lap++)
        {
            double lapStart = startSeconds - (lap * DurationSeconds);
            int first = Math.Max(0, (int)Math.Floor(lapStart * KeyRateHz));
            int last = Math.Min(SampleCount - 1, (int)Math.Ceiling((lapStart + windowSeconds) * KeyRateHz));
            for (int index = first; index <= last; index++)
            {
                double offset = GridTime(index) - lapStart;
                if (offset > TimeTolerance && offset < windowSeconds - TimeTolerance)
                {
                    offsets.Add(offset);
                }
            }
        }
    }

    /// <summary>
    /// Adds the offsets <c>τ</c> in <c>(0, windowSeconds)</c> at which either foot's planted state may change when the
    /// clip plays from <paramref name="startSeconds"/>.
    /// </summary>
    internal void AddStateBreakpoints(double startSeconds, double windowSeconds, List<double> breakpoints)
    {
        AddFootBreakpoints(Intervals.Left, startSeconds, windowSeconds, breakpoints);
        AddFootBreakpoints(Intervals.Right, startSeconds, windowSeconds, breakpoints);
    }

    private void AddFootBreakpoints(
        ImmutableArray<PlantedIntervalSegment> segments,
        double startSeconds,
        double windowSeconds,
        List<double> breakpoints)
    {
        // A wrapping clip also changes state at the loop point when its last and first segments differ; the
        // segment starts include time 0, which stands for every loop point.
        int laps = WrapsPlayback ? (int)Math.Ceiling(windowSeconds / DurationSeconds) + 1 : 1;
        foreach (PlantedIntervalSegment segment in segments)
        {
            for (int lap = 0; lap < laps; lap++)
            {
                double offset = segment.StartSeconds + (lap * DurationSeconds) - startSeconds;
                if (offset > TimeTolerance && offset < windowSeconds - TimeTolerance)
                {
                    breakpoints.Add(offset);
                }
            }
        }
    }
}
