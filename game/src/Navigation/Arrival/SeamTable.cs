using System.Collections.Immutable;
using AlleyCat.Control.Locomotion;

namespace AlleyCat.Navigation.Arrival;

/// <summary>The seam kinds of NAV-001 Technical Requirement 7: every crossfade the arrival planner issues.</summary>
public enum SeamKind
{
    /// <summary>Entry clip into a cyclic travel clip.</summary>
    EntryToCycle,

    /// <summary>Lane switch between two cyclic travel clips.</summary>
    CycleToCycle,

    /// <summary>Cyclic travel clip into an exit clip.</summary>
    CycleToExit,

    /// <summary>Head of an entry clip into the tail of the matching exit clip.</summary>
    SliceJoin,

    /// <summary>Idle into an entry clip or a confined manoeuvre.</summary>
    IdleToManoeuvre,

    /// <summary>An exit clip or a confined manoeuvre into Idle.</summary>
    ManoeuvreToIdle,

    /// <summary>Pivot-to-itself using independent native playback slots.</summary>
    PivotContinuation,
}

/// <summary>An ordered clip pair the planner may crossfade between, and its seam kind.</summary>
/// <param name="Kind">Seam kind.</param>
/// <param name="OutgoingRole">Role of the clip faded out.</param>
/// <param name="IncomingRole">Role of the clip faded in.</param>
public readonly record struct SeamPairSpec(SeamKind Kind, string OutgoingRole, string IncomingRole)
{
    /// <summary>
    /// Earliest outgoing time a seam may start at, in source seconds; for a slice join, the shortest head.
    /// </summary>
    public double MinimumOutgoingSeconds
    {
        get; init;
    }

    /// <summary>
    /// This pair's own crossfade duration in seconds (<see cref="SeamCrossfadePolicy"/>), or <see langword="null"/>
    /// for the table's base duration.
    /// </summary>
    public double? CrossfadeSeconds
    {
        get; init;
    }
}

/// <summary>
/// One phase-agreeing seam <c>(a, b)</c>: the transition is requested when outgoing playback reaches
/// <see cref="OutgoingTime"/>, and the incoming clip enters at <see cref="IncomingTime"/> on the next tick, both on their
/// native key grid. See the <see cref="SeamTable"/> remarks for the window the seam was evaluated over.
/// </summary>
/// <param name="OutgoingTime">
/// Outgoing clip playback position after the tick that requests the transition (the outgoing segment's exit), in
/// source seconds.
/// </param>
/// <param name="IncomingTime">Incoming clip entry (seek) position, in source seconds.</param>
/// <param name="LeftSeparation">
/// Largest root-relative left-ball separation over the window, in runtime metres.
/// </param>
/// <param name="RightSeparation">
/// Largest root-relative right-ball separation over the window, in runtime metres.
/// </param>
/// <param name="HasSupportThroughout">Whether at least one foot is planted in both clips throughout the window.</param>
public readonly record struct SeamCandidate(
    double OutgoingTime,
    double IncomingTime,
    float LeftSeparation,
    float RightSeparation,
    bool HasSupportThroughout)
{
    /// <summary>Maximum ball separation over samples classified as planted, across both feet.</summary>
    public float PlantedSeparation
    {
        get; init;
    }

    /// <summary>Maximum ball separation over samples classified as swinging, across both feet.</summary>
    public float SwingSeparation
    {
        get; init;
    }

    /// <summary>Largest per-foot separation over the window: the value compared with the seam tolerance.</summary>
    public float MaxSeparation => Math.Max(LeftSeparation, RightSeparation);

    /// <summary>Whether the seam satisfies pose agreement under a seam tolerance.</summary>
    public bool IsAlignedWithin(float toleranceMetres) => MaxSeparation <= toleranceMetres;
}

/// <summary>
/// Every phase-agreeing seam of one ordered clip pair, ordered by outgoing time and then by separation.
/// </summary>
public sealed class SeamPair
{
    internal SeamPair(
        SeamKind kind,
        SeamClip outgoing,
        SeamClip incoming,
        int evaluatedCount,
        ImmutableArray<SeamCandidate> candidates,
        double minimumOutgoingSeconds,
        double crossfadeSeconds,
        double entryLagSeconds)
    {
        MinimumOutgoingSeconds = minimumOutgoingSeconds;
        Kind = kind;
        Outgoing = outgoing;
        Incoming = incoming;
        EvaluatedCount = evaluatedCount;
        Candidates = candidates;
        CrossfadeSeconds = crossfadeSeconds;
        EntryLagSeconds = entryLagSeconds;
    }

    /// <summary>Seam kind.</summary>
    public SeamKind Kind
    {
        get;
    }

    /// <summary>
    /// Crossfade duration of this pair's seams, in seconds: the authored transition's duration, which the runtime
    /// requests.
    /// </summary>
    public double CrossfadeSeconds
    {
        get;
    }

    /// <summary>Playback the outgoing clip leads the incoming one by on the entry tick, in seconds.</summary>
    public double EntryLagSeconds
    {
        get;
    }

    /// <summary>
    /// Blend window of this pair's seams: <see cref="CrossfadeSeconds"/> less <see cref="EntryLagSeconds"/>, the
    /// playback both clips advance through together from the entry tick until the outgoing weight reaches zero.
    /// </summary>
    public double WindowSeconds => CrossfadeSeconds - EntryLagSeconds;

    /// <summary>Earliest outgoing time evaluated, in source seconds (the shortest slice head).</summary>
    public double MinimumOutgoingSeconds
    {
        get;
    }

    /// <summary>Clip faded out.</summary>
    public SeamClip Outgoing
    {
        get;
    }

    /// <summary>Clip faded in.</summary>
    public SeamClip Incoming
    {
        get;
    }

    /// <summary>
    /// Number of native-grid <c>(a, b)</c> windows that fit both clips (and, for a slice join, its shortest head) and
    /// were evaluated.
    /// </summary>
    public int EvaluatedCount
    {
        get;
    }

    /// <summary>Phase-agreeing seams, by outgoing time and then by separation.</summary>
    public ImmutableArray<SeamCandidate> Candidates
    {
        get;
    }

    /// <summary>
    /// Whether the crossfade duration makes this pair impossible: no evaluated window lies inside a same-phase
    /// segment of both clips.
    /// </summary>
    public bool IsImpossible => Candidates.IsEmpty;

