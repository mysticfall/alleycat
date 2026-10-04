using AlleyCat.Control.Locomotion;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// Tunable crossfade durations of planner-issued seams (NAV-001 Technical Requirements 7 and 16): a base duration for
/// every pair, and a shorter per-pair duration for a manoeuvre ↔ Idle join whose natural double-support run is too
/// short for the base blend window. Every duration is also the authored transition's duration in the NPC trees; the
/// seam table evaluates each pair over its own duration.
/// </summary>
/// <remarks>
/// <para>
/// A join with Idle (both feet planted) agrees in phase only while the manoeuvre clip stands on both feet, so its blend
/// window <c>d − λ</c> (see <see cref="SeamTable"/>) must fit inside one of the clip's double-support runs: for Idle
/// into a clip played once, the run before its first lift-off; for a clip played once into Idle, the run after its
/// last touch-down; for a wrapping (looping) confined manoeuvre, its longest run, wherever it lies, because a whole
/// step may span the loop point. When the base window does not fit that run, the pair's duration is the longest
/// whole number of ticks whose window fits the run less <see cref="MarginSeconds"/>, but never below
/// <see cref="MinimumTicks"/>. A clip with no such run gets no shorter duration: no duration makes that join agree, and
/// the seam report lists the pair as impossible.
/// </para>
/// <para>
/// Only the window length is adjusted; phase agreement and the seam tolerance are unchanged.
/// </para>
/// </remarks>
public sealed record SeamCrossfadePolicy
{
    private const double TimeTolerance = 1e-6d;

    /// <summary>The default policy: 0.15 s base crossfades at 60 Hz, floored at two ticks, with a one-tick margin.</summary>
    public static SeamCrossfadePolicy Default { get; } = new();

    /// <summary>Crossfade duration of every pair the policy does not shorten, in seconds.</summary>
    public double BaseSeconds { get; init; } = SeamTable.DefaultCrossfadeSeconds;

    /// <summary>
    /// Runtime physics delta, in seconds: the seam table's entry lag and the quantum of shortened durations.
    /// </summary>
    public double TickSeconds { get; init; } = SeamTable.DefaultEntryLagSeconds;

    /// <summary>Shortest duration a shortened crossfade may have, in ticks (at least two, so it blends).</summary>
    public int MinimumTicks { get; init; } = 2;

    /// <summary>Clearance kept between a shortened blend window and the end of its double-support run, in seconds.</summary>
    public double MarginSeconds { get; init; } = 1d / 60d;

    /// <summary>
    /// The shortened crossfade duration of a planner-set pair, or <see langword="null"/> when the pair uses
    /// <see cref="BaseSeconds"/>.
    /// </summary>
    public double? PairCrossfadeSeconds(SeamKind kind, SeamClip outgoing, SeamClip incoming)
    {
        ArgumentNullException.ThrowIfNull(outgoing);
        ArgumentNullException.ThrowIfNull(incoming);
        double run = kind == SeamKind.IdleToManoeuvre
            ? EntrySupportSeconds(incoming)
            : kind == SeamKind.ManoeuvreToIdle ? ExitSupportSeconds(outgoing) : double.PositiveInfinity;

        // The window d − λ must fit the run less the margin.
        double fitting = run + TickSeconds - MarginSeconds;
        if (run <= TimeTolerance || fitting >= BaseSeconds - TimeTolerance)
        {
            return null;
        }

        double ticks = Math.Max(MinimumTicks, Math.Floor((fitting / TickSeconds) + TimeTolerance));
        double duration = Math.Min(BaseSeconds, ticks * TickSeconds);
        return duration < BaseSeconds - TimeTolerance ? duration : null;
    }

    /// <summary>
    /// Double-support run available to a join from Idle: the run before the first lift-off of a clip played once
    /// (zero when it starts in swing), or the longest run of a wrapping clip.
    /// </summary>
    public static double EntrySupportSeconds(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<(double Start, double End)> runs = DoubleSupportRuns(clip);
        return clip.WrapsPlayback
            ? runs.Select(run => run.End - run.Start).DefaultIfEmpty(0d).Max()
            : runs.Count > 0 && runs[0].Start <= TimeTolerance ? runs[0].End - runs[0].Start : 0d;
    }

    /// <summary>
    /// Double-support run available to a join into Idle: the run after the last touch-down of a clip played once (zero
    /// when it ends in swing), or the longest run of a wrapping clip.
    /// </summary>
    public static double ExitSupportSeconds(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<(double Start, double End)> runs = DoubleSupportRuns(clip);
        return clip.WrapsPlayback
            ? runs.Select(run => run.End - run.Start).DefaultIfEmpty(0d).Max()
            : runs.Count > 0 && runs[^1].End >= clip.DurationSeconds - TimeTolerance ? runs[^1].End - runs[^1].Start : 0d;
    }

    /// <summary>
    /// Maximal intervals where both feet are planted, in playback order; a wrapping clip's run across its loop point is
    /// one run, listed last, ending after the clip end.
    /// </summary>
    public static List<(double Start, double End)> DoubleSupportRuns(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<double> boundaries = [0d, clip.DurationSeconds];
        boundaries.AddRange(clip.Intervals.Left.Select(segment => segment.StartSeconds));
        boundaries.AddRange(clip.Intervals.Right.Select(segment => segment.StartSeconds));
        boundaries.Sort();

        List<(double Start, double End)> runs = [];
        for (int index = 1; index < boundaries.Count; index++)
        {
            double start = boundaries[index - 1];
            double end = boundaries[index];
            if (end - start <= TimeTolerance)
            {
                continue;
            }

            double middle = 0.5d * (start + end);
            if (!clip.Intervals.IsPlanted(StandingLocomotionAnatomicalSide.Left, middle)
                || !clip.Intervals.IsPlanted(StandingLocomotionAnatomicalSide.Right, middle))
            {
                continue;
            }

            if (runs.Count > 0 && Math.Abs(runs[^1].End - start) <= TimeTolerance)
            {
                runs[^1] = (runs[^1].Start, end);
            }
            else
            {
                runs.Add((start, end));
            }
        }

        if (clip.WrapsPlayback
            && runs.Count > 1
            && runs[0].Start <= TimeTolerance
            && runs[^1].End >= clip.DurationSeconds - TimeTolerance)
        {
            double end = runs[0].End;
            runs.RemoveAt(0);
            runs[^1] = (runs[^1].Start, end + clip.DurationSeconds);
        }

        return runs;
    }
}