    /// <summary>
    /// Whether the planner may issue a phase-agreeing seam of this pair. A slice join additionally needs a foot planted
    /// in both clips throughout the window (<see cref="SeamCandidate.HasSupportThroughout"/>): NAV-001 Technical
    /// Requirement 6 joins slices where the same anatomical foot is planted in both. Phase agreement already makes
    /// every planted foot of one clip planted in the other at each instant, so support throughout is exactly "the same
    /// foot is planted in both clips at every instant of the join".
    /// </summary>
    public bool IsUsable(SeamCandidate candidate) => Kind != SeamKind.SliceJoin || candidate.HasSupportThroughout;

    /// <summary>Phase-agreeing seams the planner may issue (<see cref="IsUsable"/>), by outgoing time.</summary>
    public IEnumerable<SeamCandidate> UsableCandidates => Candidates.Where(IsUsable);

    /// <summary>Usable seam with the smallest separation, if any.</summary>
    public SeamCandidate? Best
    {
        get
        {
            SeamCandidate? best = null;
            foreach (SeamCandidate candidate in Candidates)
            {
                if (IsUsable(candidate) && (best is null || candidate.MaxSeparation < best.Value.MaxSeparation))
                {
                    best = candidate;
                }
            }

            return best;
        }
    }
}

/// <summary>
/// An aligned seam chosen for an outgoing playback position. Its times are native-grid aligned; see the
/// <see cref="SeamTable"/> remarks on issuing it.
/// </summary>
/// <param name="Pair">The clip pair.</param>
/// <param name="Candidate">The aligned seam.</param>
/// <param name="DelaySeconds">
/// Playback time from the query position until the seam's outgoing time: the transition is requested on the tick
/// whose evaluation leaves the outgoing clip there.
/// </param>
public readonly record struct SeamChoice(SeamPair Pair, SeamCandidate Candidate, double DelaySeconds)
{
    /// <summary>The chosen seam's pose separation: its largest per-foot separation over the window, in metres.</summary>
    public float Separation => Candidate.MaxSeparation;
}

/// <summary>
/// Bind-time seam table of NAV-001 Technical Requirement 7: for each ordered clip pair the planner may crossfade
/// between, every native-grid <c>(outgoing time, incoming time)</c> seam whose crossfade window, as the runtime plays
/// it, satisfies phase agreement, with its pose separation. Built once per rig binding, never per frame; the
/// single-pair <see cref="TryFindNextAligned(string, string, double, float, out SeamChoice)"/> query does not
/// allocate, while the multi-lane and enumeration queries are for the planner's bounded replanning events.
/// </summary>
/// <remarks>
/// <para>
/// Seam time semantics: a seam <c>(a, b)</c> of a pair with crossfade duration <c>d</c> (<see
/// cref="SeamPair.CrossfadeSeconds"/>) is issued by requesting the transition after the tick whose evaluation leaves
/// the outgoing clip at <c>a</c> (its segment's exit), with the incoming clip sought to <c>b</c>. The
/// <see cref="NPCAnimationTreeDriver"/> enters it on the next tick, the entry tick: the incoming clip is evaluated at
/// <c>b</c> while the outgoing clip advances by the tick to <c>a + λ</c>, where the entry lag <c>λ</c>
/// (<see cref="EntryLagSeconds"/>) is the physics delta. From then on both advance together, so on the <c>k</c>-th
/// crossfade tick the outgoing clip is at <c>a + kλ</c> and the incoming one at <c>b + (k − 1)λ</c>, and the
/// outgoing weight reaches zero once the crossfade position reaches <c>d</c>.
/// </para>
/// <para>
/// Window: the table evaluates exactly that pairing, outgoing <c>a + λ + τ</c> against incoming <c>b + τ</c>, over
/// the blend window <c>τ ∈ [0, w]</c> with <c>w = d − λ</c> (<see cref="SeamPair.WindowSeconds"/>): the outgoing clip
/// plays over <c>[a + λ, a + d]</c> and the incoming one over <c>[b, b + w]</c>. A clip played once must contain its
/// part of the window; wrapping clips (<see cref="SeamClip.WrapsPlayback"/>) may cross their loop point.
/// </para>
/// <para>
/// Phase agreement (strict): every foot has the same planted or swing state in both clips at every instant of the
/// window, except the instant <c>τ = w</c> where the outgoing clip's weight reaches zero. The states are piecewise
/// constant, so the check is exact: the window is split at every interval boundary of either clip and each piece is
/// tested once. It therefore holds on every crossfade tick, whatever the crossfade position at which the engine ends
/// the fade.
/// </para>
/// <para>
/// Pose agreement (tunable): the largest root-relative ball separation per foot over the whole window
/// <c>[0, w]</c>. Both clips' ball samples are linear between their keys, so between consecutive key offsets of
/// either clip the separation is the norm of a linear function, which is convex; its maximum over the window is
/// therefore exact at the key offsets and the window ends. The value is not compared with a tolerance here; queries
/// take the tolerance in force.
/// </para>
/// <para>
/// Issuing a seam: every tabulated and queried seam <c>(a, b)</c> lies on the clips' native key grid (30 Hz for the
/// reference packages). Its phase and pose agreement hold only for that exact pair, the pair's crossfade duration and
/// a physics delta equal to the entry lag, so the issuer must request the transition exactly when outgoing playback
/// reaches <c>a</c> (to tick precision), with the pair's authored crossfade, and seek the incoming clip to <c>b</c>. A
/// crossfade requested at a shifted outgoing time <c>a'</c> must seek <c>b + (a' − a)</c> and re-check that shifted
/// pair with <see cref="TryEvaluateSeam"/> before issuing it.
/// </para>
/// </remarks>
public sealed class SeamTable
{
    /// <summary>Default crossfade duration of planner-issued seams, in seconds.</summary>
    public const double DefaultCrossfadeSeconds = 0.15d;

    /// <summary>
    /// Default entry lag: the NPC runtime's physics delta (60 Hz), by which the outgoing clip leads the incoming one
    /// on the entry tick.
    /// </summary>
    public const double DefaultEntryLagSeconds = 1d / 60d;

    private const double TimeTolerance = 1e-6d;

    private readonly ImmutableDictionary<(string Outgoing, string Incoming), SeamPair> _byRoles;

    private readonly WindowEvaluator _evaluator;

    private SeamTable(
        double baseCrossfadeSeconds,
        double entryLagSeconds,
        ImmutableDictionary<string, SeamClip> clips,
        ImmutableArray<SeamPair> pairs,
        ImmutableArray<SeamPairExclusion> excludedPairs)
    {
        BaseCrossfadeSeconds = baseCrossfadeSeconds;
        EntryLagSeconds = entryLagSeconds;
        Clips = clips;
        Pairs = pairs;
        ExcludedPairs = excludedPairs;
        _byRoles = pairs.ToImmutableDictionary(pair => (pair.Outgoing.Role, pair.Incoming.Role));
        _evaluator = new WindowEvaluator(entryLagSeconds);
        ExcludedClips =
        [
            .. clips.Values
                .Where(clip => !pairs.Any(pair => pair.Outgoing == clip || pair.Incoming == clip))
                .OrderBy(clip => clip.Role, StringComparer.Ordinal)
                .Select(clip => new SeamClipExclusion(clip.Role, SeamPairPolicy.ExclusionReasonOf(clip))),
        ];
    }

    /// <summary>
    /// Clips of the binding outside the planner set: no analysed pair includes them. They are excluded from planning,
    /// which is distinct from an unavailable cyclic clip, and are not defects.
    /// </summary>
    public ImmutableArray<SeamClipExclusion> ExcludedClips
    {
        get;
    }

    /// <summary>Candidate pairs the planner-set policy excluded, with their reasons; they are never analysed.</summary>
    public ImmutableArray<SeamPairExclusion> ExcludedPairs
    {
        get;
    }

    /// <summary>
    /// Crossfade duration of every pair without its own (<see cref="SeamPairSpec.CrossfadeSeconds"/>), in seconds.
    /// Each pair's duration is <see cref="SeamPair.CrossfadeSeconds"/>.
    /// </summary>
    public double BaseCrossfadeSeconds
    {
        get;
    }

    /// <summary>
    /// Playback the outgoing clip leads the incoming one by on the entry tick, in seconds: the physics delta the
    /// seams are certified for (see the remarks).
    /// </summary>
    public double EntryLagSeconds
    {
        get;
    }

    /// <summary>Clips by role.</summary>
    public ImmutableDictionary<string, SeamClip> Clips
    {
        get;
    }

    /// <summary>Every analysed pair, in declaration order.</summary>
    public ImmutableArray<SeamPair> Pairs
    {
        get;
    }

    /// <summary>
    /// Builds the table of the planner set <see cref="SeamPairPolicy.PlannerSet"/> derives from the clips, with the
    /// per-pair crossfade durations and the entry lag of a crossfade policy.
    /// </summary>
    /// <param name="clips">Clips of one rig binding; roles must be unique.</param>
    /// <param name="crossfades">Crossfade policy; <see cref="SeamCrossfadePolicy.Default"/> when omitted.</param>
    /// <exception cref="ArgumentException">A role repeats, or a duration is invalid.</exception>
    public static SeamTable Build(IEnumerable<SeamClip> clips, SeamCrossfadePolicy? crossfades = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        SeamCrossfadePolicy policy = crossfades ?? SeamCrossfadePolicy.Default;
        SeamClip[] all = [.. clips];
        SeamPlannerSet set = SeamPairPolicy.PlannerSet(all, policy);
        return Build(all, set.Pairs, policy.BaseSeconds, set.ExcludedPairs, policy.TickSeconds);
    }

    /// <summary>Builds the table for the given clips and pairs (the planner set).</summary>
    /// <param name="clips">Clips of one rig binding; roles must be unique.</param>
    /// <param name="pairs">Ordered pairs to analyse; each pair at most once.</param>
    /// <param name="crossfadeSeconds">Crossfade duration of pairs without their own.</param>
    /// <param name="excludedPairs">Candidate pairs excluded from the planner set, reported but never analysed.</param>
    /// <param name="entryLagSeconds">
    /// Entry lag (the runtime physics delta); zero evaluates both clips starting together, as a runtime that entered
    /// the incoming clip on the request tick would.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A role repeats or is unknown, the entry lag is negative, or a crossfade duration is not longer than the entry lag.
    /// </exception>
    public static SeamTable Build(
        IEnumerable<SeamClip> clips,
        IEnumerable<SeamPairSpec> pairs,
        double crossfadeSeconds = DefaultCrossfadeSeconds,
        IEnumerable<SeamPairExclusion>? excludedPairs = null,
        double entryLagSeconds = DefaultEntryLagSeconds)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(pairs);
        if (!double.IsFinite(entryLagSeconds) || entryLagSeconds < 0d)
        {
            throw new ArgumentException("Entry lag must be finite and non-negative.", nameof(entryLagSeconds));
        }

        if (!IsValidCrossfade(crossfadeSeconds, entryLagSeconds))
        {
            throw new ArgumentException("Crossfade duration must be finite and longer than the entry lag.", nameof(crossfadeSeconds));
        }

        ImmutableDictionary<string, SeamClip>.Builder byRole =
            ImmutableDictionary.CreateBuilder<string, SeamClip>(StringComparer.Ordinal);
        foreach (SeamClip clip in clips)
        {
            if (!byRole.TryAdd(clip.Role, clip))
            {
                throw new ArgumentException($"Seam clip role '{clip.Role}' is declared more than once.", nameof(clips));
            }
        }

        var evaluator = new WindowEvaluator(entryLagSeconds);
        HashSet<(string, string)> seen = [];
        ImmutableArray<SeamPair>.Builder built = ImmutableArray.CreateBuilder<SeamPair>();
        foreach (SeamPairSpec spec in pairs)
        {
            if (!byRole.TryGetValue(spec.OutgoingRole, out SeamClip? outgoing)
                || !byRole.TryGetValue(spec.IncomingRole, out SeamClip? incoming))
            {
                throw new ArgumentException(
                    $"Seam pair '{spec.OutgoingRole}' → '{spec.IncomingRole}' names an unknown clip.",
                    nameof(pairs));
            }

            if (!seen.Add((spec.OutgoingRole, spec.IncomingRole)))
            {
                throw new ArgumentException(
                    $"Seam pair '{spec.OutgoingRole}' → '{spec.IncomingRole}' is declared more than once.",
                    nameof(pairs));
            }

            double crossfade = spec.CrossfadeSeconds ?? crossfadeSeconds;
            if (!IsValidCrossfade(crossfade, entryLagSeconds))
            {
                throw new ArgumentException(
                    $"Seam pair '{spec.OutgoingRole}' → '{spec.IncomingRole}' has a crossfade that is not longer than the entry lag.",
                    nameof(pairs));
            }

            built.Add(evaluator.Evaluate(spec.Kind, outgoing, incoming, spec.MinimumOutgoingSeconds, crossfade));
        }

        ImmutableArray<SeamPairExclusion> excluded = [.. excludedPairs ?? []];
        return excluded.Any(exclusion => seen.Contains((exclusion.Pair.OutgoingRole, exclusion.Pair.IncomingRole)))
            ? throw new ArgumentException("A seam pair cannot be both analysed and excluded.", nameof(excludedPairs))
            : new SeamTable(crossfadeSeconds, entryLagSeconds, byRole.ToImmutable(), built.ToImmutable(), excluded);
    }

    private static bool IsValidCrossfade(double crossfadeSeconds, double entryLagSeconds)
        => double.IsFinite(crossfadeSeconds) && crossfadeSeconds > entryLagSeconds + TimeTolerance;

    /// <summary>Whether an ordered clip pair is in the planner set; the planner never crossfades outside it.</summary>
    public bool IsInPlannerSet(string outgoingRole, string incomingRole)
        => _byRoles.ContainsKey((outgoingRole, incomingRole));

    /// <summary>Whether a clip is in the planner set (some analysed pair includes it).</summary>
    public bool IsInPlannerSet(string role)
        => Clips.ContainsKey(role) && !ExcludedClips.Any(exclusion => exclusion.Role == role);

    /// <summary>
    /// Whether the planner may enter a cyclic clip or looping confined manoeuvre under a tolerance: the clip is in the
    /// planner set and has at least one aligned exit seam per cycle or loop (NAV-001 Technical Requirement 7 cyclic
    /// clip availability). A cycle's exits are its cycle → exit seams; a looping manoeuvre's are its seams into Idle
    /// (<see cref="AvailabilityExitKind"/>). Such a clip in the set without one is unavailable; one outside the set is
    /// excluded; any other clip is not subject to availability and is never plannable here.
    /// </summary>
    public bool IsPlannable(string role, float toleranceMetres)
        => Clips.TryGetValue(role, out SeamClip? clip)
            && AvailabilityExitKind(clip) is { } exitKind
            && Pairs.Any(pair => pair.Kind == exitKind
                && pair.Outgoing == clip
                && pair.Candidates.Any(candidate => pair.IsUsable(candidate) && candidate.IsAlignedWithin(toleranceMetres)));

    /// <summary>
    /// The seam kind whose seams are the availability exits of a clip subject to cyclic clip availability:
    /// <see cref="SeamKind.CycleToExit"/> for a cyclic clip, <see cref="SeamKind.ManoeuvreToIdle"/> for a looping
    /// (wrapping) confined manoeuvre, and <see langword="null"/> for every other clip.
    /// </summary>
    public static SeamKind? AvailabilityExitKind(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Kind == SeamClipRole.Cycle
            ? SeamKind.CycleToExit
            : clip.Kind == SeamClipRole.Confined && clip.WrapsPlayback ? SeamKind.ManoeuvreToIdle : null;
    }

    /// <summary>
    /// Re-checks an arbitrary seam <c>(a, b)</c> of a planner-set pair, for example one shifted off the native grid
    /// because the crossfade could not start exactly at a tabulated outgoing time. Applies the same window fit, slice
    /// head, phase-agreement and pose-separation evaluation as the table. Returns <see langword="false"/> when the pair
    /// is outside the planner set or the window does not fit or does not agree in phase; the caller then compares
    /// <paramref name="candidate"/>'s separation with the tolerance in force and <see cref="SeamPair.IsUsable"/>.
    /// Evaluation reuses the table's scratch buffers, so it does not allocate but is not thread-safe.
    /// </summary>
    public bool TryEvaluateSeam(
        string outgoingRole,
        string incomingRole,
        double outgoingSeconds,
        double incomingSeconds,
        out SeamCandidate candidate)
    {
        candidate = default;
        return _byRoles.TryGetValue((outgoingRole, incomingRole), out SeamPair? pair)
            && outgoingSeconds >= pair.MinimumOutgoingSeconds - TimeTolerance
            && _evaluator.TryEvaluate(pair.Outgoing, outgoingSeconds, pair.Incoming, incomingSeconds, pair.CrossfadeSeconds, out candidate);
    }

    /// <summary>Looks up an analysed pair.</summary>
    public bool TryGetPair(string outgoingRole, string incomingRole, out SeamPair? pair)
        => _byRoles.TryGetValue((outgoingRole, incomingRole), out pair);

    /// <summary>
    /// Aligned seams of a pair under a tolerance, ordered by separation (smallest first). Empty when the pair is not
    /// analysed.
    /// </summary>
    /// <remarks>
    /// The returned seam is native-grid aligned: request the pair's crossfade when outgoing playback reaches its
    /// outgoing time, with the incoming clip sought to its incoming time, or re-check a shifted pair with
    /// <see cref="TryEvaluateSeam"/> (see the class remarks). Slice joins without a planted foot throughout are never returned (<see cref="SeamPair.IsUsable"/>).
    /// </remarks>
    public IEnumerable<SeamCandidate> AlignedSeams(string outgoingRole, string incomingRole, float toleranceMetres)
    {
        return !_byRoles.TryGetValue((outgoingRole, incomingRole), out SeamPair? pair)
            ? []
            : pair.Candidates
            .Where(candidate => pair.IsUsable(candidate) && candidate.IsAlignedWithin(toleranceMetres))
            .OrderBy(candidate => candidate.MaxSeparation)
            .ThenBy(candidate => candidate.OutgoingTime);
    }

    /// <summary>
    /// The earliest aligned seam of one pair: its outgoing time comes first at or after <paramref name="fromSeconds"/>
    /// of outgoing playback, and ties break by the lowest pose separation
    /// (<see cref="SeamCandidate.MaxSeparation"/>); no planted-separation preference applies on this path. This is the
    /// prompt choice for cancellation and supersession (NAV-001 Technical Requirement 14). A wrapping outgoing clip
    /// continues into its next cycle; a clip played once has no seam after its last one.
    /// </summary>
    /// <remarks>
    /// The returned seam is native-grid aligned: request the pair's crossfade when outgoing playback reaches its
    /// outgoing time, with the incoming clip sought to its incoming time, or re-check a shifted pair with
    /// <see cref="TryEvaluateSeam"/> (see the class remarks). Slice joins without a planted foot throughout are never returned (<see cref="SeamPair.IsUsable"/>).
    /// </remarks>
    public bool TryFindNextAligned(
        string outgoingRole,
        string incomingRole,
        double fromSeconds,
        float toleranceMetres,
        out SeamChoice choice)
    {
        choice = default;
        return _byRoles.TryGetValue((outgoingRole, incomingRole), out SeamPair? pair)
            && TrySelect(pair, fromSeconds, toleranceMetres, double.PositiveInfinity, false, out choice);
    }

    /// <summary>
    /// The earliest aligned seam from one outgoing clip into any of <paramref name="incomingRoles"/> (for example every
    /// exit lane, selected by stride phase), breaking ties by the lowest pose separation
    /// (<see cref="SeamCandidate.MaxSeparation"/>); no planted-separation preference applies on this path.
    /// </summary>
    /// <remarks>
    /// The returned seam is native-grid aligned: request the pair's crossfade when outgoing playback reaches its
    /// outgoing time, with the incoming clip sought to its incoming time, or re-check a shifted pair with
    /// <see cref="TryEvaluateSeam"/> (see the class remarks). Slice joins without a planted foot throughout are never returned (<see cref="SeamPair.IsUsable"/>).
    /// </remarks>
    public bool TryFindNextAligned(
        string outgoingRole,
        IEnumerable<string> incomingRoles,
        double fromSeconds,
        float toleranceMetres,
        out SeamChoice choice)
        => TrySelectAcross(outgoingRole, incomingRoles, fromSeconds, toleranceMetres, double.PositiveInfinity, false, out choice);

    /// <summary>
    /// The lowest-separation aligned seam of one pair whose outgoing time lies within
    /// <paramref name="maxDelaySeconds"/> of playback after <paramref name="fromSeconds"/>, breaking ties by the
    /// shorter delay. The delay bound lets the planner trade waiting against pose separation; an infinite bound picks
    /// the pair's lowest-separation seam over one whole cycle of a wrapping clip.
    /// </summary>
    /// <remarks>
    /// The returned seam is native-grid aligned: request the pair's crossfade when outgoing playback reaches its
    /// outgoing time, with the incoming clip sought to its incoming time, or re-check a shifted pair with
    /// <see cref="TryEvaluateSeam"/> (see the class remarks). Slice joins without a planted foot throughout are never returned (<see cref="SeamPair.IsUsable"/>).
    /// </remarks>
    public bool TryFindLowestSeparationAligned(
        string outgoingRole,
        string incomingRole,
        double fromSeconds,
        float toleranceMetres,
        double maxDelaySeconds,
        out SeamChoice choice)
    {
        choice = default;
        return _byRoles.TryGetValue((outgoingRole, incomingRole), out SeamPair? pair)
            && TrySelect(pair, fromSeconds, toleranceMetres, maxDelaySeconds, true, out choice);
    }

    /// <summary>
    /// The lowest-separation aligned seam from one outgoing clip into any of <paramref name="incomingRoles"/> within
    /// <paramref name="maxDelaySeconds"/> of playback, breaking ties by the shorter delay.
    /// </summary>
    /// <remarks>
    /// The returned seam is native-grid aligned: request the pair's crossfade when outgoing playback reaches its
    /// outgoing time, with the incoming clip sought to its incoming time, or re-check a shifted pair with
    /// <see cref="TryEvaluateSeam"/> (see the class remarks). Slice joins without a planted foot throughout are never returned (<see cref="SeamPair.IsUsable"/>).
    /// </remarks>
    public bool TryFindLowestSeparationAligned(
        string outgoingRole,
        IEnumerable<string> incomingRoles,
        double fromSeconds,
        float toleranceMetres,
        double maxDelaySeconds,
        out SeamChoice choice)
        => TrySelectAcross(outgoingRole, incomingRoles, fromSeconds, toleranceMetres, maxDelaySeconds, true, out choice);

    private bool TrySelectAcross(
        string outgoingRole,
        IEnumerable<string> incomingRoles,
        double fromSeconds,
        float toleranceMetres,
        double maxDelaySeconds,
        bool preferSeparation,
        out SeamChoice choice)
    {
        ArgumentNullException.ThrowIfNull(incomingRoles);
        choice = default;
        bool found = false;
        foreach (string incomingRole in incomingRoles)
        {
            if (_byRoles.TryGetValue((outgoingRole, incomingRole), out SeamPair? pair)
                && TrySelect(pair, fromSeconds, toleranceMetres, maxDelaySeconds, preferSeparation, out SeamChoice next)
                && (!found || IsPreferred(next, choice, preferSeparation)))
            {
                choice = next;
                found = true;
            }
        }

        return found;
    }

    /// <summary>
    /// Selects the pair's preferred usable, tolerance-aligned seam at or after <paramref name="fromSeconds"/> of
    /// outgoing playback, a wrapping clip's earlier candidates counting at their next-cycle delay, within
    /// <paramref name="maxDelaySeconds"/>: the earliest, with ties by the lowest pose separation
    /// (<see cref="SeamCandidate.MaxSeparation"/>), when <paramref name="preferSeparation"/> is false, or the lowest
    /// planted separation, then pose separation, then the shortest delay, when true. No planted-separation window is
    /// applied on either path.
    /// </summary>
    private static bool TrySelect(
        SeamPair pair,
        double fromSeconds,
        float toleranceMetres,
        double maxDelaySeconds,
        bool preferSeparation,
        out SeamChoice choice)
    {
        SeamClip outgoing = pair.Outgoing;
        double from = outgoing.Normalise(fromSeconds);
        choice = default;
        bool found = false;
        foreach (SeamCandidate candidate in pair.Candidates)
        {
            // Slice joins need a foot planted throughout (NAV-001 Technical Requirement 6); with phase agreement this
            // is the same anatomical foot planted in both clips at every instant of the join.
            if (!pair.IsUsable(candidate) || !candidate.IsAlignedWithin(toleranceMetres))
            {
                continue;
            }

            double delay = candidate.OutgoingTime - from;
            if (delay < -TimeTolerance)
            {
                if (!outgoing.WrapsPlayback)
                {
                    continue;
                }

                delay += outgoing.DurationSeconds;
            }

            var next = new SeamChoice(pair, candidate, Math.Max(delay, 0d));
            if (next.DelaySeconds <= maxDelaySeconds + TimeTolerance
                && (!found || IsPreferred(next, choice, preferSeparation)))
            {
                choice = next;
                found = true;
            }
        }

        return found;
    }

    private static bool IsPreferred(SeamChoice next, SeamChoice current, bool preferSeparation)
    {
        bool sameDelay = Math.Abs(next.DelaySeconds - current.DelaySeconds) <= TimeTolerance;
        bool earlier = next.DelaySeconds < current.DelaySeconds - TimeTolerance;
        return preferSeparation && next.Candidate.PlantedSeparation != current.Candidate.PlantedSeparation
            ? next.Candidate.PlantedSeparation < current.Candidate.PlantedSeparation
            : preferSeparation
            ? next.Separation < current.Separation || (next.Separation == current.Separation && earlier)
            : earlier || (sameDelay && next.Separation < current.Separation);
    }

    /// <summary>
    /// Evaluates seam windows under one entry lag (see the class remarks), reusing scratch buffers across pairs.
    /// </summary>
    private sealed class WindowEvaluator(double lag)
    {
        private readonly List<double> _breakpoints = [];
        private readonly List<double> _poseOffsets = [];

        public SeamPair Evaluate(SeamKind kind, SeamClip outgoing, SeamClip incoming, double minimumOutgoingSeconds, double crossfade)
        {
            double window = crossfade - lag;
            ImmutableArray<SeamCandidate>.Builder candidates = ImmutableArray.CreateBuilder<SeamCandidate>();
            int evaluated = 0;
            for (int i = 0; i < outgoing.GridTimeCount; i++)
            {
                double a = outgoing.GridTime(i);
                if (a < minimumOutgoingSeconds - TimeTolerance || !outgoing.WindowFits(a, crossfade))
                {
                    continue;
                }

                for (int j = 0; j < incoming.GridTimeCount; j++)
                {
                    double b = incoming.GridTime(j);
                    if (!incoming.WindowFits(b, window))
                    {
                        continue;
                    }

                    evaluated++;
                    if (TryEvaluateFitting(outgoing, a, incoming, b, window, out SeamCandidate candidate))
                    {
                        candidates.Add(candidate);
                    }
                }
            }

            return new SeamPair(
                kind,
                outgoing,
                incoming,
                evaluated,
                [.. candidates.OrderBy(value => value.OutgoingTime).ThenBy(value => value.MaxSeparation)],
                minimumOutgoingSeconds,
                crossfade,
                lag);
        }

        /// <summary>Evaluates one seam at arbitrary playback positions, or fails when its window does not fit.</summary>
        public bool TryEvaluate(SeamClip outgoing, double a, SeamClip incoming, double b, double crossfade, out SeamCandidate candidate)
        {
            candidate = default;
            double window = crossfade - lag;
            return double.IsFinite(a)
                && double.IsFinite(b)
                && a >= -TimeTolerance
                && b >= -TimeTolerance
                && outgoing.WindowFits(a, crossfade)
                && incoming.WindowFits(b, window)
                && TryEvaluateFitting(outgoing, outgoing.Normalise(a), incoming, incoming.Normalise(b), window, out candidate);
        }

        /// <summary>
        /// Evaluates seam <c>(a, b)</c>: the outgoing clip from <c>a + λ</c> against the incoming clip from <c>b</c> over
        /// the blend window.
        /// </summary>
        private bool TryEvaluateFitting(SeamClip outgoing, double a, SeamClip incoming, double b, double window, out SeamCandidate candidate)
        {
            candidate = default;
            double blendStart = outgoing.WrapsPlayback ? outgoing.Normalise(a + lag) : a + lag;
            if (!PhaseAgrees(outgoing, blendStart, incoming, b, window, out bool support))
            {
                return false;
            }

            (float left, float right, float planted, float swing) = MeasureSeparation(outgoing, blendStart, incoming, b, window);
            candidate = new SeamCandidate(a, b, left, right, support) { PlantedSeparation = planted, SwingSeparation = swing };
            return true;
        }

        /// <summary>Exact piecewise-constant comparison of both feet's states over <c>[0, w)</c>.</summary>
        private bool PhaseAgrees(SeamClip outgoing, double a, SeamClip incoming, double b, double window, out bool support)
        {
            _breakpoints.Clear();
            _breakpoints.Add(0d);
            outgoing.AddStateBreakpoints(a, window, _breakpoints);
            incoming.AddStateBreakpoints(b, window, _breakpoints);
            _breakpoints.Add(window);
            _breakpoints.Sort();

            support = true;
            double previous = 0d;
            for (int index = 1; index < _breakpoints.Count; index++)
            {
                double next = _breakpoints[index];
                if (next - previous <= TimeTolerance)
                {
                    // Boundaries of both clips that coincide within tolerance bound no piece of their own.
                    continue;
                }

                double tau = 0.5d * (previous + next);
                bool outLeft = outgoing.IsPlanted(StandingLocomotionAnatomicalSide.Left, a + tau);
                bool outRight = outgoing.IsPlanted(StandingLocomotionAnatomicalSide.Right, a + tau);
                if (outLeft != incoming.IsPlanted(StandingLocomotionAnatomicalSide.Left, b + tau)
                    || outRight != incoming.IsPlanted(StandingLocomotionAnatomicalSide.Right, b + tau))
                {
                    return false;
                }

                support &= outLeft || outRight;
                previous = next;
            }

            return true;
        }

        /// <summary>
        /// Largest per-foot separation over <c>[0, w]</c>, evaluated where it can peak: at every key offset of either
        /// clip inside the window (including a wrapping clip's loop point) and at both window ends.
        /// </summary>
        private (float Left, float Right, float Planted, float Swing) MeasureSeparation(SeamClip outgoing, double a, SeamClip incoming, double b, double window)
        {
            _poseOffsets.Clear();
            _poseOffsets.Add(0d);
            outgoing.AddKeyOffsets(a, window, _poseOffsets);
            incoming.AddKeyOffsets(b, window, _poseOffsets);
            _poseOffsets.Add(window);

            float left = 0f;
            float right = 0f;
            float planted = 0f;
            float swing = 0f;
            foreach (double tau in _poseOffsets)
            {
                left = Math.Max(left, Separation(StandingLocomotionAnatomicalSide.Left, tau));
                right = Math.Max(right, Separation(StandingLocomotionAnatomicalSide.Right, tau));
            }

            return (left, right, planted, swing);

            // Both one-sided limits, so a wrapping clip's loop point bounds the key intervals on either side.
            float Separation(StandingLocomotionAnatomicalSide foot, double tau)
            {
                float separation = Math.Max(
                    outgoing.Ball(foot, a + tau, false).DistanceTo(incoming.Ball(foot, b + tau, false)),
                    outgoing.Ball(foot, a + tau, true).DistanceTo(incoming.Ball(foot, b + tau, true)));
                // Match the split probe: classify boundary samples just inside the phase window.
                double phaseTau = tau >= window ? Math.Max(0d, tau - 1e-8d) : tau + 1e-8d;
                if (outgoing.IsPlanted(foot, a + phaseTau))
                {
                    planted = Math.Max(planted, separation);
                }
                else
                {
                    swing = Math.Max(swing, separation);
                }
                return separation;
            }
        }
    }
}

/// <summary>Why a clip or candidate pair is outside the planner set.</summary>
public enum SeamExclusionReason
{
    /// <summary>
    /// A lateral cycle: it has no entry or exit of its own, so the planner does not use it, nor any pair with it.
    /// </summary>
    LateralCycle,

    /// <summary>
    /// A lane switch between the two arcs whose lanes are on their turning sides (<c>al_l ↔ ar_r</c>): opposite turns
    /// on opposite lanes.
    /// </summary>
    TurningSideLaneArcSwitch,

    /// <summary>An entry into an arc whose lane is on its turning side, from the entry on the other side.</summary>
    WrongLaneArcEntry,

    /// <summary>A clip that no planner-set pair includes.</summary>
    NoPlannedPair,
}

/// <summary>A candidate pair outside the planner set, and why.</summary>
/// <param name="Pair">The excluded pair.</param>
/// <param name="Reason">Why it is excluded.</param>
public readonly record struct SeamPairExclusion(SeamPairSpec Pair, SeamExclusionReason Reason);

/// <summary>A clip outside the planner set, and why.</summary>
/// <param name="Role">Clip role.</param>
/// <param name="Reason">Why it is excluded.</param>
public readonly record struct SeamClipExclusion(string Role, SeamExclusionReason Reason);

/// <summary>The planner set of one binding: the analysed pairs, and the candidate pairs excluded from it.</summary>
/// <param name="Pairs">Pairs the planner may crossfade between.</param>
/// <param name="ExcludedPairs">Candidate pairs outside the set, with reasons.</param>
public sealed record SeamPlannerSet(ImmutableArray<SeamPairSpec> Pairs, ImmutableArray<SeamPairExclusion> ExcludedPairs);

/// <summary>
/// The planner set of NAV-001 Technical Requirement 7: the clip pairs and seam kinds the arrival planner uses, derived
/// from the clips' planner roles and capabilities. It is a planner design decision; ANIM-002 clips and pairs outside it
/// are excluded from planning, not defects.
/// </summary>
public static class SeamPairPolicy
{
    /// <summary>
    /// Candidate pairs by seam kind, each either in the planner set or excluded with a <see cref="SeamExclusionReason"/>:
    /// <list type="bullet">
    /// <item>entry → cycle: forward entries into the forward cycle and the arcs, backward entries into the backward
    /// cycle. An arc whose lane is on its turning side (<c>al_l</c>, <c>ar_r</c>) is entered only from the entry on
    /// that side (<see cref="SeamExclusionReason.WrongLaneArcEntry"/>);</item>
    /// <item>cycle → cycle lane switches: between every two forward-going cycles (forward cycle and arcs), except
    /// between the two turning-side-lane arcs (<see cref="SeamExclusionReason.TurningSideLaneArcSwitch"/>);</item>
    /// <item>cycle → exit: forward-going cycles into the forward exits, the backward cycle into the backward exits
    /// (either lane; phase selects);</item>
    /// <item>slice joins: every forward entry into every forward exit, every backward entry into every backward exit,
    /// with a head that reaches the entry's first lift-off (<see cref="FirstLiftOffSeconds"/>), so the join is not the
    /// standing-still start of the entry;</item>
    /// <item>manoeuvre ↔ Idle: Idle into every entry and confined manoeuvre, every exit and confined manoeuvre into
    /// Idle.</item>
    /// </list>
    /// Lateral cycles are excluded (<see cref="SeamExclusionReason.LateralCycle"/>), with their candidate lane
    /// switches (between the lanes of one lateral direction, and with the forward cycle) and their exits into the
    /// forward exits. Each included pair carries its own crossfade duration when the crossfade policy shortens it
    /// (<see cref="SeamCrossfadePolicy.PairCrossfadeSeconds"/>).
    /// </summary>
    /// <param name="clips">Clips of one rig binding.</param>
    /// <param name="crossfades">Crossfade policy; <see cref="SeamCrossfadePolicy.Default"/> when omitted.</param>
    public static SeamPlannerSet PlannerSet(IEnumerable<SeamClip> clips, SeamCrossfadePolicy? crossfades = null)
    {
        ArgumentNullException.ThrowIfNull(clips);
        SeamCrossfadePolicy policy = crossfades ?? SeamCrossfadePolicy.Default;
        SeamClip[] all = [.. clips];
        SeamClip[] forwardCycles = [.. all.Where(clip => clip.Kind == SeamClipRole.Cycle && IsForwardGoing(clip))];
        SeamClip[] backwardCycles = [.. all.Where(clip => clip.Capability == StandingLocomotionCapability.CyclicBackwardPrimary)];
        SeamClip[] lateralCycles = [.. all.Where(IsLateralCycle)];
        SeamClip[] forwardPrimary = [.. all.Where(clip => clip.Capability == StandingLocomotionCapability.CyclicForwardPrimary)];
        SeamClip[] forwardEntries = [.. all.Where(clip => clip.Kind == SeamClipRole.Entry && !IsBackward(clip))];
        SeamClip[] backwardEntries = [.. all.Where(clip => clip.Kind == SeamClipRole.Entry && IsBackward(clip))];
        SeamClip[] forwardExits = [.. all.Where(clip => clip.Kind == SeamClipRole.Exit && !IsBackward(clip))];
        SeamClip[] backwardExits = [.. all.Where(clip => clip.Kind == SeamClipRole.Exit && IsBackward(clip))];
        SeamClip[] confined = [.. all.Where(clip => clip.Kind == SeamClipRole.Confined)];
        SeamClip[] idle = [.. all.Where(clip => clip.Kind == SeamClipRole.Idle)];

        (SeamPairSpec Pair, SeamClip From, SeamClip To)[] candidates =
        [
            .. Cross(SeamKind.EntryToCycle, forwardEntries, forwardCycles),
            .. Cross(SeamKind.EntryToCycle, backwardEntries, backwardCycles),
            .. Cross(SeamKind.CycleToCycle, forwardCycles, forwardCycles),
            .. Cross(SeamKind.CycleToCycle, lateralCycles, lateralCycles, static (from, to) => LateralDirection(from) == LateralDirection(to)),
            .. Cross(SeamKind.CycleToCycle, forwardPrimary, lateralCycles),
            .. Cross(SeamKind.CycleToCycle, lateralCycles, forwardPrimary),
            .. Cross(SeamKind.CycleToExit, [.. forwardCycles, .. lateralCycles], forwardExits),
            .. Cross(SeamKind.CycleToExit, backwardCycles, backwardExits),
            .. Cross(SeamKind.SliceJoin, [.. forwardEntries, .. backwardEntries], [.. forwardExits, .. backwardExits], static (entry, exit) => IsBackward(entry) == IsBackward(exit)),
            .. Cross(SeamKind.IdleToManoeuvre, idle, [.. forwardEntries, .. backwardEntries, .. confined]),
            .. Cross(SeamKind.ManoeuvreToIdle, [.. forwardExits, .. backwardExits, .. confined], idle),
            .. all.Where(clip => clip.Capability is StandingLocomotionCapability.ConfinedPivotLeft or StandingLocomotionCapability.ConfinedPivotRight)
                .Select(clip => (new SeamPairSpec(SeamKind.PivotContinuation, clip.Role, clip.Role), clip, clip)),
        ];

        ImmutableArray<SeamPairSpec>.Builder included = ImmutableArray.CreateBuilder<SeamPairSpec>();
        ImmutableArray<SeamPairExclusion>.Builder excluded = ImmutableArray.CreateBuilder<SeamPairExclusion>();
        foreach ((SeamPairSpec pair, SeamClip from, SeamClip to) in candidates)
        {
            if (ExclusionOf(pair.Kind, from, to) is { } reason)
            {
                excluded.Add(new SeamPairExclusion(pair, reason));
            }
            else
            {
                included.Add(pair with
                {
                    MinimumOutgoingSeconds = pair.Kind == SeamKind.SliceJoin ? FirstLiftOffSeconds(from) : 0d,
                    CrossfadeSeconds = policy.PairCrossfadeSeconds(pair.Kind, from, to),
                });
            }
        }

        return new SeamPlannerSet(included.ToImmutable(), excluded.ToImmutable());
    }

    /// <summary>The planner-set pairs of <see cref="PlannerSet"/> under the default crossfade policy.</summary>
    public static IEnumerable<SeamPairSpec> Relevant(IEnumerable<SeamClip> clips) => PlannerSet(clips).Pairs;

    /// <summary>Why a clip that no planner-set pair includes is excluded.</summary>
    public static SeamExclusionReason ExclusionReasonOf(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return IsLateralCycle(clip) ? SeamExclusionReason.LateralCycle : SeamExclusionReason.NoPlannedPair;
    }

    private static SeamExclusionReason? ExclusionOf(SeamKind kind, SeamClip from, SeamClip to)
    {
        return IsLateralCycle(from) || IsLateralCycle(to)
            ? SeamExclusionReason.LateralCycle
            : kind == SeamKind.EntryToCycle && IsTurningSideLaneArc(to) && from.Capability?.Side() != to.Capability?.Side()
            ? SeamExclusionReason.WrongLaneArcEntry
            : kind == SeamKind.CycleToCycle && IsTurningSideLaneArc(from) && IsTurningSideLaneArc(to)
                ? SeamExclusionReason.TurningSideLaneArcSwitch
                : null;
    }

    /// <summary>
    /// Source time of a clip's first lift-off: the earliest start of a swing segment of either foot. A slice head that
    /// ends before it has not left the standing start.
    /// </summary>
    public static double FirstLiftOffSeconds(SeamClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        double first = clip.DurationSeconds;
        foreach (PlantedIntervalSegment segment in clip.Intervals.Left.Concat(clip.Intervals.Right))
        {
            if (!segment.IsPlanted)
            {
                first = Math.Min(first, segment.StartSeconds);
            }
        }

        return first;
    }

    private static IEnumerable<(SeamPairSpec Pair, SeamClip From, SeamClip To)> Cross(
        SeamKind kind,
        IEnumerable<SeamClip> outgoing,
        IEnumerable<SeamClip> incoming,
        Func<SeamClip, SeamClip, bool>? include = null)
    {
        SeamClip[] targets = [.. incoming];
        foreach (SeamClip from in outgoing)
        {
            foreach (SeamClip to in targets)
            {
                if (!ReferenceEquals(from, to) && (include?.Invoke(from, to) ?? true))
                {
                    yield return (new SeamPairSpec(kind, from.Role, to.Role), from, to);
                }
            }
        }
    }

    private static bool IsBackward(SeamClip clip) => clip.Capability?.IsBackward() ?? false;

    private static bool IsForwardGoing(SeamClip clip) => clip.Capability is
        StandingLocomotionCapability.CyclicForwardPrimary
        or StandingLocomotionCapability.CyclicArcLeftLeftLane
        or StandingLocomotionCapability.CyclicArcLeftRightLane
        or StandingLocomotionCapability.CyclicArcRightLeftLane
        or StandingLocomotionCapability.CyclicArcRightRightLane;

    /// <summary>An arc whose lane is on its turning side: <c>al_l</c> or <c>ar_r</c>.</summary>
    private static bool IsTurningSideLaneArc(SeamClip clip) => clip.Capability is
        StandingLocomotionCapability.CyclicArcLeftLeftLane
        or StandingLocomotionCapability.CyclicArcRightRightLane;

    private static bool IsLateralCycle(SeamClip clip) => clip.Kind == SeamClipRole.Cycle && LateralDirection(clip) != 0;

    private static int LateralDirection(SeamClip clip)
    {
        bool left = clip.Capability is StandingLocomotionCapability.CyclicLateralLeftLeftLane
            or StandingLocomotionCapability.CyclicLateralLeftRightLane;
        bool right = clip.Capability is StandingLocomotionCapability.CyclicLateralRightLeftLane
            or StandingLocomotionCapability.CyclicLateralRightRightLane;
        return left ? -1 : right ? 1 : 0;
    }
}
