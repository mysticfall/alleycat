using System.Collections.Immutable;
using System.Diagnostics;
using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>
/// The NAV-001 arrival planner (Technical Requirements 5–7, 13 and 14): from the clip the NPC is playing and a
/// destination with a final facing, it chooses a sequence of clip segments, joined only at aligned seams of the
/// planner set, that ends in Idle, and predicts it exactly. It is pure C#, bound once per rig to the seam table and the
/// <see cref="SequencePredictor"/>.
/// </summary>
/// <remarks>
/// <para>Movement choice (A5) follows <see cref="ArrivalMovementChoice"/>. Each family composes Idle-to-Idle blocks:</para>
/// <list type="bullet">
/// <item>turn only: a final pivot block (<see cref="ArrivalStage.Face"/>);</item>
/// <item>side-step: a side-step block, then the final pivot;</item>
/// <item>straight and turn first: an alignment pivot (bounded for straight), a walk (a slice pair, or entry → forward
/// cycle → exit), then the final pivot;</item>
/// <item>arc: an optional lead pivot towards the target side, entry → arc (→ forward cycle) → exit, then the final
/// pivot.</item>
/// </list>
/// <para>
/// Heading follows the path while travelling (the alignment pivot faces the walk towards the target); the final facing
/// shapes only the final pivot. A pivot or side-step block is Idle → looping manoeuvre (whole loops plus a partial
/// step from any aligned entry to any aligned exit) → Idle. A looping pivot may also repeat one phase-complete self
/// seam: a homogeneous chain of restart units (restart fade, native solo back to the seam's outgoing time, entry
/// seek) whose additional units and extra native loops are charged to the same extra-native reserve on both yaw and
/// ticks, with the seam chosen per request by complete-chain ranking rather than a bind-time throughput table.
/// </para>
/// <para>
/// Search, bounded: at bind time every join is a seam of the table aligned within the predictor's seam tolerance, each
/// with its exact crossfade motion. Direct Idle → entry → exit → Idle blocks keep every legal entry seek, incoming
/// cut and exit cut and are ranked by complete motion; SeamOptionsPerPair, WalkEntryOptions and ExitToIdleOptions do
/// not limit these blocks. Other menus keep the lowest-separation seam per distinct outgoing (or entry) time. Direct
/// blocks, pivot and side-step blocks and forward-walk tails are tabulated and sorted by their motion. Per request, walk
/// prefixes look up the tails whose distance fits, and alignment and final pivots are looked up by the yaw they need;
/// each composition is costed with the endpoint model (exact crossfade motions composed with closed-form solo
/// playback), within <see cref="ArrivalPlannerConfig.MaxCompositions"/>. The best finalists are placed on the Idle
/// seams they actually reach, predicted exactly in summary mode, and the lowest residual wins; residuals within
/// <see cref="ArrivalPlannerConfig.ResidualTieMetres"/> tie. Idle-start stationary turns prefer fewer interior Idle
/// bridges, shorter playback, fewer pieces, then lower seam separation; mixed searches retain their legacy
/// steps/separation/duration order. The winner's
/// exact summary carries the support windows for swing-phase correction; the per-tick prediction is <see cref="Predict"/>.
/// </para>
/// <para>
/// A straight request also searches gentle arcs into the walk, within the same alignment-pivot bound: the pivots'
/// aligned seams leave heading gaps that an arc's continuous turn fills.
/// </para>
/// <para>
/// Rules: every crossfade is a usable seam of a planner-set pair aligned within the tolerance, requested exactly at
/// its grid outgoing time with the pair's crossfade and never while another crossfade runs (each segment plays at
/// least the crossfade's remaining ticks); a plan never teleports. A mid-clip start leaves its clip through the same
/// seams: to Idle (then planned onwards), or, from a travelling clip, by travelling on to the target. With
/// <see cref="ArrivalStart.Prompt"/> the first transition is the clip's first aligned seam. No aligned plan ends
/// <see cref="ArrivalPlanStatus.Failed"/>.
/// </para>
/// <para>Thread safety: not thread-safe (it shares the predictor and the seam table's scratch buffers).</para>
/// </remarks>
public sealed class ArrivalPlanner
{
    private const double TimeTolerance = 1e-6d;

    /// <summary>
    /// Largest expected difference between a candidate's endpoint-model cost and its exact cost (the model omits Idle
    /// dwell root motion and a running start crossfade), in metres.
    /// </summary>
    private const double EstimateMarginMetres = 0.0001d;

    /// <summary>
    /// Slack of the distance lower bound: alignment and final pivot blocks may shift the actor a little, so a
    /// travel's distance miss bounds the residual only up to the largest pivot-block offset plus this, in metres.
    /// </summary>
    private const double BoundSlackMarginMetres = 0.001d;

    /// <summary>
    /// Bearing slack of the walk-head pre-check: the tail's own lateral drift and the pivot quantisation, in radians.
    /// </summary>
    private static readonly double _alignSlackRadians = Mathf.DegToRad(3d);

    private readonly double _boundSlack;

    private readonly Dictionary<string, double> _lengths = new(StringComparer.Ordinal);

    private readonly SeamTable _seams;
    private readonly SequencePredictor _predictor;
    private readonly float _tolerance;
    private readonly double _dt;
    private readonly SeamClip _idle;
    private readonly string? _walk;
    private readonly string[] _entries;
    private readonly string[] _exits;
    private readonly HashSet<SeamPair> _used = [];
    private readonly Dictionary<string, Dictionary<int, IdleSlot[]>> _idleSlots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Join[]> _exitToIdle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Leave[]> _stops = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Leave[]> _switches = new(StringComparer.Ordinal);
    private readonly Manoeuvre[] _pivots;
    private readonly PivotContinuation[] _pivotContinuations;
    private readonly RepeatedPivotFamily[] _repeatFamilies;
    private readonly double[] _pivotYaws;

    /// <summary>
    /// The capped-correction schedule terminal continuations are assessed with: the same default
    /// <see cref="SwingCorrectionCaps"/> the navigation component's runtime allocator applies, so admissibility and
    /// execution share one set of limits.
    /// </summary>
    private readonly SwingCorrectionAllocator _terminalCorrection = new(new SwingCorrectionCaps());
    private readonly Manoeuvre[] _steps;
    private readonly WalkPrefix[] _walkPrefixes;
    private readonly SliceWalk[] _sliceWalks;
    private readonly WalkPrefix[] _arcPrefixes;
    private readonly ArcWalks[] _arcWalks;
    private readonly Dictionary<int, (ArcStop[] Stops, double[] Distances)> _arcStops;
    private readonly double[] _sliceDistances;
    private readonly double[] _stepDistances;
    private readonly Tail[][] _walkTails;
    private readonly double[][] _walkTailKeys;
    private readonly string[] _arcRoles;

    /// <summary>Binds the planner to one rig's seam table and predictor.</summary>
    /// <param name="seams">The rig's seam table (the predictor's).</param>
    /// <param name="predictor">The rig's predictor; its seam tolerance is the tolerance in force.</param>
    /// <param name="config">Tunables; <see cref="ArrivalPlannerConfig.Default"/> when omitted.</param>
    /// <exception cref="ArgumentException">The table has no Idle clip.</exception>
    public ArrivalPlanner(SeamTable seams, SequencePredictor predictor, ArrivalPlannerConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(seams);
        ArgumentNullException.ThrowIfNull(predictor);
        _seams = seams;
        _predictor = predictor;
        Config = config ?? ArrivalPlannerConfig.Default;
        _tolerance = predictor.SeamToleranceMetres;
        _dt = seams.EntryLagSeconds;
        _idle = seams.Clips.Values.SingleOrDefault(clip => clip.Kind == SeamClipRole.Idle)
            ?? throw new ArgumentException("The seam table has no single Idle clip.", nameof(seams));
        foreach (SeamClip clip in seams.Clips.Values)
        {
            _lengths[clip.Role] = clip.DurationSeconds;
        }

        _walk = RoleOf(StandingLocomotionCapability.CyclicForwardPrimary);
        _entries = Roles(StandingLocomotionCapability.EntryForwardLeft, StandingLocomotionCapability.EntryForwardRight);
        _exits = Roles(StandingLocomotionCapability.ExitForwardLeft, StandingLocomotionCapability.ExitForwardRight);
        string[] arcs = [.. Roles(
                StandingLocomotionCapability.CyclicArcLeftLeftLane,
                StandingLocomotionCapability.CyclicArcLeftRightLane,
                StandingLocomotionCapability.CyclicArcRightLeftLane,
                StandingLocomotionCapability.CyclicArcRightRightLane)
            .Where(role => seams.IsPlannable(role, _tolerance))];
        string[] pivots = [.. Roles(StandingLocomotionCapability.ConfinedPivotLeft, StandingLocomotionCapability.ConfinedPivotRight).Where(IsEnterable)];
        string[] steps = [.. Roles(StandingLocomotionCapability.ConfinedSideStepLeft, StandingLocomotionCapability.ConfinedSideStepRight).Where(IsEnterable)];
        if (_walk is not null && !seams.IsPlannable(_walk, _tolerance))
        {
            _walk = null;
        }

        foreach (string exit in _exits)
        {
            _exitToIdle[exit] = [.. Menu(exit, _idle.Role, byOutgoing: true, 0).OrderBy(join => join.A)];
        }

        string[] cycles = _walk is null ? arcs : [_walk, .. arcs];
        foreach (string cycle in cycles)
        {
            int maxCycles = cycle == _walk ? Config.MaxWalkCycles : Config.MaxArcCycles;
            int options = cycle == _walk ? Config.SeamOptionsPerPair : Config.ArcSeamOptionsPerPair;
            _stops[cycle] = [.. _exits.SelectMany(exit => Menu(cycle, exit, true, options))
                .SelectMany(join => ToIdleFrom(join).Select(toIdle => MakeLeave(join, toIdle, maxCycles)))];
            if (cycle != _walk && _walk is not null)
            {
                _switches[cycle] = [.. Menu(cycle, _walk, true, options).Select(join => MakeLeave(join, null, maxCycles))];
            }
        }

        // Walk → arc and entry → arc lane switches (a travelling redirect's first seam): the pairs join the used set
        // at bind time so travelling prefix and section menus may enumerate them.
        if (_walk is not null)
        {
            foreach (string arc in arcs)
            {
                _ = Menu(_walk, arc, true, Config.ArcSeamOptionsPerPair);
                foreach (string entry in _entries)
                {
                    _ = Menu(entry, arc, true, Config.ArcSeamOptionsPerPair);
                }
            }
        }

        _arcRoles = arcs;

        _pivots = [.. pivots.SelectMany(BuildManoeuvres).OrderBy(option => option.Motion.Yaw)];
        _pivotContinuations = [.. pivots.SelectMany(BuildPivotContinuations)];
        _repeatFamilies = [.. pivots.Select(BuildRepeatedPivotFamily)];
        _pivotYaws = [.. _pivots.Select(option => option.Motion.Yaw)];
        _boundSlack = (2d * _pivots.Select(option => option.Motion.Distance).DefaultIfEmpty(0d).Max()) + BoundSlackMarginMetres;
        _steps = [.. steps.SelectMany(BuildManoeuvres)];

        List<WalkPrefix> walkPrefixes = [];
        List<WalkPrefix> arcPrefixes = [];
        foreach (string entry in _entries)
        {
            foreach (Join fromIdle in IdleMenu(entry).Take(Math.Max(1, Config.WalkEntryOptions)))
            {
                if (_walk is not null)
                {
                    walkPrefixes.AddRange(Prefixes(fromIdle, _walk, Config.SeamOptionsPerPair));
                }

                foreach (string arc in arcs)
                {
                    arcPrefixes.AddRange(Prefixes(fromIdle, arc, Config.ArcSeamOptionsPerPair));
                }
            }
        }

        _walkPrefixes = [.. walkPrefixes];
        _sliceWalks = [.. BuildDirectSlices()];
        _arcPrefixes = [.. arcPrefixes];
        _arcWalks = [.. _arcPrefixes.Select(BuildArcWalks)];
        _arcStops = BuildArcStops();
        _sliceWalks = [.. _sliceWalks.OrderBy(slice => slice.Motion.Distance)];
        _sliceDistances = [.. _sliceWalks.Select(slice => slice.Motion.Distance)];
        _steps = [.. _steps.OrderBy(step => step.Motion.Distance)];
        _stepDistances = [.. _steps.Select(step => step.Motion.Distance)];
        (_walkTails, _walkTailKeys) = BuildWalkTails();
        UsablePairs = [.. seams.Pairs.Where(_used.Contains)];
    }

    /// <summary>
    /// The planner set the planner actually uses: the seam-table pairs its candidate families join through with at
    /// least one aligned seam, in table order. Pairs of the table outside it are never issued by this planner.
    /// </summary>
    public ImmutableArray<SeamPair> UsablePairs
    {
        get;
    }

    /// <summary>Diagnostics: every tabulated pivot block's motion (right, forward, yaw), by yaw.</summary>
    internal IEnumerable<(double Right, double Forward, double Yaw, int Steps, float Separation)> PivotBlocks
        => _pivots.Select(option => (option.Motion.X, option.Motion.Y, option.Motion.Yaw, option.Steps, option.Separation));

    /// <summary>
    /// Diagnostics: every complete repeated-pivot unit of every looping pivot, with its seam times
    /// (<c>OutgoingSeconds</c>, <c>IncomingSeconds</c>), signed unit yaw in radians and unit tick charge in seconds
    /// (the restart fade, the native solo back to the seam's outgoing time and the entry seek).
    /// </summary>
    internal IEnumerable<(string Role, double OutgoingSeconds, double IncomingSeconds, double UnitYawRadians, double UnitChargeSeconds)> RepeatedPivotUnits
        => _repeatFamilies.SelectMany(
            family => family.Units.Select(unit => (family.Role, unit.Restart.A, unit.Restart.B, unit.UnitYaw, unit.UnitSeconds)));

    /// <summary>The tunables in force.</summary>
    public ArrivalPlannerConfig Config
    {
        get;
    }

    /// <summary>Role of the Idle clip.</summary>
    public string IdleRole => _idle.Role;

    /// <summary>
    /// The exact per-tick prediction of a plan (for diagnostics and recording; planning itself verifies in summary
    /// mode). Not thread-safe, like the predictor.
    /// </summary>
    /// <exception cref="ArgumentException">The plan failed.</exception>
    public SequencePrediction Predict(ArrivalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.SequenceStart is { } start
            ? _predictor.Predict(start, [.. plan.SequenceSegments], _dt)
            : throw new ArgumentException("A failed plan has no sequence.", nameof(plan));
    }

    /// <summary>Plans from a start state to a destination and final facing.</summary>
    /// <param name="start">The playing clip and the actor pose.</param>
    /// <param name="target">Destination and final facing.</param>
    public ArrivalPlan Plan(ArrivalStart start, ArrivalTarget target)
        => Plan(start, target, travellingOnly: false);

    /// <summary>
    /// Plans from a start state to a destination and final facing. With <paramref name="travellingOnly"/>, a
    /// travelling start must continue travelling to the target when a travelling composition can complete it: such a
    /// continuation is admissible only if its materialised sequence finishes within the arrival tolerances
    /// (<see cref="ArrivalPlannerConfig.ArrivedDistanceMetres"/> and <see cref="ArrivalPlannerConfig.ArrivedYawRadians"/>)
    /// under the legal capped swing correction, so a route's terminal approach never stops and restarts mid-route
    /// while a closable continuation exists, and a continuation that cannot complete no longer suppresses the
    /// complete final-approach families (they are searched instead).
    /// </summary>
    /// <param name="start">The playing clip and the actor pose.</param>
    /// <param name="target">Destination and final facing.</param>
    /// <param name="travellingOnly">Whether a travelling start must keep travelling when it can complete the target.</param>
    public ArrivalPlan Plan(ArrivalStart start, ArrivalTarget target, bool travellingOnly)
        => Plan(start, target, travellingOnly, continuingPhase: null);

    /// <summary>
    /// Plans from a start state to a destination and final facing with the live physical support phase's remaining
    /// correction allowance (CORNER-03): terminal continuation admission assesses candidates against the same
    /// ongoing-phase budget the runtime will spend, so a partly spent phase is not replenished by the plan change
    /// itself. The carried budget is an immutable value; assessment consumes its own copy.
    /// </summary>
    /// <param name="start">The playing clip and the actor pose.</param>
    /// <param name="target">Destination and final facing.</param>
    /// <param name="travellingOnly">Whether a travelling start must keep travelling when it can complete the target.</param>
    /// <param name="continuingPhase">
    /// The committed allowance of the physical support phase ongoing at the start tick, or null on a cold start.
    /// </param>
    /// <param name="composedPathAdmission">
    /// Optional composed-trajectory containment admission (CORNER-12): when set, each finalist's exact per-tick
    /// prediction is replayed with the runtime's capped correction and checked against the navigable surface —
    /// an unadmitted finalist is rejected so a contained one wins. The terminal construction is unchanged.
    /// </param>
    internal ArrivalPlan Plan(ArrivalStart start, ArrivalTarget target, bool travellingOnly, SwingCorrectionPhaseBudget? continuingPhase, SectionComposedPathAdmission? composedPathAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        long began = Stopwatch.GetTimestamp();
        var displacement = PlanarMotion.Between(start.Pose, new SequencePose(target.Position, target.Yaw));
        // Idle starts search one fixed movement family. Mid-clip starts can search several prefix-dependent families
        // and a ContinueTravel candidate in the same Search, so they must keep the legacy whole-search ordering.
        bool stationaryOnly = start.Role == _idle.Role
            && ArrivalMovementChoice.Choose(displacement.X, displacement.Y, displacement.Yaw, Config) == ArrivalMovementKind.TurnOnly;
        var search = new Search(Config, displacement, stationaryOnly);
        if (!_seams.Clips.TryGetValue(start.Role, out SeamClip? clip)
            || !_seams.IsInPlannerSet(start.Role)
            || (start.Fading is { } fading && !_seams.Clips.ContainsKey(fading.FromRole))
            || !double.IsFinite(start.PositionSeconds))
        {
            return Fail(start, target, ArrivalPlanFailure.InvalidStart, $"'{start.Role}' is not a plannable clip of this binding.", search, began);
        }

        double earliest = start.PositionSeconds + (RemainingCrossfadeTicks(start.Fading) * _dt);
        if (clip.Kind == SeamClipRole.Idle)
        {
            search.Origin(-1, PlanarMotion.Identity);
            SearchFromIdle(search);
        }
        else
        {
            List<StartPrefix> prefixes = StartPrefixes(clip, start.PositionSeconds, earliest, start.Prompt, search, out bool travelling);
            search.Prefixes = prefixes;
            if (!travellingOnly)
            {
                foreach ((StartPrefix prefix, int index) in prefixes
                             .Select((prefix, index) => (prefix, index))
                             .Where(pair => !pair.prefix.Continues)
                             .OrderBy(pair => pair.prefix.Duration)
                             .Take(Math.Max(1, Config.StopPrefixOptions)))
                {
                    search.Origin(index, prefix.Motion);
                    SearchFromIdle(search);
                }
            }

            // Travelling on: the prefix itself reaches the target, then the final pivot.
            for (int index = 0; travelling && index < prefixes.Count; index++)
            {
                search.Origin(index, PlanarMotion.Identity);
                search.Kind = ArrivalMovementKind.ContinueTravel;
                Consider(search, prefixes[index].Motion, new Recipe(MainKind.None), 0, 0f, 0d, travel: true, alignLimit: null);
            }

            if (travellingOnly && search.Count > 0)
            {
                // Terminal continuation admissibility (CORNER-08): a continuation's presence is not proof it can
                // complete. Keep only the travelling finalists whose materialised sequence actually finishes within
                // the arrival tolerances under the legal capped swing correction; inadmissible ones must not
                // suppress the complete final-approach fallback below.
                RetainClosableContinuations(start, target, earliest, search, continuingPhase);

                // Containment is part of admissibility (CORNER-12): a travelling finalist whose composed path
                // leaves the navigable surface is equally inadmissible, and must equally not suppress the
                // complete final-approach fallback — the west-route recovery otherwise failed its terminal plan
                // with only uncontained straight-walk continuations available while a contained stop-prefix
                // composition existed.
                if (composedPathAdmission is { } admission)
                {
                    search.KeepOnly(candidate => TryMaterialise(start, earliest, search, candidate.Recipe, out SequenceStart sequenceStart, out ImmutableArray<ArrivalPlanSegment> segments)
                        && AdmitsComposedPath(sequenceStart, segments, admission));
                }
            }

            if (travellingOnly && search.Count == 0)
            {
                // No travelling composition reaches the target, or none that reached it can complete: the stop
                // prefixes remain the honest fallback (the complete final-approach families).
                foreach ((StartPrefix prefix, int index) in prefixes
                             .Select((prefix, index) => (prefix, index))
                             .Where(pair => !pair.prefix.Continues)
                             .OrderBy(pair => pair.prefix.Duration)
                             .Take(Math.Max(1, Config.StopPrefixOptions)))
                {
                    search.Origin(index, prefix.Motion);
                    SearchFromIdle(search);
                }
            }
        }

        return Finish(start, target, earliest, search, began, composedPathAdmission);
    }

    /// <summary>
    /// Admission check for one section finalist's composed trajectory: the finalist's exact per-tick prediction
    /// (native playback, support windows and planted balls) handed to the caller, which replays the runtime's own
    /// capped correction and checks the realised samples against the actual navigable surface. Returning false
    /// rejects the finalist like an exact-end miss, so a lower-ranked admissible composition can win. The planner
    /// itself stays surface- and correction-agnostic: it only supplies the exact prediction the check needs
    /// (CORNER-07: endpoint-only or native-only checks cannot see a composed segment that leaves the surface).
    /// </summary>
    /// <param name="prediction">The finalist's exact per-tick prediction (valid; windows and balls included).</param>
    /// <param name="segments">The finalist's materialised segments, for diagnostics.</param>
    public delegate bool SectionComposedPathAdmission(SequencePrediction prediction, ImmutableArray<ArrivalPlanSegment> segments);

    /// <summary>
    /// Plans one nonterminal route section (NAV-001 User Requirement 8 route follow-through): travel from the
    /// playing clip toward an intermediate route station through native walk/arc seams, ending mid-travel. A
    /// section never contains an exit clip or an Idle tail, and its last segment is a looping travel
    /// clip whose run ends on the tick grid (no seam is needed where no transition happens); the actor keeps
    /// moving when it ends, and the caller replans from the realised state.
    /// </summary>
    /// <param name="start">The playing clip and the actor pose (Idle, an entry, or a travelling cycle).</param>
    /// <param name="station">A point on the route with its heading there: a tracking reference, not a stop.</param>
    /// <param name="promptRedirect">
    /// With TR14 prompt semantics (a supersession redirect): the first transition out of a travelling clip is
    /// the first aligned seam into a travelling successor, never delayed by cost. Ignored for an Idle start.
    /// </param>
    /// <param name="composedPathAdmission">
    /// Optional composed-trajectory admission for each finalist (CORNER-07); null keeps the unfiltered behaviour.
    /// </param>
    /// <remarks>
    /// Candidate families, all bounded: continuing the current travel clip (a pure run, no seam); a lane switch
    /// into an arc towards the station's side (walk → arc for a redirect; arc → walk to straighten; entry → walk
    /// or entry → arc), then the arc's own switch back into the walk, then a straight walk run whose length is
    /// solved closed-form on the tick grid; or ending mid-arc on a coarse tick scan; or, from an Idle start
    /// (TR22), the straight Idle → entry → walk run and — for stations no straight run reaches, first and
    /// foremost behind or wide first legs of a route — a pivot-led composition: one confined pivot block
    /// (nearest to the station's bearing, loop crossings as needed) that hands off through the planner-set
    /// pivot → Idle → entry pairs into the same entry → walk prefix along the new heading. Candidates whose
    /// endpoint misses the station by more than the configured <see cref="ArrivalPlannerConfig.DistanceWindowMetres"/> (the
    /// planner's travel-miss window) are excluded, so a station no travelling composition reaches fails the section
    /// instead of producing a wayward plan. Survivors are ranked by endpoint residual to the station (the same cost
    /// model as <see cref="Plan(ArrivalStart, ArrivalTarget)"/>), and finalists are predicted exactly in summary mode with support windows, like
    /// <see cref="Finish"/>. Sections from Idle enter through an entry clip only (Idle → arc pairs are not in the
    /// planner set).
    /// </remarks>
    public ArrivalPlan PlanSection(
        ArrivalStart start,
        ArrivalTarget station,
        bool promptRedirect,
        SectionComposedPathAdmission? composedPathAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        long began = Stopwatch.GetTimestamp();
        var displacement = PlanarMotion.Between(start.Pose, new SequencePose(station.Position, station.Yaw));
        if (!_seams.Clips.TryGetValue(start.Role, out SeamClip? clip)
            || !_seams.IsInPlannerSet(start.Role)
            || (start.Fading is { } fading && !_seams.Clips.ContainsKey(fading.FromRole))
            || !double.IsFinite(start.PositionSeconds))
        {
            return FailSection(start, station, ArrivalPlanFailure.InvalidStart, $"'{start.Role}' is not a plannable clip of this binding.", began, 0, 0);
        }

        double earliest = start.PositionSeconds + (RemainingCrossfadeTicks(start.Fading) * _dt);
        int compositions = 0;
        List<SectionCandidate> candidates = [];
        if (clip.Kind == SeamClipRole.Idle)
        {
            if (_walk is null)
            {
                return FailSection(start, station, ArrivalPlanFailure.NoAlignedPlan, "No forward walk cycle is plannable.", began, 0, 0);
            }

            // Idle → entry → walk run; the redirect, if the route needs one, belongs to a later section.
            foreach (WalkPrefix prefix in _walkPrefixes)
            {
                foreach ((PlanarMotion motion, double seconds) in WalkRuns(prefix.Motion, _walk, prefix.Next.Settled, displacement))
                {
                    // The walk segment plays from its entry seek (prefix.Next.B): the crossfade window to Settled is
                    // part of the segment's playback, so the run is materialised entry-relative (see RunExit).
                    RunExit(_walk, prefix.Next.B, prefix.Next.Settle + seconds, out double exit, out int cycles);
                    candidates.Add(new SectionCandidate(
                        prefix.FromIdle.A,
                        0,
                        [(prefix.FromIdle, prefix.Next.A, 0), (prefix.Next, exit, cycles)],
                        motion,
                        2 + cycles,
                        Math.Max(prefix.Separation, prefix.Next.Separation),
                        prefix.Duration + seconds,
                        FromIdle: true));
                    compositions++;
                }
            }

            // TR22 from-idle coverage: a route station is purely route-geometric (one section horizon along the
            // navmesh path), so the FIRST leg of a route can bear in any direction from a standing actor, and a
            // straight forward walk run cannot reach a behind or wide station. The actor aligns first through one
            // confined pivot block (pl45/pr45 with loop crossings — the same idle-bounded blocks the terminal
            // TurnFirst family uses, blocks nearest to the station's bearing with unbounded total yaw), hands off
            // through the planner-set pivot→Idle→entry pairs within the idle dwell bound, and then walks the
            // EXISTING entry→walk prefix along the new heading. The section still ends mid-walk (no exit or Idle
            // tail); the straight-only compositions above remain the natural winner wherever they reach, because
            // they carry fewer steps. A local WINDOW of nearest blocks is enumerated rather than a single nearest
            // pair: each block bakes its own pivot→Idle exit join, and a block whose exit settles where no
            // entry slot is reachable within the dwell bound contributes nothing, so the window lets the endpoint
            // window and the existing section ranking choose a viable block instead of failing on the nearest one.
            double bearing = ArrivalMovementChoice.BearingOf(displacement.X, displacement.Y);
            foreach (Manoeuvre pivot in PivotBlockWindow(bearing))
            {
                compositions += AddPivotLedSectionCandidates(
                    displacement, candidates,
                    pivot.In, [(pivot.In, pivot.Out.A, pivot.Cycles)], pivot.Out, pivot.Motion, pivot.Steps, pivot.Separation, pivot.Duration);
            }

            // The single-block families leave yaw gaps (each bakes one entry/exit solo pair, so the loop counts
            // step by the pivot's whole-cycle yaw); the pivot CONTINUATIONS — the same aligned self-seam machinery
            // the terminal's stationary search uses — combine two solo portions and land between them. The nearest
            // few by bearing are enumerated so a station in a single-block gap still has a precisely aligned block.
            foreach (PivotContinuation continuation in PivotContinuationWindow(bearing))
            {
                compositions += AddPivotLedSectionCandidates(
                    displacement, candidates,
                    continuation.Entry,
                    [
                        (continuation.Entry, continuation.Restart.A, continuation.BeforeCycles),
                        (continuation.Restart, continuation.Exit.A, continuation.AfterCycles),
                    ],
                    continuation.Exit, continuation.Motion, continuation.Steps, continuation.Separation, continuation.Duration);
            }
        }
        else
        {
            if (clip.Kind is not (SeamClipRole.Cycle or SeamClipRole.Entry))
            {
                return FailSection(start, station, ArrivalPlanFailure.InvalidStart, $"'{start.Role}' is not a travelling clip.", began, 0, 0);
            }

            double from = clip.WrapsPlayback ? clip.Normalise(earliest) : earliest;

            // Continuing the current travel clip: a pure run with no seam at all (a cycle only).
            if (clip.Kind == SeamClipRole.Cycle)
            {
                if (start.Role == _walk)
                {
                    foreach ((PlanarMotion motion, double seconds) in WalkRuns(PlanarMotion.Identity, start.Role, start.PositionSeconds, displacement))
                    {
                        RunExit(start.Role, start.PositionSeconds, seconds, out double exit, out int cycles);
                        candidates.Add(new SectionCandidate(exit, cycles, [], motion, cycles, 0f, seconds, FromIdle: false));
                        compositions++;
                    }
                }
                else
                {
                    foreach ((PlanarMotion motion, double seconds, int steps) in ArcRuns(PlanarMotion.Identity, start.Role, start.PositionSeconds, displacement))
                    {
                        RunExit(start.Role, start.PositionSeconds, seconds, out double exit, out int cycles);
                        candidates.Add(new SectionCandidate(exit, cycles, [], motion, steps, 0f, seconds, FromIdle: false));
                        compositions++;
                    }
                }
            }

            foreach (Join first in SectionFirsts(clip, from, promptRedirect, displacement))
            {
                double delay = clip.WrapsPlayback ? LoopBase(start.Role, from, first.A) : first.A - from;
                double elapsed = earliest - start.PositionSeconds + delay;
                PlanarMotion lead = Solo(start.Role, start.PositionSeconds, elapsed);
                int exitCycles = CyclesOf(start.Role, start.PositionSeconds, first.A, elapsed);
                PlanarMotion head = lead.Then(first.Motion);
                if (first.In == _walk)
                {
                    foreach ((PlanarMotion motion, double seconds) in WalkRuns(head, _walk!, first.Settled, displacement))
                    {
                        // The walk segment plays from its entry seek first.B (see RunExit): the crossfade window to
                        // Settled is part of the playback, so the run is materialised entry-relative.
                        RunExit(_walk!, first.B, first.Settle + seconds, out double exit, out int cycles);
                        candidates.Add(new SectionCandidate(
                            first.A,
                            exitCycles,
                            [(first, exit, cycles)],
                            motion,
                            1 + cycles + exitCycles,
                            first.Separation,
                            elapsed + first.Settle + seconds,
                            FromIdle: false));
                        compositions++;
                    }

                    continue;
                }

                // Into an arc: turn, then either straighten into the walk for a closing run, or end mid-arc.
                foreach (Leave lane in _switches.TryGetValue(first.In, out Leave[]? lanes) ? lanes : [])
                {
                    double solo = LoopBase(first.In, first.Settled, lane.Join.A);
                    for (int cycles = 0; cycles < lane.CyclesThen.Length; cycles++)
                    {
                        PlanarMotion turned = head.Then(Solo(first.In, first.Settled, solo)).Then(lane.CyclesThen[cycles]);
                        double intoWalk = elapsed + first.Settle + solo + lane.Join.Settle + (cycles * Length(first.In));
                        foreach ((PlanarMotion motion, double seconds) in WalkRuns(turned, _walk!, lane.Join.Settled, displacement))
                        {
                            // The walk segment plays from its entry seek lane.Join.B (see RunExit): the crossfade
                            // window to Settled is part of the playback, so the run is materialised entry-relative.
                            RunExit(_walk!, lane.Join.B, lane.Join.Settle + seconds, out double exit, out int runCycles);
                            int arcCycles = CyclesOf(first.In, first.B, lane.Join.A, intoWalk - elapsed - lane.Join.Settle);
                            candidates.Add(new SectionCandidate(
                                first.A,
                                exitCycles,
                                [(first, lane.Join.A, arcCycles), (lane.Join, exit, runCycles)],
                                motion,
                                2 + arcCycles + runCycles + exitCycles,
                                Math.Max(first.Separation, lane.Separation),
                                intoWalk + seconds,
                                FromIdle: false));
                            compositions++;
                        }
                    }
                }

                foreach ((PlanarMotion motion, double seconds, int steps) in ArcRuns(head, first.In, first.Settled, displacement))
                {
                    // The arc segment plays from its entry seek first.B (see RunExit): entry-relative materialisation.
                    RunExit(first.In, first.B, first.Settle + seconds, out double exit, out int cycles);
                    candidates.Add(new SectionCandidate(
                        first.A,
                        exitCycles,
                        [(first, exit, cycles)],
                        motion,
                        steps + exitCycles,
                        first.Separation,
                        elapsed + first.Settle + seconds,
                        FromIdle: false));
                    compositions++;
                }
            }

            // TR22 sharp-bearing coverage (CORNER-stuck): the terminal machinery already owns sharp-bearing
            // final approaches (TurnFirst from a travelling state), and the measured west-quadrant stuck loop
            // was caused by degenerate zero-tick section adoptions (now rejected in TryBeginSection), not by a
            // composition gap: with that guard alone the route recovers and the terminal plan composes the turn
            // (ADOPT trace: no travelling pivot-led section is ever needed). A gated travelling pivot-led family
            // was measured to slow the previously completing from-idle route past its unchanged 20 s watchdog,
            // and an ungated one derails the mid-walk corner route entirely, so the bounded enumeration below
            // stays env-gated diagnostics — deliberately inert in production until an evidenced need exists.
            if (System.Environment.GetEnvironmentVariable("ALLEYCAT_TRAVELLING_PIVOT_SECTIONS") == "1")
            {
                float existingWindow = Math.Max(0f, Config.DistanceWindowMetres);
                bool existingFamilyReaches = candidates.Any(candidate =>
                {
                    double dx = displacement.X - candidate.Motion.X;
                    double dy = displacement.Y - candidate.Motion.Y;
                    return Math.Sqrt((dx * dx) + (dy * dy)) <= existingWindow;
                });
                if (!existingFamilyReaches)
                {
                    compositions += AddTravellingPivotLedSectionCandidates(start, earliest, clip, from, displacement, candidates);
                }
            }
        }

        if (candidates.Count == 0)
        {
            return FailSection(start, station, ArrivalPlanFailure.NoAlignedPlan, "No travelling composition reaches towards the station.", began, compositions, 0);
        }

        double StationCost(PlanarMotion reached)
        {
            double dx = displacement.X - reached.X;
            double dy = displacement.Y - reached.Y;
            return Math.Sqrt((dx * dx) + (dy * dy))
                + (Config.YawWeightMetresPerRadian * Math.Abs(Mathf.Wrap(displacement.Yaw - reached.Yaw, -Math.PI, Math.PI)));
        }

        double window = Math.Max(0d, Config.DistanceWindowMetres);
        bool WithinWindow(PlanarMotion reached)
        {
            double dx = displacement.X - reached.X;
            double dy = displacement.Y - reached.Y;
            return Math.Sqrt((dx * dx) + (dy * dy)) <= window;
        }

        double quantum = Math.Max(1e-9d, Config.ResidualTieMetres);
        List<SectionCandidate> finalists = [.. candidates
            .Where(candidate => WithinWindow(candidate.Motion))
            .OrderBy(candidate => Math.Floor(StationCost(candidate.Motion) / quantum))
            .ThenBy(candidate => candidate.Steps)
            .ThenBy(candidate => candidate.Separation)
            .ThenBy(candidate => candidate.Duration)
            .Take(Math.Max(1, Config.MaxFinalists))];
        if (finalists.Count == 0)
        {
            return FailSection(start, station, ArrivalPlanFailure.NoAlignedPlan, $"No travelling composition ends within {window:0.###} m of the station.", began, compositions, 0);
        }

        (SequenceStart Start, ImmutableArray<ArrivalPlanSegment> Segments, SectionCandidate Candidate, SequenceSummary Summary, double Cost)? best = null;
        List<string> rejections = [];
        foreach (SectionCandidate finalist in finalists)
        {
            if (best is { } found && Math.Floor((StationCost(finalist.Motion) - EstimateMarginMetres) / quantum) > Math.Floor(found.Cost / quantum))
            {
                break;
            }

            if (!TryBuildSection(start, earliest, finalist, out SequenceStart sequenceStart, out ImmutableArray<ArrivalPlanSegment> segments))
            {
                rejections.Add("section did not materialise");
                continue;
            }

            SequenceSummary summary = _predictor.PredictSummary(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt, collectWindows: true);
            if (!summary.IsValid)
            {
                rejections.Add(summary.Rejection?.ToString() ?? "rejected");
                continue;
            }

            // The declared endpoint window binds the EXACT predicted result, not only the approximate candidate:
            // a finalist whose exact end leaves the window is rejected, never adopted (CORNER-02). This holds with
            // in-flight fades and across the loop boundary, where model and exact endpoints can diverge.
            float exactMiss = summary.EndPose.Position.DistanceTo(station.Position);
            if (exactMiss > window)
            {
                rejections.Add(FormattableString.Invariant($"exact end {summary.EndPose.Position.X:0.####},{summary.EndPose.Position.Y:0.####} misses the station by {exactMiss:0.####} m > {window:0.####} m"));
                continue;
            }

            // The composed-trajectory admission (CORNER-07) sees the finalist's exact per-tick prediction: the
            // caller replays the runtime's own capped correction over it and checks the realised samples against
            // the actual navigable surface, so a composition whose composed path leaves the surface mid-segment is
            // rejected here even when both endpoints are inside the window.
            if (composedPathAdmission is { } admission)
            {
                SequencePrediction composed = _predictor.Predict(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt);
                if (!composed.IsValid || !admission(composed, segments))
                {
                    rejections.Add("the composed path was not admitted");
                    continue;
                }
            }

            double cost = StationCost(PlanarMotion.Between(start.Pose, summary.EndPose));
            if (best is null || cost < best.Value.Cost)
            {
                best = (sequenceStart, segments, finalist, summary, cost);
            }
        }

        if (best is not { } winner)
        {
            return FailSection(start, station, ArrivalPlanFailure.NoAlignedPlan, "Every section finalist was rejected: " + string.Join("; ", rejections), began, compositions, finalists.Count);
        }

        var statistics = new ArrivalPlanStatistics(compositions, finalists.Count, false, Math.Abs(StationCost(winner.Candidate.Motion) - winner.Cost), 0d, Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        return new ArrivalPlan(start, station, ArrivalMovementKind.TravelSection, winner.Start, winner.Segments, winner.Summary, statistics);
    }

    private static ArrivalPlan FailSection(
        ArrivalStart start,
        ArrivalTarget station,
        ArrivalPlanFailure failure,
        string detail,
        long began,
        int compositions,
        int summaryPredictions)
        => new(start, station, failure, detail, new ArrivalPlanStatistics(compositions, summaryPredictions, false, 0d, 0d, Stopwatch.GetElapsedTime(began).TotalMilliseconds));

    /// <summary>Travelling seams out of the start clip for a section: the turn-side arcs from the walk, the walk from an
    /// arc, and the walk or turn-side arcs from an entry. Prompt keeps only the first aligned seam time.</summary>
    private List<Join> SectionFirsts(SeamClip clip, double from, bool prompt, PlanarMotion displacement)
    {
        string role = clip.Role;
        List<string> successors = [];
        if (role == _walk)
        {
            successors.AddRange(TurnSideArcs(displacement));
        }
        else if (clip.Kind == SeamClipRole.Cycle)
        {
            if (_walk is not null)
            {
                successors.Add(_walk);
            }
        }
        else
        {
            if (_walk is not null)
            {
                successors.Add(_walk);
            }

            successors.AddRange(TurnSideArcs(displacement));
        }

        List<Join> firsts = [];
        if (prompt)
        {
            if (successors.Count > 0 && _seams.TryFindNextAligned(role, [.. successors], from, _tolerance, out SeamChoice choice))
            {
                foreach (string successor in successors)
                {
                    if (_seams.TryFindNextAligned(role, successor, from, _tolerance, out SeamChoice next)
                        && Math.Abs(next.DelaySeconds - choice.DelaySeconds) <= TimeTolerance)
                    {
                        firsts.Add(MakeJoin(next.Pair, next.Candidate));
                    }
                }
            }
        }
        else
        {
            foreach (string successor in successors)
            {
                // The soonest seams first (delay from the current position), so an early-cycle seam time cannot
                // force a late redirect; SectionJoinOptions bounds how many are kept.
                firsts.AddRange(MenuFrom(role, successor, from, clip.WrapsPlayback)
                    .OrderBy(join => clip.WrapsPlayback ? LoopBase(role, from, join.A) : Math.Max(0d, join.A - from))
                    .ThenBy(join => join.Separation)
                    .Take(SectionJoinOptions));
            }
        }

        return firsts;
    }

    /// <summary>
    /// Straight-run lengths (whole ticks) whose walk travel best reaches the station: the closed-form
    /// along-heading solve and its tick neighbours, each evaluated exactly through the trajectory table.
    /// </summary>
    private IEnumerable<(PlanarMotion Motion, double Seconds)> WalkRuns(PlanarMotion head, string role, double from, PlanarMotion displacement)
    {
        (double sin, double cos) = Math.SinCos(head.Yaw);
        double along = (-sin * (displacement.X - head.X)) + (cos * (displacement.Y - head.Y));
        double cap = MaxRunSeconds(role);
        double solved = Math.Clamp(along, 0d, cap);
        int centre = (int)Math.Round(solved / _dt);
        for (int tick = Math.Max(0, centre - 1); tick <= centre + 1; tick++)
        {
            double seconds = tick * _dt;
            if (seconds > cap + TimeTolerance)
            {
                continue;
            }

            yield return (head.Then(Solo(role, from, seconds)), seconds);
        }
    }

    /// <summary>Arc-run lengths on a coarse tick scan refined locally, for sections that end still turning.</summary>
    private IEnumerable<(PlanarMotion Motion, double Seconds, int Steps)> ArcRuns(PlanarMotion head, string role, double from, PlanarMotion displacement)
    {
        double cap = MaxRunSeconds(role);
        int maxTicks = (int)Math.Floor(cap / _dt);
        (PlanarMotion Motion, double Seconds)? best = null;
        double bestCost = double.PositiveInfinity;
        double Cost(PlanarMotion motion)
        {
            double dx = displacement.X - motion.X;
            double dy = displacement.Y - motion.Y;
            return Math.Sqrt((dx * dx) + (dy * dy));
        }
        for (int tick = 0; tick <= maxTicks; tick += ArcScanStepTicks)
        {
            double seconds = tick * _dt;
            double cost = Cost(head.Then(Solo(role, from, seconds)));
            if (cost < bestCost)
            {
                bestCost = cost;
                best = (head.Then(Solo(role, from, seconds)), seconds);
            }
        }

        if (best is null)
        {
            yield break;
        }

        int centre = (int)Math.Round(best.Value.Seconds / _dt);
        for (int tick = Math.Max(0, centre - ArcScanStepTicks); tick <= Math.Min(maxTicks, centre + ArcScanStepTicks); tick++)
        {
            double seconds = tick * _dt;
            yield return (head.Then(Solo(role, from, seconds)), seconds, 1 + ((int)Math.Floor(seconds / Length(role))));
        }
    }

    private double MaxRunSeconds(string role)
        => role == _walk ? Config.MaxWalkCycles * Length(role) : (Config.MaxArcCycles + 1d) * Length(role);

    /// <summary>
    /// The run exit time and extra whole cycles that make a looping clip play <paramref name="seconds"/> of playback
    /// MEASURED FROM <paramref name="from"/>: the predictor's segment playback is
    /// <c>posmod(exit − from, length) + cycles · length</c>, so <paramref name="from"/> must be the segment's ENTRY
    /// seek time (the join's <c>b</c>), never its post-fade settled position — the crossfade window from the entry to
    /// settled is part of the segment's playback, and measuring it from settled instead drops exactly one whole
    /// cycle whenever <c>settle + run</c> crosses the clip's loop length.
    /// </summary>
    private void RunExit(string role, double from, double seconds, out double exit, out int cycles)
    {
        SeamClip clip = _seams.Clips[role];
        double length = clip.DurationSeconds;
        double total = Math.Clamp(seconds, 0d, double.PositiveInfinity);
        cycles = (int)Math.Floor(total / length);
        exit = clip.Normalise(from + Math.Max(0d, total - (cycles * length)));
        cycles = CyclesOf(role, from, exit, total);
    }

    private bool TryBuildSection(
        ArrivalStart start,
        double earliest,
        SectionCandidate candidate,
        out SequenceStart sequenceStart,
        out ImmutableArray<ArrivalPlanSegment> segments)
    {
        sequenceStart = default!;
        segments = [];
        if (candidate.FromIdle)
        {
            if (candidate.Segments.Length < 2)
            {
                return false;
            }

            var builder = new PlanBuilder(this, start.PositionSeconds, earliest - start.PositionSeconds) { IdleFrom = earliest };
            if (!builder.OpenFromIdle(candidate.Segments[0].Join, ArrivalStage.Travel))
            {
                return false;
            }

            foreach ((Join join, double exit, int cycles) in candidate.Segments)
            {
                _ = builder.Add(join, exit, cycles, ArrivalStage.Travel);
            }

            (double idleExit, int idleCycles) = builder.StartIdleExit ?? (start.PositionSeconds, 0);
            sequenceStart = SequenceStart.Playing(start.Pose, start.Role, start.PositionSeconds, idleExit, idleCycles, start.Fading);
            segments = [.. builder.Segments];
            return true;
        }

        List<ArrivalPlanSegment> built = [];
        foreach ((Join join, double exit, int cycles) in candidate.Segments)
        {
            built.Add(new ArrivalPlanSegment(join.In, join.Pair.Incoming.Kind, ArrivalStage.Travel, join.B, join.Crossfade, exit, cycles, join.Separation));
        }

        sequenceStart = SequenceStart.Playing(start.Pose, start.Role, start.PositionSeconds, candidate.StartExit, candidate.StartCycles, start.Fading);
        segments = [.. built];
        return true;
    }

    /// <summary>Bound on seams kept per travelling pair when enumerating a section (an enumeration bound, not a tolerance).</summary>
    private const int SectionJoinOptions = 4;

    /// <summary>
    /// Half-width of the pivot-block window a from-idle section enumerates around the station's bearing (an
    /// enumeration bound, not a tolerance): several nearest blocks, because each bakes its own pivot→Idle exit
    /// join and a block whose exit settles where no entry slot is reachable contributes nothing.
    /// </summary>
    private const int IdleSectionPivotWindow = 4;

    /// <summary>Reachable idle→entry hand-off joins kept per entry clip in a from-idle section, beyond
    /// <see cref="ArrivalPlannerConfig.WalkEntryOptions"/> per seek position (an enumeration bound).</summary>
    private const int IdleSectionEntryOptions = 3;

    private const int ArcScanStepTicks = 5;

    /// <summary>One enumerated section composition, before exact verification.</summary>
    private readonly record struct SectionCandidate(
        double StartExit,
        int StartCycles,
        (Join Join, double Exit, int Cycles)[] Segments,
        PlanarMotion Motion,
        int Steps,
        float Separation,
        double Duration,
        bool FromIdle);

    /// <summary>
    /// Places the finalists on real Idle seams in rank order and predicts each exactly (summary mode), keeping the
    /// best exact result. It stops once no remaining finalist's estimate, less
    /// <see cref="EstimateMarginMetres"/>, could reach the best exact residual bucket (including ties).
    /// </summary>
    private ArrivalPlan Finish(ArrivalStart start, ArrivalTarget target, double earliest, Search search, long began, SectionComposedPathAdmission? composedPathAdmission = null)
    {
        search.SearchMilliseconds = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        if (search.Count == 0)
        {
            return Fail(
                start,
                target,
                search.Exhausted ? ArrivalPlanFailure.BudgetExhausted : ArrivalPlanFailure.NoAlignedPlan,
                search.Exhausted ? "The composition budget ran out before any candidate." : "No candidate sequence of aligned seams reaches Idle.",
                search,
                began);
        }

        (SequenceStart Start, ImmutableArray<ArrivalPlanSegment> Segments, Candidate Candidate, SequenceSummary Summary, double Estimate)? best = null;
        List<string> rejections = [];
        List<Candidate> finalists = search.Best;
        for (int rank = 0; rank < finalists.Count; rank++)
        {
            Candidate candidate = finalists[rank];
            if (best is { } found && search.Bucket(candidate.Cost - EstimateMarginMetres) > search.Bucket(found.Candidate.Cost))
            {
                break;
            }

            if (!TryMaterialise(start, earliest, search, candidate.Recipe, out SequenceStart sequenceStart, out ImmutableArray<ArrivalPlanSegment> segments))
            {
                rejections.Add($"{candidate.Recipe.Main}: no Idle seam within the dwell bound");
                continue;
            }

            SequenceSummary summary = _predictor.PredictSummary(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt, collectWindows: true);
            search.SummaryPredictions++;
            if (!summary.IsValid)
            {
                rejections.Add($"{candidate.Recipe.Main}: {summary.Rejection} at segment {summary.RejectedSegment}");
                continue;
            }

            // The composed-trajectory admission (CORNER-07/CORNER-12) sees the finalist's exact per-tick
            // prediction under the same rule the section finalist loop applies: the caller replays the runtime's
            // own capped correction over it and checks the realised samples against the actual navigable surface,
            // so a terminal or natural-stop composition whose composed path leaves the surface (a stop plan whose
            // walk-exit overshoots a mesh edge) is rejected here and a contained finalist wins instead. The
            // terminal construction itself is unchanged — this gate only selects among the search's own finalists.
            if (composedPathAdmission is { } admission)
            {
                SequencePrediction composed = _predictor.Predict(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt);
                if (!composed.IsValid || !admission(composed, segments))
                {
                    rejections.Add($"{candidate.Recipe.Main}: the composed path was not admitted");
                    continue;
                }
            }

            Candidate exact = candidate with
            {
                Cost = search.Cost(PlanarMotion.Between(start.Pose, summary.EndPose)),
                Duration = summary.TickCount * _dt
            };
            if (best is null || search.IsBetter(exact, best.Value.Candidate))
            {
                best = (sequenceStart, segments, exact, summary, candidate.Cost);
            }
        }

        if (best is not { } winner)
        {
            return Fail(start, target, ArrivalPlanFailure.NoAlignedPlan, "Every finalist was rejected: " + string.Join("; ", rejections), search, began);
        }

        search.EstimateError = Math.Abs(winner.Estimate - winner.Candidate.Cost);
        return new ArrivalPlan(start, target, winner.Candidate.Recipe.Kind, winner.Start, winner.Segments, winner.Summary, search.Statistics(began));
    }

    private static ArrivalPlan Fail(ArrivalStart start, ArrivalTarget target, ArrivalPlanFailure failure, string detail, Search search, long began)
        => new(start, target, failure, detail, search.Statistics(began));

    /// <summary>
    /// Terminal continuation admissibility (CORNER-08): keeps only the travelling finalists whose materialised
    /// sequence, executed with the legal capped swing correction over their own support windows and per-tick
    /// geometry (<see cref="TerminalCorrectionSchedule.Simulate(SequencePrediction, ArrivalTarget, SwingCorrectionAllocator, SwingCorrectionPhaseBudget)"/>, the runtime movement operation's schedule),
    /// finishes within the arrival tolerances. A finalist that cannot complete is dropped from the search — exactly
    /// as <see cref="Finish"/> drops finalists that cannot materialise or predict — so its presence never decides the
    /// request: when no continuation closes, the caller's stop-prefix fallback searches the complete final-approach
    /// families instead. Feasibility semantics are one mechanism everywhere they decide this boundary; the
    /// complete-approach fallback's own ranking stays the honest cost ranking.
    /// </summary>
    private void RetainClosableContinuations(ArrivalStart start, ArrivalTarget target, double earliest, Search search, SwingCorrectionPhaseBudget? continuingPhase)
    {
        // Admissibility is evaluated for the candidate KeepOnly actually supplies (storage order), so each
        // finalist's own materialisation and capped-schedule assessment stays attached to that finalist: Best is a
        // sorted copy, and rank-indexed results would be applied to the wrong recipes whenever the two orders
        // differ (CORNER-08's recorded association defect). The carried phase budget (CORNER-03) is an immutable
        // value consumed as each candidate's own copy, so one candidate's assessment cannot spend another's
        // allowance or the live state.
        search.KeepOnly(candidate => TryMaterialise(start, earliest, search, candidate.Recipe, out SequenceStart sequenceStart, out ImmutableArray<ArrivalPlanSegment> segments)
            && ClosesUnderCappedCorrection(sequenceStart, segments, target, continuingPhase));
    }

    private bool ClosesUnderCappedCorrection(
        SequenceStart sequenceStart,
        ImmutableArray<ArrivalPlanSegment> segments,
        ArrivalTarget target,
        SwingCorrectionPhaseBudget? continuingPhase)
    {
        SequencePrediction prediction = _predictor.Predict(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt);
        return prediction.IsValid
            && TerminalCorrectionSchedule.Simulate(prediction, target, _terminalCorrection, continuingPhase)
                .ClosesWithin(Config.ArrivedDistanceMetres, Config.ArrivedYawRadians);
    }

    /// <summary>CORNER-12: whether a materialised candidate's exact composed trajectory passes the caller's
    /// containment admission. The prediction is the same full per-tick model the section finalist loop admits
    /// against; an invalid prediction is inadmissible by definition.</summary>
    private bool AdmitsComposedPath(SequenceStart sequenceStart, ImmutableArray<ArrivalPlanSegment> segments, SectionComposedPathAdmission admission)
    {
        SequencePrediction prediction = _predictor.Predict(sequenceStart, [.. segments.Select(segment => segment.ToSequenceSegment())], _dt);
        return prediction.IsValid && admission(prediction, segments);
    }

    private void SearchFromIdle(Search search)
    {
        PlanarMotion target = search.LocalTarget;
        ArrivalMovementKind kind = ArrivalMovementChoice.Choose(target.X, target.Y, target.Yaw, Config);
        search.Kind = kind;
        switch (kind)
        {
            case ArrivalMovementKind.None:
                Consider(search, PlanarMotion.Identity, new Recipe(MainKind.None), 0, 0f, 0d, travel: false, alignLimit: null, face: false);
                break;
            case ArrivalMovementKind.TurnOnly:
                Consider(search, PlanarMotion.Identity, new Recipe(MainKind.None), 0, 0f, 0d, travel: false, alignLimit: null);
                SearchStationaryPairs(search);
                SearchPivotContinuations(search);
                break;
            case ArrivalMovementKind.SideStep:
                foreach (int index in Outward(_stepDistances, search))
                {
                    Manoeuvre step = _steps[index];
                    if (Math.Sign(step.Motion.X) == Math.Sign(target.X))
                    {
                        Consider(search, step.Motion, new Recipe(MainKind.Manoeuvre, index), step.Steps, step.Separation, step.Duration, travel: true, alignLimit: null);
                    }
                }

                break;
            case ArrivalMovementKind.Straight:
                SearchWalks(search, Config.StraightMaxAlignPivotRadians);
                // A gentle arc into the walk turns the heading continuously where no alignment pivot fits.
                if (Math.Abs(ArrivalMovementChoice.BearingOf(target.X, target.Y)) >= Config.StraightArcMinBearingRadians)
                {
                    SearchArcs(search, Config.StraightMaxAlignPivotRadians, 0);
                }

                break;
            case ArrivalMovementKind.TurnFirst:
                SearchWalks(search, double.PositiveInfinity);
                break;
            case ArrivalMovementKind.Arc:
                SearchArcs(search, Config.ArcMaxLeadPivotRadians, Math.Sign(ArrivalMovementChoice.BearingOf(target.X, target.Y)));
                break;
            case ArrivalMovementKind.ContinueTravel:
                break;
            case ArrivalMovementKind.TravelSection:
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// Adds two same-direction partial blocks, never a recursive chain or an opposite-direction correction.
    /// Block motions already include both predictor-evaluated fades. Final materialisation resolves the bridge
    /// Idle's actual phase/dwell and summary-predicts the complete sequence before selecting it.
    /// </summary>
    private void SearchStationaryPairs(Search search)
    {
        double yaw = search.LocalTarget.Yaw;
        Span<int> neighbours = stackalloc int[2 * Config.PivotNeighbours];
        for (int first = 0; first < _pivots.Length && !search.Exhausted; first++)
        {
            Manoeuvre a = _pivots[first];
            if (a.Steps != 1 || Math.Sign(a.Motion.Yaw) != Math.Sign(yaw) || Math.Abs(a.Motion.Yaw) >= Math.Abs(yaw))
            {
                continue;
            }

            int count = NearestPivots(yaw - a.Motion.Yaw, neighbours);
            for (int index = 0; index < count; index++)
            {
                int second = neighbours[index];
                Manoeuvre b = _pivots[second];
                if (b.Steps != 1 || Math.Sign(b.Motion.Yaw) != Math.Sign(yaw))
                {
                    continue;
                }

                StartPrefix? prefix = search.PrefixIndex < 0 ? null : search.Prefixes![search.PrefixIndex];
                search.Offer(new Candidate(
                    search.Cost(search.PrefixMotion.Then(a.Motion).Then(b.Motion)),
                    a.Steps + b.Steps + (prefix?.Steps ?? 0),
                    Math.Max(Math.Max(a.Separation, b.Separation), prefix?.Separation ?? 0f),
                    a.Duration + b.Duration + (prefix?.Duration ?? 0d),
                    new Recipe(MainKind.None) { Prefix = search.PrefixIndex, Align = first, Face = second, Kind = search.Kind }));
                if (++search.Compositions >= Config.MaxCompositions)
                {
                    search.Exhausted = true;
                    return;
                }
            }
        }
    }

    private void SearchPivotContinuations(Search search)
    {
        for (int index = 0; index < _pivotContinuations.Length && !search.Exhausted; index++)
        {
            PivotContinuation option = _pivotContinuations[index];
            if (Math.Abs(search.LocalTarget.Yaw) <= (3d * option.CycleYaw) + TimeTolerance
                || Math.Sign(option.Motion.Yaw) != Math.Sign(search.LocalTarget.Yaw))
            {
                continue;
            }

            StartPrefix? prefix = search.PrefixIndex < 0 ? null : search.Prefixes![search.PrefixIndex];
            search.Offer(new Candidate(
                search.Cost(search.PrefixMotion.Then(option.Motion)),
                option.Steps + (prefix?.Steps ?? 0),
                Math.Max(option.Separation, prefix?.Separation ?? 0f),
                option.Duration + (prefix?.Duration ?? 0d),
                new Recipe(MainKind.PivotContinuation, index) { Prefix = search.PrefixIndex, Kind = search.Kind }));
            if (++search.Compositions >= Config.MaxCompositions)
            {
                search.Exhausted = true;
            }
        }

        if (!search.Exhausted)
        {
            SearchRepeatedPivotContinuations(search);
        }
    }

    /// <summary>
    /// The request-dependent repeated-pivot family: homogeneous chains of one COMPLETE restart unit of a looping
    /// pivot's own self seam — the restart fade plus the native solo back to the seam's outgoing time, a full
    /// alternating swing cycle of the clip's metadata — offered only when the request exceeds three native cycles of
    /// the role. Every additional restart and every extra native loop (placed after the last restart) is charged to
    /// the SAME extra-native reserve on both axes: yaw and ticks, each unit's entry seek included, never more than
    /// <see cref="ArrivalPlannerConfig.MaxPivotLoops"/> native loops' worth of either. Chains compose the endpoint
    /// model and enter the normal Offer path with no priority over the bind-time families; every composed chain is
    /// charged to the shared composition budget, and a chain whose yaw residual alone cannot reach the finalists'
    /// threshold is skipped before composition exactly as the travel families pre-filter theirs.
    /// </summary>
    private void SearchRepeatedPivotContinuations(Search search)
    {
        double target = search.LocalTarget.Yaw;
        double prefixYaw = search.PrefixMotion.Yaw;
        double yawWeight = Math.Abs(Config.YawWeightMetresPerRadian);
        foreach (RepeatedPivotFamily family in _repeatFamilies)
        {
            if (search.Exhausted)
            {
                return;
            }

            if (Math.Abs(target) <= (3d * family.CycleYaw) + TimeTolerance)
            {
                continue;
            }

            double length = family.CycleSeconds;
            double reserveYaw = Config.MaxPivotLoops * family.CycleYaw;
            double reserveSeconds = Config.MaxPivotLoops * family.CycleSeconds;
            foreach (RepeatedPivotUnit unit in family.Units)
            {
                if (Math.Sign(unit.UnitYaw) != Math.Sign(target))
                {
                    continue;
                }

                double unitYaw = Math.Abs(unit.UnitYaw);
                for (int extra = 1;
                    (extra * unit.UnitSeconds) <= reserveSeconds + TimeTolerance
                        && (extra * unitYaw) <= reserveYaw + TimeTolerance;
                    extra++)
                {
                    int mostLoops = (int)Math.Floor(Math.Min(
                        (reserveSeconds - (extra * unit.UnitSeconds)) / length,
                        (reserveYaw - (extra * unitYaw)) / family.CycleYaw) + TimeTolerance);
                    for (int loops = 0; loops <= mostLoops; loops++)
                    {
                        OfferRepeatedPivotContinuations(search, family, unit, extra, loops, prefixYaw, yawWeight);
                        if (search.Exhausted)
                        {
                            return;
                        }
                    }
                }
            }
        }
    }

    /// <summary>Composes and offers every entry/exit binding of one repeated-pivot chain shape.</summary>
    private void OfferRepeatedPivotContinuations(
        Search search,
        RepeatedPivotFamily family,
        RepeatedPivotUnit unit,
        int extra,
        int loops,
        double prefixYaw,
        double yawWeight)
    {
        string role = family.Role;
        double length = family.CycleSeconds;
        foreach (Join entry in family.Entries)
        {
            double before = LoopBase(role, entry.Settled, unit.Restart.A);
            PlanarMotion head = entry.Motion.Then(Solo(role, entry.Settled, before));
            foreach (Join exit in family.Exits)
            {
                double after = LoopBase(role, unit.Restart.Settled, exit.A);
                PlanarMotion tail = Solo(role, unit.Restart.Settled, after);
                PlanarMotion loop = Solo(role, exit.A, length);
                double yaw = head.Yaw + (extra * unit.UnitYaw) + unit.Restart.Motion.Yaw + tail.Yaw
                    + (loops * loop.Yaw) + exit.Motion.Yaw;
                double miss = Math.Abs(Mathf.Wrap(search.LocalTarget.Yaw - (prefixYaw + yaw), -Math.PI, Math.PI)) * yawWeight;
                if (miss > search.Threshold)
                {
                    continue;
                }

                PlanarMotion motion = head;
                for (int repeat = 0; repeat < extra; repeat++)
                {
                    motion = motion.Then(unit.Restart.Motion).Then(unit.SoloMotion);
                }

                PlanarMotion end = tail;
                for (int loopIndex = 0; loopIndex < loops; loopIndex++)
                {
                    end = end.Then(loop);
                }

                motion = motion.Then(unit.Restart.Motion).Then(end).Then(exit.Motion);
                double duration = entry.Settle + before
                    + (extra * (unit.Restart.Settle + unit.UnitSeconds - _dt))
                    + unit.Restart.Settle + after + (loops * length) + exit.Settle;
                float separation = Math.Max(Math.Max(entry.Separation, unit.Restart.Separation), exit.Separation);
                int steps = extra + 2 + loops;
                int index = _pivotContinuations.Length + search.AddGeneratedPivotContinuation(new PivotContinuation(
                    entry, unit.Restart, exit, 0, loops, extra + 1, unit.UnitYaw, unit.UnitSeconds, motion,
                    separation, duration, steps, family.CycleYaw));
                StartPrefix? prefix = search.PrefixIndex < 0 ? null : search.Prefixes![search.PrefixIndex];
                search.Offer(new Candidate(
                    search.Cost(search.PrefixMotion.Then(motion)),
                    steps + (prefix?.Steps ?? 0),
                    Math.Max(separation, prefix?.Separation ?? 0f),
                    duration + (prefix?.Duration ?? 0d),
                    new Recipe(MainKind.PivotContinuation, index) { Prefix = search.PrefixIndex, Kind = search.Kind }));
                if (++search.Compositions >= Config.MaxCompositions)
                {
                    search.Exhausted = true;
                    return;
                }
            }
        }
    }

    private void SearchWalks(Search search, double alignLimit)
    {
        foreach (int index in Outward(_sliceDistances, search))
        {
            SliceWalk slice = _sliceWalks[index];
            Consider(search, slice.Motion, new Recipe(MainKind.Slice, index), 2, slice.Separation, slice.Duration, travel: true, alignLimit);
        }

        for (int index = 0; index < _walkPrefixes.Length; index++)
        {
            WalkPrefix prefix = _walkPrefixes[index];
            ConsiderTails(search, prefix.Motion, GridIndex(_walk!, prefix.Next.Settled), new Recipe(MainKind.Walk, index), 1, prefix.Separation, prefix.Duration, alignLimit);
        }
    }

    /// <summary>
    /// Entry → arc towards the target side (→ forward cycle) → exit walks, with alignment pivots up to
    /// <paramref name="alignLimit"/> (on <paramref name="alignSide"/> only, when not zero).
    /// </summary>
    private void SearchArcs(Search search, double alignLimit, int alignSide)
    {
        PlanarMotion target = search.LocalTarget;
        int side = Math.Sign(ArrivalMovementChoice.BearingOf(target.X, target.Y));
        if (_arcStops.TryGetValue(side, out (ArcStop[] Stops, double[] Distances) sided))
        {
            foreach (int index in Outward(sided.Distances, search))
            {
                ArcStop stop = sided.Stops[index];
                Consider(
                    search,
                    stop.Motion,
                    new Recipe(MainKind.ArcStop, stop.Prefix, stop.Stop, Cycles0: stop.Cycles),
                    3 + stop.Cycles,
                    stop.Separation,
                    stop.Duration,
                    travel: true,
                    alignLimit,
                    alignSide);
            }
        }

        for (int index = 0; index < _arcPrefixes.Length && !search.Exhausted; index++)
        {
            ArcWalks walks = _arcWalks[index];
            if (walks.Turn != side)
            {
                continue;
            }

            for (int lane = 0; lane < walks.SwitchMids.Length; lane++)
            {
                PlanarMotion[] mids = walks.SwitchMids[lane];
                for (int cycles = 0; cycles < mids.Length; cycles++)
                {
                    ConsiderTails(
                        search,
                        mids[cycles],
                        walks.SwitchSettled[lane],
                        new Recipe(MainKind.ArcSwitch, index, lane, Cycles0: cycles),
                        3 + cycles,
                        walks.SwitchSeparations[lane],
                        walks.SwitchDurations[lane] + (cycles * walks.Length),
                        alignLimit,
                        alignSide);
                }
            }
        }
    }

    /// <summary>
    /// Every entry → arc → exit → Idle walk (each arc prefix, stop and extra arc cycles), by the arc's turning side and
    /// sorted by distance.
    /// </summary>
    private Dictionary<int, (ArcStop[] Stops, double[] Distances)> BuildArcStops()
    {
        Dictionary<int, List<ArcStop>> bySide = [];
        for (int index = 0; index < _arcPrefixes.Length; index++)
        {
            WalkPrefix prefix = _arcPrefixes[index];
            string arc = prefix.Next.In;
            double settled = prefix.Next.Settled;
            Leave[] stops = _stops[arc];
            int side = _arcWalks[index].Turn;
            if (!bySide.TryGetValue(side, out List<ArcStop>? list))
            {
                bySide[side] = list = [];
            }

            for (int stopIndex = 0; stopIndex < stops.Length; stopIndex++)
            {
                Leave stop = stops[stopIndex];
                double solo = LoopBase(arc, settled, stop.Join.A);
                PlanarMotion head = prefix.Motion.Then(Solo(arc, settled, solo));
                for (int cycles = 0; cycles < stop.CyclesThen.Length; cycles++)
                {
                    list.Add(new ArcStop(
                        index,
                        stopIndex,
                        cycles,
                        head.Then(stop.CyclesThen[cycles]),
                        Math.Max(prefix.Separation, stop.Separation),
                        prefix.Duration + solo + (cycles * Length(arc)) + stop.Duration));
                }
            }
        }

        return bySide.ToDictionary(
            pair => pair.Key,
            pair =>
            {
                ArcStop[] sorted = [.. pair.Value.OrderBy(stop => stop.Motion.Distance)];
                return (sorted, sorted.Select(stop => stop.Motion.Distance).ToArray());
            });
    }

    /// <summary>
    /// Indices of a distance-sorted list, nearest to the target distance first, while the distance miss stays within
    /// <see cref="ArrivalPlannerConfig.DistanceWindowMetres"/> and the finalists' threshold (re-read at each step, so
    /// the scan stops as soon as the finalists improve past it).
    /// </summary>
    private IEnumerable<int> Outward(double[] distances, Search search)
    {
        double distance = search.LocalDistance;
        int found = Array.BinarySearch(distances, distance);
        int above = found >= 0 ? found : ~found;
        int below = above - 1;
        while (!search.Exhausted)
        {
            double bound = Math.Min(Config.DistanceWindowMetres, search.Threshold + _boundSlack);
            double up = above < distances.Length ? distances[above] - distance : double.PositiveInfinity;
            double down = below >= 0 ? distance - distances[below] : double.PositiveInfinity;
            if (Math.Min(up, down) > bound)
            {
                yield break;
            }

            yield return up <= down ? above++ : below--;
        }
    }

    /// <summary>
    /// Bind-time arc walks of one arc prefix up to each lane switch (arc → forward cycle), after the arc's solo
    /// playback with 0 to <see cref="ArrivalPlannerConfig.MaxArcCycles"/> extra arc cycles.
    /// </summary>
    private ArcWalks BuildArcWalks(WalkPrefix prefix)
    {
        string arc = prefix.Next.In;
        double settled = prefix.Next.Settled;
        Leave[] switches = _switches.TryGetValue(arc, out Leave[]? lanes) ? lanes : [];

        var switchMids = new PlanarMotion[switches.Length][];
        float[] switchSeparations = new float[switches.Length];
        double[] switchDurations = new double[switches.Length];
        int[] switchSettled = new int[switches.Length];
        for (int index = 0; index < switches.Length; index++)
        {
            Leave lane = switches[index];
            double solo = LoopBase(arc, settled, lane.Join.A);
            PlanarMotion head = prefix.Motion.Then(Solo(arc, settled, solo));
            switchMids[index] = [.. lane.CyclesThen.Select(head.Then)];
            switchSeparations[index] = Math.Max(prefix.Separation, lane.Separation);
            switchDurations[index] = prefix.Duration + solo + lane.Join.Settle;
            switchSettled[index] = GridIndex(_walk!, lane.Join.Settled);
        }

        return new ArcWalks(
            Math.Sign(Solo(arc, 0d, Length(arc)).Yaw),
            Length(arc),
            switchMids,
            switchSeparations,
            switchDurations,
            switchSettled);
    }

    /// <summary>
    /// Looks up the forward-walk tails from the walk cycle's settled grid position whose forward distance makes the
    /// whole walk as long as the target is far, and considers each.
    /// </summary>
    private void ConsiderTails(Search search, PlanarMotion head, int settledIndex, Recipe recipe, int steps, float separation, double duration, double alignLimit, int alignSide = 0)
    {
        double[] keys = _walkTailKeys[settledIndex];
        if (keys.Length == 0)
        {
            return;
        }

        (double sin, double cos) = Math.SinCos(head.Yaw);
        double along = (-sin * head.X) + (cos * head.Y);
        double discriminant = (along * along) - ((head.X * head.X) + (head.Y * head.Y)) + (search.LocalDistance * search.LocalDistance);
        double needed = -along + (discriminant > 0d ? Math.Sqrt(discriminant) : 0d);
        double bound = Math.Min(Config.DistanceWindowMetres, search.Threshold + _boundSlack);
        if (needed < keys[0] - bound || needed > keys[^1] + bound)
        {
            return;
        }

        // The walk ends about where the head's heading carries it by the needed distance; skip heads whose bearing
        // no allowed alignment pivot can turn onto the target's.
        PlanarMotion target = search.LocalTarget;
        double turn = Mathf.Wrap(
            ArrivalMovementChoice.BearingOf(target.X, target.Y) - ArrivalMovementChoice.BearingOf(head.X - (sin * needed), head.Y + (cos * needed)),
            -Math.PI,
            Math.PI);
        if (Math.Abs(turn) > _alignSlackRadians && (Math.Abs(turn) > alignLimit + _alignSlackRadians || (alignSide != 0 && Math.Sign(turn) != alignSide)))
        {
            return;
        }

        int found = Array.BinarySearch(keys, needed);
        int centre = found >= 0 ? found : ~found;
        Tail[] tails = _walkTails[settledIndex];
        Leave[] stops = _stops[_walk!];
        for (int index = Math.Max(0, centre - Config.TailNeighbours); index < Math.Min(tails.Length, centre + Config.TailNeighbours); index++)
        {
            Tail tail = tails[index];
            PlanarMotion main = head.Then(tail.Motion);
            if (Math.Abs(main.Distance - search.LocalDistance) > bound)
            {
                continue;
            }

            Leave stop = stops[tail.Stop];
            Consider(
                search,
                main,
                recipe with
                {
                    Settled = settledIndex,
                    Tail = index
                },
                steps + 2 + tail.Cycles,
                Math.Max(separation, stop.Separation),
                duration + tail.Duration,
                travel: true,
                alignLimit,
                alignSide);
        }
    }

    /// <summary>
    /// Considers a main motion (from the search origin in Idle) with the alignment pivots it may take and the final
    /// pivots the facing needs, costing each composition with the endpoint model.
    /// </summary>
    private void Consider(
        Search search,
        PlanarMotion main,
        Recipe recipe,
        int steps,
        float separation,
        double duration,
        bool travel,
        double? alignLimit,
        int alignSide = 0,
        bool face = true)
    {
        if (search.Exhausted)
        {
            return;
        }

        PlanarMotion target = search.LocalTarget;
        double distanceMiss = Math.Abs(main.Distance - search.LocalDistance);
        if (travel && (distanceMiss > Config.DistanceWindowMetres || distanceMiss > search.Threshold + _boundSlack))
        {
            return;
        }

        Span<int> aligns = stackalloc int[(2 * Config.PivotNeighbours) + 1];
        int alignCount = 0;
        aligns[alignCount++] = -1;
        if (alignLimit is { } limit && main.Distance > 1e-3d)
        {
            double needed = Mathf.Wrap(
                ArrivalMovementChoice.BearingOf(target.X, target.Y) - ArrivalMovementChoice.BearingOf(main.X, main.Y),
                -Math.PI,
                Math.PI);
            alignCount += NearestPivots(needed, aligns[alignCount..]);
            int kept = 1;
            for (int index = 1; index < alignCount; index++)
            {
                double yaw = _pivots[aligns[index]].Motion.Yaw;
                if (Math.Abs(yaw) <= limit + 1e-6d && (alignSide == 0 || Math.Sign(yaw) == alignSide))
                {
                    aligns[kept++] = aligns[index];
                }
            }

            alignCount = kept;
        }

        Span<int> faces = stackalloc int[(2 * Config.PivotNeighbours) + 1];
        for (int a = 0; a < alignCount; a++)
        {
            int align = aligns[a];
            PlanarMotion before = align < 0 ? main : _pivots[align].Motion.Then(main);
            double planarMiss = Math.Sqrt(((target.X - before.X) * (target.X - before.X)) + ((target.Y - before.Y) * (target.Y - before.Y)));
            if (travel && planarMiss > search.Threshold + _boundSlack)
            {
                continue;
            }

            int faceCount = 0;
            faces[faceCount++] = -1;
            if (face)
            {
                faceCount += NearestPivots(Mathf.Wrap(target.Yaw - before.Yaw, -Math.PI, Math.PI), faces[faceCount..]);
            }

            for (int f = 0; f < faceCount; f++)
            {
                int final = faces[f];
                PlanarMotion total = final < 0 ? before : before.Then(_pivots[final].Motion);
                if (++search.Compositions >= Config.MaxCompositions)
                {
                    search.Exhausted = true;
                }

                Manoeuvre? alignPivot = align < 0 ? null : _pivots[align];
                Manoeuvre? facePivot = final < 0 ? null : _pivots[final];
                StartPrefix? prefix = search.PrefixIndex < 0 ? null : search.Prefixes![search.PrefixIndex];
                search.Offer(new Candidate(
                    search.Cost(search.PrefixMotion.Then(total)),
                    steps + (alignPivot?.Steps ?? 0) + (facePivot?.Steps ?? 0) + (prefix?.Steps ?? 0),
                    Math.Max(Math.Max(separation, prefix?.Separation ?? 0f), Math.Max(alignPivot?.Separation ?? 0f, facePivot?.Separation ?? 0f)),
                    duration + (alignPivot?.Duration ?? 0d) + (facePivot?.Duration ?? 0d) + (prefix?.Duration ?? 0d),
                    recipe with
                    {
                        Prefix = search.PrefixIndex,
                        Align = align,
                        Face = final,
                        Kind = search.Kind
                    }));
                if (search.Exhausted)
                {
                    return;
                }
            }
        }
    }

    private int NearestPivots(double yaw, Span<int> into)
    {
        if (_pivotYaws.Length == 0)
        {
            return 0;
        }

        int found = Array.BinarySearch(_pivotYaws, yaw);
        int centre = found >= 0 ? found : ~found;
        int count = 0;
        for (int index = Math.Max(0, centre - Config.PivotNeighbours); index < Math.Min(_pivotYaws.Length, centre + Config.PivotNeighbours) && count < into.Length; index++)
        {
            into[count++] = index;
        }

        return count;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Materialisation: placing a candidate on real Idle seams and listing its segments.
    // ---------------------------------------------------------------------------------------------------------------

    private bool TryMaterialise(
        ArrivalStart start,
        double earliest,
        Search search,
        Recipe recipe,
        out SequenceStart sequenceStart,
        out ImmutableArray<ArrivalPlanSegment> segments)
    {
        var builder = new PlanBuilder(this, start.PositionSeconds, earliest - start.PositionSeconds);
        double startExit;
        int startCycles;
        StartPrefix? prefix = recipe.Prefix < 0 ? null : search.Prefixes![recipe.Prefix];
        if (prefix is not null)
        {
            startExit = prefix.Exit;
            startCycles = prefix.ExitCycles;
            builder.AddPrefix(prefix);
        }
        else
        {
            startExit = start.PositionSeconds;
            startCycles = 0;
            builder.IdleFrom = earliest;
        }

        bool placed = (recipe.Align < 0 || builder.AddManoeuvre(_pivots[recipe.Align], ArrivalStage.Align))
            && AddMain(search, builder, recipe)
            && (recipe.Face < 0 || builder.AddManoeuvre(_pivots[recipe.Face], ArrivalStage.Face));
        sequenceStart = default!;
        segments = [];
        if (!placed)
        {
            return false;
        }

        if (builder.StartIdleExit is { } exit)
        {
            startExit = exit.Exit;
            startCycles = exit.Cycles;
        }

        builder.CloseIdle();
        sequenceStart = SequenceStart.Playing(start.Pose, start.Role, start.PositionSeconds, startExit, startCycles, start.Fading);
        segments = [.. builder.Segments];
        return true;
    }

    private bool AddMain(Search search, PlanBuilder builder, Recipe recipe)
    {
        switch (recipe.Main)
        {
            case MainKind.None:
                return true;
            case MainKind.Manoeuvre:
                return builder.AddManoeuvre(_steps[recipe.Index0], ArrivalStage.Travel);
            case MainKind.PivotContinuation:
                return builder.AddPivotContinuation(recipe.Index0 < _pivotContinuations.Length
                    ? _pivotContinuations[recipe.Index0]
                    : search.GeneratedPivotContinuation(recipe.Index0 - _pivotContinuations.Length));
            case MainKind.Slice:
                {
                    SliceWalk slice = _sliceWalks[recipe.Index0];
                    return builder.OpenFromIdle(slice.Prefix.FromIdle, ArrivalStage.Travel)
                           && builder.Add(slice.Prefix.FromIdle, slice.Prefix.Next.A, 0, ArrivalStage.Travel)
                           && builder.Add(slice.Prefix.Next, slice.ToIdle.A, 0, ArrivalStage.Travel)
                           && builder.AddIdle(slice.ToIdle);
                }

            case MainKind.Walk:
                {
                    WalkPrefix prefix = _walkPrefixes[recipe.Index0];
                    return builder.OpenFromIdle(prefix.FromIdle, ArrivalStage.Travel)
                           && builder.Add(prefix.FromIdle, prefix.Next.A, 0, ArrivalStage.Travel)
                           && AddWalkTail(builder, prefix.Next, recipe.Settled, recipe.Tail);
                }

            case MainKind.ArcStop:
                {
                    WalkPrefix prefix = _arcPrefixes[recipe.Index0];
                    Leave stop = _stops[prefix.Next.In][recipe.Index1];
                    double elapsed = prefix.Next.Settle + LoopBase(prefix.Next.In, prefix.Next.Settled, stop.Join.A) + (recipe.Cycles0 * Length(prefix.Next.In));
                    return builder.OpenFromIdle(prefix.FromIdle, ArrivalStage.Travel)
                           && builder.Add(prefix.FromIdle, prefix.Next.A, 0, ArrivalStage.Travel)
                           && builder.Add(prefix.Next, stop.Join.A, CyclesOf(prefix.Next.In, prefix.Next.B, stop.Join.A, elapsed), ArrivalStage.Travel)
                           && builder.Add(stop.Join, stop.ToIdle!.A, 0, ArrivalStage.Travel)
                           && builder.AddIdle(stop.ToIdle);
                }

            case MainKind.ArcSwitch:
                {
                    WalkPrefix prefix = _arcPrefixes[recipe.Index0];
                    Leave lane = _switches[prefix.Next.In][recipe.Index1];
                    double elapsed = prefix.Next.Settle + LoopBase(prefix.Next.In, prefix.Next.Settled, lane.Join.A) + (recipe.Cycles0 * Length(prefix.Next.In));
                    return builder.OpenFromIdle(prefix.FromIdle, ArrivalStage.Travel)
                           && builder.Add(prefix.FromIdle, prefix.Next.A, 0, ArrivalStage.Travel)
                           && builder.Add(prefix.Next, lane.Join.A, CyclesOf(prefix.Next.In, prefix.Next.B, lane.Join.A, elapsed), ArrivalStage.Travel)
                           && AddWalkTail(builder, lane.Join, recipe.Settled, recipe.Tail);
                }

            default:
                return false;
        }
    }

    /// <summary>The walk cycle entered by <paramref name="into"/>, then a tabulated tail to Idle.</summary>
    private bool AddWalkTail(PlanBuilder builder, Join into, int settledIndex, int tailIndex)
    {
        Tail tail = _walkTails[settledIndex][tailIndex];
        Leave stop = _stops[_walk!][tail.Stop];
        double elapsed = into.Settle + tail.Solo + (tail.Cycles * Length(_walk!));
        return builder.Add(into, stop.Join.A, CyclesOf(_walk!, into.B, stop.Join.A, elapsed), ArrivalStage.Travel)
               && builder.Add(stop.Join, stop.ToIdle!.A, 0, ArrivalStage.Travel)
               && builder.AddIdle(stop.ToIdle);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Start prefixes: leaving a clip that is playing mid-sequence.
    // ---------------------------------------------------------------------------------------------------------------

    private List<StartPrefix> StartPrefixes(SeamClip clip, double position, double earliest, bool prompt, Search search, out bool travelling)
    {
        List<StartPrefix> prefixes = [];
        string role = clip.Role;
        double from = clip.WrapsPlayback ? clip.Normalise(earliest) : earliest;
        List<Join> firsts = [];
        string[] successors = clip.Kind switch
        {
            // A non-prompt travelling walk may also switch lanes into an arc (a route bend or a redirect);
            // a prompt stop still leaves through exits only, unchanged.
            SeamClipRole.Cycle when !prompt && role == _walk && _arcRoles.Length > 0
                => [.. _exits, .. TurnSideArcs(search.LocalTarget)],
            SeamClipRole.Cycle => [.. _exits, .. role != _walk && _walk is not null ? [_walk] : Array.Empty<string>()],
            SeamClipRole.Entry when !prompt && _arcRoles.Length > 0
                => _walk is null ? [.. _exits, .. TurnSideArcs(search.LocalTarget)] : [.. _exits, _walk, .. TurnSideArcs(search.LocalTarget)],
            SeamClipRole.Entry => _walk is null ? _exits : [.. _exits, _walk],
            SeamClipRole.Idle => throw new NotImplementedException(),
            // An interrupted confined manoeuvre may continue through its aligned self seam or leave for Idle (TR14);
            // an exit leaves at its first aligned Idle seam, then planning resumes from that Idle start.
            SeamClipRole.Confined when _seams.TryGetPair(role, role, out SeamPair? continuation)
                && IsUsedPair(continuation!) => [_idle.Role, role],
            SeamClipRole.Exit or SeamClipRole.Confined => [_idle.Role],
            _ => [_idle.Role],
        };
        if (prompt)
        {
            if (_seams.TryFindNextAligned(role, successors, from, _tolerance, out SeamChoice choice))
            {
                foreach (string successor in successors)
                {
                    if (_seams.TryFindNextAligned(role, successor, from, _tolerance, out SeamChoice next)
                        && Math.Abs(next.DelaySeconds - choice.DelaySeconds) <= TimeTolerance)
                    {
                        firsts.Add(MakeJoin(next.Pair, next.Candidate));
                    }
                }
            }
        }
        else
        {
            foreach (string successor in successors)
            {
                // A redirect's seam times are ordered by their delay from the current position (the soonest seams
                // first) rather than by separation alone, so an early-cycle seam cannot force a late redirect; the
                // arc menu bound still limits how many are kept, and exits keep every distinct time.
                IEnumerable<Join> menu = MenuFrom(role, successor, from, clip.WrapsPlayback)
                    .OrderBy(join => clip.WrapsPlayback ? LoopBase(role, from, join.A) : Math.Max(0d, join.A - from))
                    .ThenBy(join => join.Separation);
                if (successor != _walk && _seams.Clips[successor].Kind == SeamClipRole.Cycle)
                {
                    menu = menu.Take(Math.Max(1, Config.ArcSeamOptionsPerPair));
                }

                firsts.AddRange(menu);
            }
        }

        // A finite confined manoeuvre or exit with no aligned seam into Idle before its end plays to its end and
        // hands off to Idle through its authored successor (Technical Requirement 14): the handoff is the pair's
        // latest usable seam at or after the playback position, whose blend window ends at the clip end. Verification
        // still judges the seam's alignment, as it does every other join. A looping confined manoeuvre always has an
        // aligned exit seam per loop, so only these finite clips reach this.
        if (firsts.Count == 0
            && clip.Kind is SeamClipRole.Exit or SeamClipRole.Confined
            && !clip.WrapsPlayback
            && _seams.TryGetPair(role, _idle.Role, out SeamPair? handoff)
            && handoff!.Candidates.Where(candidate => handoff.IsUsable(candidate) && candidate.OutgoingTime >= from - TimeTolerance)
                .MaxBy(candidate => candidate.OutgoingTime) is { } end)
        {
            firsts.Add(MakeJoin(handoff, end));
        }

        travelling = !prompt && clip.Kind is SeamClipRole.Cycle or SeamClipRole.Entry;
        foreach (Join first in firsts)
        {
            double delay = clip.WrapsPlayback ? LoopBase(role, from, first.A) : first.A - from;
            double elapsed = earliest - position + delay;
            PlanarMotion lead = Solo(role, position, elapsed);
            int exitCycles = CyclesOf(role, position, first.A, elapsed);
            if (first.In == _idle.Role)
            {
                prefixes.Add(StartPrefix.Direct(first.A, exitCycles, lead.Then(first.Motion), first, elapsed));
                continue;
            }

            SeamClip next = _seams.Clips[first.In];
            if (next.Kind == SeamClipRole.Confined && first.In == role)
            {
                foreach (Join toIdle in Menu(role, _idle.Role, byOutgoing: true, Config.ExitToIdleOptions))
                {
                    double solo = LoopBase(role, first.Settled, toIdle.A);
                    double continued = first.Settle + solo;
                    PlanarMotion motion = lead.Then(first.Motion).Then(Solo(role, first.Settled, solo)).Then(toIdle.Motion);
                    prefixes.Add(StartPrefix.Through(first.A, exitCycles,
                        [(first, toIdle.A, CyclesOf(role, first.B, toIdle.A, continued))],
                        toIdle, motion, elapsed + continued, continues: false));
                }

                continue;
            }

            if (next.Kind == SeamClipRole.Exit)
            {
                foreach (Join toIdle in ToIdleFrom(first))
                {
                    PlanarMotion motion = lead.Then(first.Motion).Then(Solo(first.In, first.Settled, toIdle.A - first.Settled)).Then(toIdle.Motion);
                    prefixes.Add(StartPrefix.Through(first.A, exitCycles, [(first, toIdle.A, 0)], toIdle, motion, elapsed + (toIdle.A - first.B), continues: false));
                }

                continue;
            }

            // Into the forward walk cycle, then a tabulated tail: the shortest stops, and (travelling) the tails
            // whose distance fits the target.
            if (first.In == _walk)
            {
                PlanarMotion head = lead.Then(first.Motion);
                int settledIndex = GridIndex(first.In, first.Settled);
                Tail[] tails = _walkTails[settledIndex];
                IEnumerable<int> chosen = Enumerable.Range(0, tails.Length).Where(index => tails[index].Cycles == 0);
                foreach (int index in chosen)
                {
                    prefixes.Add(WalkStartPrefix(first, head, exitCycles, elapsed, settledIndex, index, continues: false));
                }

                if (travelling)
                {
                    foreach (int index in NearestTails(settledIndex, head, search.LocalDistance))
                    {
                        prefixes.Add(WalkStartPrefix(first, head, exitCycles, elapsed, settledIndex, index, continues: true));
                    }
                }

                continue;
            }

            // A lane switch into an arc (from the forward walk): the arc turns the heading, then its own lane
            // switch back into the walk, then tabulated tails — the travelling one-bend continuation.
            foreach (Leave lane in _switches.TryGetValue(first.In, out Leave[]? lanes) ? lanes : [])
            {
                double solo = LoopBase(first.In, first.Settled, lane.Join.A);
                double intoWalk = elapsed + first.Settle + solo + lane.Join.Settle;
                int settledIndex = GridIndex(_walk!, lane.Join.Settled);
                for (int cycles = 0; cycles < lane.CyclesThen.Length; cycles++)
                {
                    PlanarMotion head = lead.Then(first.Motion).Then(Solo(first.In, first.Settled, solo)).Then(lane.CyclesThen[cycles]);
                    IEnumerable<int> chosen = Enumerable.Range(0, _walkTails[settledIndex].Length)
                        .Where(index => _walkTails[settledIndex][index].Cycles == 0);
                    foreach (int index in chosen)
                    {
                        prefixes.Add(ArcStartPrefix(first, lane, cycles, head, exitCycles, intoWalk + (cycles * Length(first.In)), settledIndex, index, continues: false));
                    }

                    if (travelling)
                    {
                        foreach (int index in NearestTails(settledIndex, head, search.LocalDistance))
                        {
                            prefixes.Add(ArcStartPrefix(first, lane, cycles, head, exitCycles, intoWalk + (cycles * Length(first.In)), settledIndex, index, continues: true));
                        }
                    }
                }
            }
        }

        // A travelling cycle may also keep travelling in itself before its exit.
        if (travelling && clip.Kind == SeamClipRole.Cycle && _stops.TryGetValue(role, out Leave[]? stops))
        {
            foreach (Leave stop in stops)
            {
                double delay = LoopBase(role, from, stop.Join.A);
                double elapsed = earliest - position + delay;
                PlanarMotion lead = Solo(role, position, elapsed);
                for (int cycles = 1; cycles < stop.CyclesThen.Length; cycles++)
                {
                    double total = elapsed + (cycles * clip.DurationSeconds);
                    prefixes.Add(StartPrefix.Through(
                        stop.Join.A,
                        CyclesOf(role, position, stop.Join.A, total),
                        [(stop.Join, stop.ToIdle!.A, 0)],
                        stop.ToIdle,
                        lead.Then(stop.CyclesThen[cycles]),
                        total + stop.Duration,
                        continues: true));
                }
            }
        }

        return prefixes;
    }

    private StartPrefix WalkStartPrefix(Join first, PlanarMotion head, int exitCycles, double elapsed, int settledIndex, int tailIndex, bool continues)
    {
        Tail tail = _walkTails[settledIndex][tailIndex];
        Leave stop = _stops[_walk!][tail.Stop];
        double walkElapsed = first.Settle + tail.Solo + (tail.Cycles * Length(_walk!));
        return StartPrefix.Through(
            first.A,
            exitCycles,
            [(first, stop.Join.A, CyclesOf(_walk!, first.B, stop.Join.A, walkElapsed)), (stop.Join, stop.ToIdle!.A, 0)],
            stop.ToIdle,
            head.Then(tail.Motion),
            elapsed + walkElapsed + stop.Duration,
            continues);
    }

    /// <summary>
    /// A travelling start redirected through an arc: start clip → arc (whole cycles) → lane switch back into
    /// the walk → tabulated tail, ending in Idle. The arc segment enters through <paramref name="intoArc"/> and
    /// the walk through the arc's own lane switch.
    /// </summary>
    private StartPrefix ArcStartPrefix(
        Join intoArc,
        Leave lane,
        int arcCycles,
        PlanarMotion head,
        int exitCycles,
        double intoWalk,
        int settledIndex,
        int tailIndex,
        bool continues)
    {
        Tail tail = _walkTails[settledIndex][tailIndex];
        Leave stop = _stops[_walk!][tail.Stop];
        double arcElapsed = intoArc.Settle + LoopBase(intoArc.In, intoArc.Settled, lane.Join.A) + (arcCycles * Length(intoArc.In));
        double walkElapsed = lane.Join.Settle + tail.Solo + (tail.Cycles * Length(_walk!));
        return StartPrefix.Through(
            intoArc.A,
            exitCycles,
            [
                (intoArc, lane.Join.A, CyclesOf(intoArc.In, intoArc.B, lane.Join.A, arcElapsed)),
                (lane.Join, stop.Join.A, CyclesOf(_walk!, lane.Join.B, stop.Join.A, walkElapsed)),
                (stop.Join, stop.ToIdle!.A, 0)
            ],
            stop.ToIdle,
            head.Then(tail.Motion),
            intoWalk + walkElapsed + stop.Duration,
            continues);
    }

    private IEnumerable<int> NearestTails(int settledIndex, PlanarMotion head, double distance)
    {
        double[] keys = _walkTailKeys[settledIndex];
        (double sin, double cos) = Math.SinCos(head.Yaw);
        double along = (-sin * head.X) + (cos * head.Y);
        double discriminant = (along * along) - ((head.X * head.X) + (head.Y * head.Y)) + (distance * distance);
        double needed = -along + (discriminant > 0d ? Math.Sqrt(discriminant) : 0d);
        int found = Array.BinarySearch(keys, needed);
        int centre = found >= 0 ? found : ~found;
        for (int index = Math.Max(0, centre - Config.TailNeighbours); index < Math.Min(keys.Length, centre + Config.TailNeighbours); index++)
        {
            yield return index;
        }
    }

    /// <summary>The lowest-separation aligned seam at each distinct outgoing time at or after a playback position.</summary>
    private IEnumerable<Join> MenuFrom(string outgoing, string incoming, double from, bool wraps)
    {
        return !_seams.TryGetPair(outgoing, incoming, out SeamPair? pair) || !IsUsedPair(pair!)
            ? []
            : [.. pair!.Candidates
            .Where(candidate => pair.IsUsable(candidate) && candidate.IsAlignedWithin(_tolerance) && (wraps || candidate.OutgoingTime >= from - TimeTolerance))
            .GroupBy(candidate => Math.Round(candidate.OutgoingTime * pair.Outgoing.KeyRateHz))
            .Select(group => group.MinBy(candidate => (candidate.PlantedSeparation, candidate.MaxSeparation)))
            .OrderBy(candidate => candidate.PlantedSeparation).ThenBy(candidate => candidate.MaxSeparation)
            .Take(Config.SeamOptionsPerPair > 0 ? Config.SeamOptionsPerPair : int.MaxValue)
            .Select(candidate => MakeJoin(pair, candidate))];
    }

    /// <summary>
    /// Enumerates the pivot-led section compositions for one confined pivot block: the block plays,
    /// hands off through the planner-set pivot→Idle→entry pairs into an entry clip, and walks the existing
    /// entry→walk prefix along the new heading (or runs the arc-led variant, ending mid-arc). The hand-off
    /// chooses from the idle→entry joins REACHABLE from where this block's exit settles (best separation
    /// first, then earliest dwell), within the idle dwell bound OpenFromIdle enforces; the wrap past the
    /// idle loop's end is materialised as the idle segment's extra cycle. Every join stays an aligned
    /// planner-set pair; the section still ends mid-walk.
    /// </summary>
    /// <param name="displacement">The station displacement in the start pose's frame.</param>
    /// <param name="candidates">The candidate list to append to.</param>
    /// <param name="pivotIn">The pivot block's entry join (Idle→pivot).</param>
    /// <param name="pivotSegments">The pivot block's own segments (entry join and restart joins).</param>
    /// <param name="pivotOut">The pivot block's exit join (pivot→Idle).</param>
    /// <param name="pivotMotion">The pivot block's composed motion.</param>
    /// <param name="pivotSteps">The pivot block's step count.</param>
    /// <param name="pivotSeparation">The pivot block's separation.</param>
    /// <param name="pivotDuration">The pivot block's duration.</param>
    /// <param name="leadMotion">Optional pre-pivot head motion (identity for the from-idle start; the
    /// travelling branch supplies run-to-exit + exit-into-Idle + the interior idle dwell).</param>
    /// <param name="leadSegments">Optional segments played before the pivot block (empty from Idle).</param>
    /// <param name="startExit">The start clip's exit when a lead is present (the exit seam's A).</param>
    /// <param name="startCycles">The start clip's cycles when a lead is present.</param>
    /// <param name="leadSteps">Extra step count of the lead.</param>
    /// <param name="leadSeparation">The lead's separation (maxed with the pivot's).</param>
    /// <param name="leadDuration">The lead's duration.</param>
    /// <returns>The number of compositions enumerated.</returns>
    private int AddPivotLedSectionCandidates(
        PlanarMotion displacement,
        List<SectionCandidate> candidates,
        Join pivotIn,
        (Join Join, double Exit, int Cycles)[] pivotSegments,
        Join pivotOut,
        PlanarMotion pivotMotion,
        int pivotSteps,
        float pivotSeparation,
        double pivotDuration,
        PlanarMotion? leadMotion = null,
        (Join Join, double Exit, int Cycles)[]? leadSegments = null,
        double startExit = 0d,
        int startCycles = 0,
        int leadSteps = 0,
        float leadSeparation = 0f,
        double leadDuration = 0d)
    {
        int compositions = 0;
        bool fromIdle = leadMotion is null;
        string walk = _walk ?? throw new InvalidOperationException("A pivot-led section needs a forward walk cycle.");
        PlanarMotion lead = leadMotion ?? PlanarMotion.Identity;
        foreach (string entry in _entries)
        {
            // Every aligned idle→entry candidate, grouped by the entry seek position, best separation first per
            // group: the bind-time menu keeps only one join per seek (the global min separation), which can be
            // unreachable from where the pivot's exit settles — the hand-off needs the joins that ARE reachable.
            if (!_seams.TryGetPair(_idle.Role, entry, out SeamPair? fromIdlePair)
                || !IsUsedPair(fromIdlePair!))
            {
                continue;
            }

            List<(Join FromIdle, double Dwell)> reachable = [];
            foreach (IGrouping<int, SeamCandidate> group in fromIdlePair!.Candidates
                .Where(candidate => fromIdlePair.IsUsable(candidate) && candidate.IsAlignedWithin(_tolerance))
                .GroupBy(candidate => (int)Math.Round(candidate.IncomingTime * fromIdlePair.Incoming.KeyRateHz)))
            {
                foreach (SeamCandidate candidate in group
                    .OrderBy(candidate => candidate.PlantedSeparation)
                    .ThenBy(candidate => candidate.MaxSeparation)
                    .Take(Math.Max(1, Config.WalkEntryOptions)))
                {
                    Join handoff = MakeJoin(fromIdlePair, candidate);
                    if (TryIdleDwell(handoff, pivotOut, out double dwell))
                    {
                        reachable.Add((handoff, dwell));
                    }
                }
            }

            foreach (WalkPrefix prefix in reachable
                .OrderBy(slot => slot.FromIdle.Separation)
                .ThenBy(slot => slot.Dwell)
                .Take(Math.Max(1, Config.WalkEntryOptions) * IdleSectionEntryOptions)
                .SelectMany(slot => Prefixes(slot.FromIdle, walk, Config.SeamOptionsPerPair)))
            {
                if (!TryIdleDwell(prefix.FromIdle, pivotOut, out double dwell))
                {
                    continue;
                }

                int idleCycles = prefix.FromIdle.A + TimeTolerance < pivotOut.Settled ? 1 : 0;
                PlanarMotion head = lead
                    .Then(pivotMotion)
                    .Then(Solo(_idle.Role, pivotOut.Settled, dwell))
                    .Then(prefix.Motion);
                foreach ((PlanarMotion motion, double seconds) in WalkRuns(head, walk, prefix.Next.Settled, displacement))
                {
                    RunExit(walk, prefix.Next.B, prefix.Next.Settle + seconds, out double exit, out int cycles);
                    candidates.Add(new SectionCandidate(
                        fromIdle ? pivotIn.A : startExit,
                        fromIdle ? 0 : startCycles,
                        [.. leadSegments ?? [], .. pivotSegments, (pivotOut, prefix.FromIdle.A, idleCycles), (prefix.FromIdle, prefix.Next.A, 0), (prefix.Next, exit, cycles)],
                        motion,
                        leadSteps + pivotSteps + 3 + idleCycles + cycles,
                        Math.Max(Math.Max(leadSeparation, pivotSeparation), prefix.Separation),
                        leadDuration + pivotDuration + dwell + prefix.Duration + seconds,
                        FromIdle: fromIdle));
                    compositions++;
                }
            }
        }

        // Arc-led variant: the reachable pivot blocks step yaw by the pivot's whole-cycle quantum, which can leave
        // a residual bearing no straight walk run closes; the entry→arc head curves the walk onto the bearing and
        // the section ends mid-arc — the same shape the travelling branch uses for a bend (TR22: a travelling
        // heading change is an arc).
        foreach (WalkPrefix arcPrefix in _arcPrefixes)
        {
            if (!TryIdleDwell(arcPrefix.FromIdle, pivotOut, out double dwell))
            {
                continue;
            }

            int idleCycles = arcPrefix.FromIdle.A + TimeTolerance < pivotOut.Settled ? 1 : 0;
            PlanarMotion head = lead
                .Then(pivotMotion)
                .Then(Solo(_idle.Role, pivotOut.Settled, dwell))
                .Then(arcPrefix.Motion);
            foreach ((PlanarMotion motion, double seconds, int steps) in ArcRuns(head, arcPrefix.Next.In, arcPrefix.Next.Settled, displacement))
            {
                RunExit(arcPrefix.Next.In, arcPrefix.Next.B, arcPrefix.Next.Settle + seconds, out double exit, out int cycles);
                candidates.Add(new SectionCandidate(
                    fromIdle ? pivotIn.A : startExit,
                    fromIdle ? 0 : startCycles,
                    [.. leadSegments ?? [], .. pivotSegments, (pivotOut, arcPrefix.FromIdle.A, idleCycles), (arcPrefix.FromIdle, arcPrefix.Next.A, 0), (arcPrefix.Next, exit, cycles)],
                    motion,
                    leadSteps + pivotSteps + steps + idleCycles + 1,
                    Math.Max(Math.Max(leadSeparation, pivotSeparation), arcPrefix.Separation),
                    leadDuration + pivotDuration + dwell + arcPrefix.Duration + seconds,
                    FromIdle: fromIdle));
                compositions++;
            }
        }

        return compositions;
    }

    /// <summary>
    /// Enumerates the TRAVELLING pivot-led section family for a sharp-bearing station (CORNER-stuck): the
    /// current travel clip runs to an exit seam, the exit stops into Idle through its own Idle seam, the
    /// pivot block (or continuation) nearest the station's bearing turns, and the shared pivot-led tail
    /// (reachable Idle→entry hand-off, then the entry→walk prefix or the arc-led head) runs along the new
    /// heading. All seams come from the existing planner-set menus (start→exit CycleToExit/EntryToExit,
    /// exit→Idle, Idle→pivot, pivot→self, Idle→entry, entry→walk); the interior Idle dwell between the
    /// exit's settle and the pivot's entry uses the same bound and wrap-cycle encoding as the from-idle
    /// hand-off; the enumeration is bounded by the existing menu limits and the pivot window.
    /// </summary>
    /// <returns>The number of compositions enumerated.</returns>
    private int AddTravellingPivotLedSectionCandidates(
        ArrivalStart start,
        double earliest,
        SeamClip clip,
        double from,
        PlanarMotion displacement,
        List<SectionCandidate> candidates)
    {
        if (_walk is null || _exits.Length == 0)
        {
            return 0;
        }

        int compositions = 0;
        double bearing = ArrivalMovementChoice.BearingOf(displacement.X, displacement.Y);
        foreach (Join exitSeam in MenuFrom(start.Role, _exits[0], from, clip.WrapsPlayback)
            .Concat(MenuFrom(start.Role, _exits[1], from, clip.WrapsPlayback))
            .OrderBy(join => clip.WrapsPlayback ? LoopBase(start.Role, from, join.A) : Math.Max(0d, join.A - from))
            .ThenBy(join => join.Separation)
            .Take(SectionJoinOptions))
        {
            double delay = clip.WrapsPlayback ? LoopBase(start.Role, from, exitSeam.A) : exitSeam.A - from;
            double elapsed = earliest - start.PositionSeconds + delay;
            if (elapsed < -TimeTolerance)
            {
                continue;
            }

            PlanarMotion lead = Solo(start.Role, start.PositionSeconds, elapsed).Then(exitSeam.Motion);
            int exitCycles = CyclesOf(start.Role, start.PositionSeconds, exitSeam.A, elapsed);
            foreach (Join toIdle in ToIdleFrom(exitSeam))
            {
                // The interior Idle dwell from the exit's settle to the pivot's entry, with the same wrap and
                // bound the from-idle hand-off applies.
                IEnumerable<(Join PivotIn, (Join Join, double Exit, int Cycles)[] Segments, Join Out, PlanarMotion Motion, int Steps, float Separation, double Duration)> blocks =
                [
                    .. PivotBlockWindow(bearing).Select(pivot => (pivot.In, new (Join, double, int)[] { (pivot.In, pivot.Out.A, pivot.Cycles) }, pivot.Out, pivot.Motion, pivot.Steps, pivot.Separation, pivot.Duration)),
                    .. PivotContinuationWindow(bearing).Select(continuation => (continuation.Entry, new (Join, double, int)[]
                    {
                        (continuation.Entry, continuation.Restart.A, continuation.BeforeCycles),
                        (continuation.Restart, continuation.Exit.A, continuation.AfterCycles),
                    }, continuation.Exit, continuation.Motion, continuation.Steps, continuation.Separation, continuation.Duration)),
                ];
                foreach ((Join pivotIn, (Join Join, double Exit, int Cycles)[] pivotSegments, Join pivotOut, PlanarMotion pivotMotion, int pivotSteps, float pivotSeparation, double pivotDuration) in blocks)
                {
                    double idleDwell = pivotIn.A - toIdle.Settled;
                    int idleCycles = 0;
                    if (idleDwell < -TimeTolerance)
                    {
                        idleDwell += Length(_idle.Role);
                        idleCycles = 1;
                    }

                    if (idleDwell < -TimeTolerance || idleDwell > Config.MaxIdleDwellSeconds + TimeTolerance)
                    {
                        continue;
                    }

                    idleDwell = Math.Max(0d, idleDwell);
                    PlanarMotion head = lead
                        .Then(toIdle.Motion)
                        .Then(Solo(_idle.Role, toIdle.Settled, idleDwell));
                    (Join, double, int)[] leadSegments =
                    [
                        (exitSeam, toIdle.A, exitCycles),
                        (toIdle, pivotIn.A, idleCycles),
                    ];
                    compositions += AddPivotLedSectionCandidates(
                        displacement,
                        candidates,
                        pivotIn,
                        pivotSegments,
                        pivotOut,
                        pivotMotion,
                        pivotSteps,
                        pivotSeparation,
                        pivotDuration,
                        head,
                        leadSegments,
                        exitSeam.A,
                        exitCycles,
                        2,
                        Math.Max(exitSeam.Separation, toIdle.Separation),
                        elapsed + toIdle.A - exitSeam.B + toIdle.Settle + idleDwell);
                }
            }
        }

        return compositions;
    }

    /// <summary>The confined pivot blocks whose yaw is nearest the station's bearing (a local window, an
    /// enumeration bound): each block bakes its own entry/exit solo pair and a block whose hand-offs do not
    /// reach contributes nothing, so the window lets the endpoint window and the section ranking choose a
    /// viable block instead of failing on the single nearest one.</summary>
    private IEnumerable<Manoeuvre> PivotBlockWindow(double bearing)
    {
        int centre = Array.BinarySearch(_pivotYaws, bearing);
        if (centre < 0)
        {
            centre = ~centre;
        }

        for (int index = Math.Max(0, centre - IdleSectionPivotWindow); index <= Math.Min(_pivotYaws.Length - 1, centre + IdleSectionPivotWindow - 1); index++)
        {
            yield return _pivots[index];
        }
    }

    /// <summary>The pivot-continuation blocks nearest the bearing (two solo portions land between the
    /// single-block families' yaw gaps), nearest first, bounded.</summary>
    private IEnumerable<PivotContinuation> PivotContinuationWindow(double bearing)
        => _pivotContinuations
            .Select(option => (Option: option, Error: Math.Abs(Mathf.Wrap(option.Motion.Yaw - bearing, -Math.PI, Math.PI))))
            .OrderBy(option => option.Error)
            .ThenBy(option => option.Option.Separation)
            .Take(IdleSectionPivotWindow * 2)
            .Select(option => option.Option);

    /// <summary>
    /// The idle dwell a pivot block's exit needs before an idle→entry join's slot is reachable: the solo after
    /// the pivot→Idle fade settles, plus one whole idle loop when the slot lies behind the settle point.
    /// Unreachable when the dwell would exceed the idle dwell bound.
    /// </summary>
    private bool TryIdleDwell(Join fromIdle, Join pivotOut, out double dwell)
    {
        dwell = fromIdle.A - pivotOut.Settled;
        if (dwell < -TimeTolerance)
        {
            dwell += Length(_idle.Role);
        }

        if (dwell < -TimeTolerance || dwell > Config.MaxIdleDwellSeconds + TimeTolerance)
        {
            dwell = double.NaN;
            return false;
        }

        dwell = Math.Max(0d, dwell);
        return true;
    }

    private bool IsUsedPair(SeamPair pair) => _used.Contains(pair);

    // ---------------------------------------------------------------------------------------------------------------
    // Bind-time menus.
    // ---------------------------------------------------------------------------------------------------------------

    private string? RoleOf(StandingLocomotionCapability capability)
        => _seams.Clips.Values.FirstOrDefault(clip => clip.Capability == capability && _seams.IsInPlannerSet(clip.Role))?.Role;

    /// <summary>
    /// Arcs whose native turn is towards the target's bearing in the actor frame, for a travelling redirect;
    /// with no decided side (a straight-ahead target) no arc is offered and only straight continuations exist.
    /// </summary>
    private string[] TurnSideArcs(PlanarMotion target)
    {
        double bearing = ArrivalMovementChoice.BearingOf(target.X, target.Y);
        return Math.Abs(bearing) < _alignSlackRadians
            ? []
            : [.. _arcRoles.Where(role => Math.Sign(Solo(role, 0d, Length(role)).Yaw) == Math.Sign(bearing))];
    }

    private string[] Roles(params StandingLocomotionCapability[] capabilities)
        => [.. capabilities.Select(RoleOf).OfType<string>()];

    /// <summary>
    /// Whether the planner may enter a confined manoeuvre: a looping one needs an aligned exit per loop
    /// (<see cref="SeamTable.IsPlannable"/>); one played once needs only to be in the planner set.
    /// </summary>
    private bool IsEnterable(string role)
        => _seams.Clips[role].WrapsPlayback ? _seams.IsPlannable(role, _tolerance) : _seams.IsInPlannerSet(role);

    /// <summary>
    /// The lowest-separation aligned seam per distinct outgoing (or incoming) time of one pair, lowest separation
    /// first, at most <paramref name="limit"/> (all when not positive), each with its crossfade motion.
    /// </summary>
    private List<Join> Menu(string outgoing, string incoming, bool byOutgoing, int limit)
    {
        if (!_seams.TryGetPair(outgoing, incoming, out SeamPair? pair))
        {
            return [];
        }

        List<Join> joins = [.. pair!.Candidates
            .Where(candidate => pair.IsUsable(candidate) && candidate.IsAlignedWithin(_tolerance))
            .GroupBy(candidate => Math.Round((byOutgoing ? candidate.OutgoingTime : candidate.IncomingTime) * (byOutgoing ? pair.Outgoing : pair.Incoming).KeyRateHz))
            .Select(group => group.MinBy(candidate => (candidate.PlantedSeparation, candidate.MaxSeparation)))
            .OrderBy(candidate => candidate.PlantedSeparation).ThenBy(candidate => candidate.MaxSeparation)
            .ThenBy(candidate => candidate.OutgoingTime)
            .Take(limit > 0 ? limit : int.MaxValue)
            .Select(candidate => MakeJoin(pair, candidate))];
        if (joins.Count > 0)
        {
            _ = _used.Add(pair);
        }

        return joins;
    }

    /// <summary>
    /// The legacy fixed-capacity self-pivot menu: the lowest-separation aligned seam at each distinct outgoing time.
    /// Repeated-use seams are not reserved here — a request-independent throughput table misleads for chains whose
    /// value depends on the request — but chosen per request over the raw self-seam candidates by the complete-chain
    /// comparison of <see cref="SearchRepeatedPivotContinuations"/>.
    /// </summary>
    private List<Join> PivotContinuationMenu(string role)
        => Config.ArcSeamOptionsPerPair <= 0 ? [] : Menu(role, role, byOutgoing: true, Config.ArcSeamOptionsPerPair);

    /// <summary>
    /// Idle seams into a clip, one per distinct entry position (its lowest-separation Idle time stands for it; the
    /// materialised plan uses the earliest aligned Idle time the Idle reaches), and the placement index of every
    /// aligned Idle time per entry position.
    /// </summary>
    private List<Join> IdleMenu(string incoming)
    {
        List<Join> joins = Menu(_idle.Role, incoming, byOutgoing: false, 0);
        if (_seams.TryGetPair(_idle.Role, incoming, out SeamPair? pair) && !_idleSlots.ContainsKey(incoming))
        {
            _idleSlots[incoming] = pair!.Candidates
                .Where(candidate => pair.IsUsable(candidate) && candidate.IsAlignedWithin(_tolerance))
                .GroupBy(candidate => (int)Math.Round(candidate.IncomingTime * pair.Incoming.KeyRateHz))
                .ToDictionary(group => group.Key, group => group.Select(candidate => new IdleSlot(candidate.OutgoingTime, candidate.PlantedSeparation)).OrderBy(slot => slot.A).ToArray());
        }

        return joins;
    }

    private Join MakeJoin(SeamPair pair, SeamCandidate candidate)
    {
        double settle = (SequencePredictor.CrossfadeTicks(pair.CrossfadeSeconds, _dt) - 1) * _dt;
        PlanarMotion motion = _predictor.CrossfadeMotion(pair.Outgoing.Role, candidate.OutgoingTime, pair.Incoming.Role, candidate.IncomingTime, pair.CrossfadeSeconds, _dt);
        return new Join(pair, candidate.OutgoingTime, candidate.IncomingTime, candidate.PlantedSeparation, motion, settle, pair.Incoming.Normalise(candidate.IncomingTime + settle));
    }

    /// <summary>Exit → Idle seams an exit entered by <paramref name="into"/> may leave through, lowest separation first.</summary>
    private IEnumerable<Join> ToIdleFrom(Join into)
        => _exitToIdle.TryGetValue(into.In, out Join[]? joins)
            ? joins.Where(join => join.A >= into.Settled - TimeTolerance).OrderBy(join => join.Separation).Take(Math.Max(1, Config.ExitToIdleOptions))
            : [];

    private Leave MakeLeave(Join join, Join? toIdle, int maxCycles)
    {
        PlanarMotion rest = join.Motion;
        double duration = join.Settle;
        float separation = join.Separation;
        if (toIdle is not null)
        {
            rest = rest.Then(Solo(join.In, join.Settled, toIdle.A - join.Settled)).Then(toIdle.Motion);
            duration = toIdle.A - join.B + toIdle.Settle;
            separation = Math.Max(separation, toIdle.Separation);
        }

        PlanarMotion cycle = Solo(join.Out, join.A, Length(join.Out));
        var cyclesThen = new PlanarMotion[maxCycles + 1];
        PlanarMotion lead = PlanarMotion.Identity;
        for (int cycles = 0; cycles <= maxCycles; cycles++)
        {
            cyclesThen[cycles] = lead.Then(rest);
            lead = lead.Then(cycle);
        }

        return new Leave(join, toIdle, cyclesThen, separation, duration);
    }

    private IEnumerable<WalkPrefix> Prefixes(Join fromIdle, string next, int options)
    {
        foreach (Join join in Menu(fromIdle.In, next, byOutgoing: true, options))
        {
            if (join.A < fromIdle.Settled - TimeTolerance)
            {
                continue;
            }

            PlanarMotion motion = fromIdle.Motion.Then(Solo(fromIdle.In, fromIdle.Settled, join.A - fromIdle.Settled)).Then(join.Motion);
            yield return new WalkPrefix(fromIdle, join, motion, Math.Max(fromIdle.Separation, join.Separation), join.A - fromIdle.B + join.Settle);
        }
    }

    /// <summary>
    /// Complete Idle → entry head → exit tail → Idle blocks. Separation alone cannot dominate a different
    /// entry seek, incoming tail or exit cut: all three change the endpoint, including their fade motion.
    /// Keep that diversity until the distance-indexed, composition-budgeted search ranks complete blocks.
    /// The compressed menus remain appropriate bounds for the other walk and manoeuvre families.
    /// </summary>
    private IEnumerable<SliceWalk> BuildDirectSlices()
    {
        foreach (string entry in _entries)
        {
            List<Join> entries = IdleMenu(entry);
            foreach (string exit in _exits)
            {
                if (!_seams.TryGetPair(entry, exit, out SeamPair? pair))
                {
                    continue;
                }

                foreach (SeamCandidate candidate in pair!.Candidates)
                {
                    if (!pair.IsUsable(candidate) || !candidate.IsAlignedWithin(_tolerance))
                    {
                        continue;
                    }

                    Join join = MakeJoin(pair, candidate);
                    foreach (Join fromIdle in entries)
                    {
                        if (join.A < fromIdle.Settled - TimeTolerance)
                        {
                            continue;
                        }

                        PlanarMotion head = fromIdle.Motion.Then(Solo(entry, fromIdle.Settled, join.A - fromIdle.Settled)).Then(join.Motion);
                        var prefix = new WalkPrefix(fromIdle, join, head, Math.Max(fromIdle.Separation, join.Separation), join.A - fromIdle.B + join.Settle);
                        foreach (Join toIdle in _exitToIdle[exit])
                        {
                            if (toIdle.A < join.Settled - TimeTolerance)
                            {
                                continue;
                            }

                            _ = _used.Add(pair);
                            PlanarMotion motion = head.Then(Solo(exit, join.Settled, toIdle.A - join.Settled)).Then(toIdle.Motion);
                            yield return new SliceWalk(prefix, toIdle, motion, Math.Max(prefix.Separation, toIdle.Separation), prefix.Duration + (toIdle.A - join.Settled) + toIdle.Settle);
                        }
                    }
                }
            }
        }
    }

    /// <summary>Idle → manoeuvre (whole loops and a partial step) → Idle blocks of one confined manoeuvre.</summary>
    private IEnumerable<Manoeuvre> BuildManoeuvres(string role)
    {
        SeamClip clip = _seams.Clips[role];
        List<Join> entries = IdleMenu(role);
        List<Join> exits = Menu(role, _idle.Role, byOutgoing: true, 0);
        int maxLoops = !clip.WrapsPlayback ? 0 : clip.Capability is StandingLocomotionCapability.ConfinedPivotLeft or StandingLocomotionCapability.ConfinedPivotRight
            ? Config.MaxPivotLoops
            : Config.MaxSideStepLoops;
        foreach (Join entry in entries)
        {
            foreach (Join exit in exits)
            {
                double solo = clip.WrapsPlayback ? LoopBase(role, entry.Settled, exit.A) : exit.A - entry.Settled;
                if (solo < -TimeTolerance)
                {
                    continue;
                }

                PlanarMotion head = entry.Motion.Then(Solo(role, entry.Settled, solo));
                PlanarMotion loop = Solo(role, exit.A, clip.DurationSeconds);
                for (int loops = 0; loops <= maxLoops; loops++)
                {
                    double elapsed = entry.Settle + solo + (loops * clip.DurationSeconds);
                    yield return new Manoeuvre(entry, exit, CyclesOf(role, entry.B, exit.A, elapsed), head.Then(exit.Motion), Math.Max(entry.Separation, exit.Separation), elapsed + exit.Settle, 1 + loops);
                    head = head.Then(loop);
                }
            }
        }
    }

    private IEnumerable<PivotContinuation> BuildPivotContinuations(string role)
    {
        if (!_seams.Clips[role].WrapsPlayback || !_seams.TryGetPair(role, role, out _))
        {
            yield break;
        }

        double length = Length(role);
        double cycleYaw = Math.Abs(Solo(role, 0d, length).Yaw);
        List<Join> entries = [.. IdleMenu(role).Take(Math.Max(1, Config.WalkEntryOptions))];
        List<Join> exits = Menu(role, _idle.Role, byOutgoing: true, 0);
        List<Join> self = PivotContinuationMenu(role);
        foreach (Join entry in entries)
        {
            foreach (Join restart in self)
            {
                double before = LoopBase(role, entry.Settled, restart.A);
                double unitSolo = LoopBase(role, restart.Settled, restart.A);
                double unitYaw = restart.Motion.Then(Solo(role, restart.Settled, unitSolo)).Yaw;
                double unitSeconds = restart.Settle + unitSolo + _dt;
                PlanarMotion head = entry.Motion.Then(Solo(role, entry.Settled, before));
                for (int beforeCycles = 0; beforeCycles <= Config.MaxPivotLoops; beforeCycles++)
                {
                    foreach (Join exit in exits)
                    {
                        double after = LoopBase(role, restart.Settled, exit.A);
                        PlanarMotion motion = head.Then(restart.Motion).Then(Solo(role, restart.Settled, after)).Then(exit.Motion);
                        for (int afterCycles = 0; beforeCycles + afterCycles <= Config.MaxPivotLoops; afterCycles++)
                        {
                            double duration = entry.Settle + before + (beforeCycles * length)
                                + restart.Settle + after + (afterCycles * length) + exit.Settle;
                            yield return new PivotContinuation(entry, restart, exit, beforeCycles, afterCycles, 1,
                                unitYaw, unitSeconds, motion,
                                Math.Max(Math.Max(entry.Separation, restart.Separation), exit.Separation), duration,
                                2 + beforeCycles + afterCycles, cycleYaw);
                            motion = motion.Then(Solo(role, exit.A, length));
                        }
                    }

                    head = head.Then(Solo(role, restart.A, length));
                }
            }
        }
    }

    /// <summary>
    /// Binds one looping pivot's repeated-continuation inputs: its native loop charge (a whole loop's yaw and
    /// seconds), the Idle entries and Idle exits a chain binds to, and every COMPLETE restart unit of the raw
    /// self-seam candidates — the full candidate list, before per-outgoing-time menu grouping would discard
    /// alternative incoming times. A unit is kept only when it is finite, positive, progresses in a consistent
    /// direction, can legally host the next request (its middle segment's playback covers the restart fade) and —
    /// from the clip's own planted-interval metadata, not from clip names — plays one full swing of each foot.
    /// </summary>
    private RepeatedPivotFamily BuildRepeatedPivotFamily(string role)
    {
        SeamClip clip = _seams.Clips[role];
        double length = clip.DurationSeconds;
        double cycleYaw = Math.Abs(Solo(role, 0d, length).Yaw);
        Join[] entries = [.. IdleMenu(role).Take(Math.Max(1, Config.WalkEntryOptions))];
        Join[] exits = [.. Menu(role, _idle.Role, byOutgoing: true, 0)];
        List<RepeatedPivotUnit> units = [];
        if (clip.WrapsPlayback && cycleYaw > TimeTolerance && _seams.TryGetPair(role, role, out SeamPair? pair))
        {
            foreach (SeamCandidate candidate in pair!.Candidates)
            {
                if (!pair.IsUsable(candidate) || !candidate.IsAlignedWithin(_tolerance))
                {
                    continue;
                }

                Join restart = MakeJoin(pair, candidate);
                double solo = LoopBase(role, restart.Settled, restart.A);
                double advance = restart.Settle + solo;
                if (!double.IsFinite(advance) || advance <= TimeTolerance
                    || LoopBase(role, restart.B, restart.A) < restart.Crossfade - TimeTolerance)
                {
                    continue;
                }

                PlanarMotion soloMotion = Solo(role, restart.Settled, solo);
                double unitYaw = restart.Motion.Then(soloMotion).Yaw;
                if (!double.IsFinite(unitYaw) || Math.Abs(unitYaw) <= TimeTolerance)
                {
                    continue;
                }

                if (!CoversCompleteSwingCycle(clip, restart.B, advance))
                {
                    continue;
                }

                units.Add(new RepeatedPivotUnit(restart, unitYaw, advance + _dt, soloMotion));
            }
        }

        return new RepeatedPivotFamily(role, cycleYaw, length, entries, exits, [.. units]);
    }

    /// <summary>
    /// Whether one looping clip's playback window — advancing <paramref name="seconds"/> from
    /// <paramref name="from"/>, as a restart unit's incoming clock does through its fade and solo — fully contains
    /// a swing interval of each foot (a complete alternating swing cycle), per the clip's planted-interval metadata.
    /// </summary>
    private static bool CoversCompleteSwingCycle(SeamClip clip, double from, double seconds)
    {
        double length = clip.DurationSeconds;
        return Covers(clip.Intervals.Left) && Covers(clip.Intervals.Right);
        bool Covers(ImmutableArray<PlantedIntervalSegment> foot)
        {
            foreach (PlantedIntervalSegment segment in foot)
            {
                if (segment.IsPlanted)
                {
                    continue;
                }

                for (long cycle = (long)Math.Floor((from - segment.EndSeconds) / length) - 1;
                    segment.StartSeconds + (cycle * length) <= from + seconds + TimeTolerance;
                    cycle++)
                {
                    double start = segment.StartSeconds + (cycle * length);
                    double end = segment.EndSeconds + (cycle * length);
                    if (from <= start + TimeTolerance && end <= from + seconds + TimeTolerance)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    private (Tail[][] Tails, double[][] Keys) BuildWalkTails()
    {
        if (_walk is null)
        {
            return ([], []);
        }

        SeamClip clip = _seams.Clips[_walk];
        Leave[] stops = _stops[_walk];
        var tails = new Tail[clip.GridTimeCount][];
        double[][] keys = new double[clip.GridTimeCount][];
        for (int index = 0; index < clip.GridTimeCount; index++)
        {
            double settled = clip.GridTime(index);
            List<Tail> list = [];
            for (int stopIndex = 0; stopIndex < stops.Length; stopIndex++)
            {
                Leave stop = stops[stopIndex];
                double solo = LoopBase(_walk, settled, stop.Join.A);
                PlanarMotion head = Solo(_walk, settled, solo);
                for (int cycles = 0; cycles < stop.CyclesThen.Length; cycles++)
                {
                    list.Add(new Tail(stopIndex, cycles, solo, head.Then(stop.CyclesThen[cycles]), solo + (cycles * clip.DurationSeconds) + stop.Duration));
                }
            }

            tails[index] = [.. list.OrderBy(tail => tail.Motion.Y)];
            keys[index] = [.. tails[index].Select(tail => tail.Motion.Y)];
        }

        return (tails, keys);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Timing helpers.
    // ---------------------------------------------------------------------------------------------------------------

    private PlanarMotion Solo(string role, double start, double elapsed) => _predictor.SoloMotion(role, start, elapsed);

    private double Length(string role) => _lengths[role];

    private int GridIndex(string role, double time)
    {
        SeamClip clip = _seams.Clips[role];
        int index = (int)Math.Round(clip.Normalise(time) * clip.KeyRateHz);
        return clip.WrapsPlayback && index >= clip.GridTimeCount ? 0 : index;
    }

    /// <summary>Playback from <paramref name="from"/> to the next time a looping clip reaches <paramref name="to"/> (zero when there).</summary>
    private double LoopBase(string role, double from, double to)
    {
        double length = Length(role);
        double value = (to - from) % length;
        if (value < 0d)
        {
            value += length;
        }

        return length - value <= 1e-5d ? 0d : value < 1e-5d ? 0d : value;
    }

    /// <summary>The <see cref="SequenceSegment.ExitCycles"/> that make a segment play <paramref name="elapsed"/>.</summary>
    private int CyclesOf(string role, double entry, double exit, double elapsed)
    {
        SeamClip clip = _seams.Clips[role];
        if (!clip.WrapsPlayback)
        {
            return 0;
        }

        double length = clip.DurationSeconds;
        double first = (exit - entry) % length;
        if (first < 0d)
        {
            first += length;
        }

        if (length - first <= 1e-5d)
        {
            first = 0d;
        }

        return Math.Max(0, (int)Math.Round((elapsed - first) / length));
    }

    private int RemainingCrossfadeTicks(SequenceCrossfadeProgress? fading)
    {
        if (fading is not { } progress)
        {
            return 0;
        }

        int total = SequencePredictor.CrossfadeTicks(progress.FadeLengthSeconds, _dt);
        int done = (int)Math.Round(progress.FadePositionSeconds / _dt);
        return Math.Max(0, total - done);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Types.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>A precomputed aligned seam with its exact crossfade motion (from the request tick to the fade's end).</summary>
    private sealed record Join(SeamPair Pair, double A, double B, float Separation, PlanarMotion Motion, double Settle, double Settled)
    {
        public string Out => Pair.Outgoing.Role;

        public string In => Pair.Incoming.Role;

        public double Crossfade => Pair.CrossfadeSeconds;
    }

    /// <summary>An aligned Idle time for one entry position.</summary>
    private readonly record struct IdleSlot(double A, float Separation);

    /// <summary>
    /// A seam out of a cycle (into an exit then Idle, or a lane switch), with its motion after whole extra cycles:
    /// <c>CyclesThen[n]</c> is <c>n</c> cycles from the seam time, then the seam, then (for a stop) the exit to Idle.
    /// </summary>
    private sealed record Leave(Join Join, Join? ToIdle, PlanarMotion[] CyclesThen, float Separation, double Duration);

    /// <summary>An Idle → manoeuvre → Idle block.</summary>
    private sealed record Manoeuvre(Join In, Join Out, int Cycles, PlanarMotion Motion, float Separation, double Duration, int Steps);

    /// <summary>Idle → entry → the next clip, ending once the next clip's crossfade has ended.</summary>
    private sealed record WalkPrefix(Join FromIdle, Join Next, PlanarMotion Motion, float Separation, double Duration);

    /// <summary>An entry → arc (whole extra cycles) → exit → Idle walk.</summary>
    private readonly record struct ArcStop(int Prefix, int Stop, int Cycles, PlanarMotion Motion, float Separation, double Duration);

    /// <summary>Bind-time arc lane-switch motions of one arc prefix (see <see cref="BuildArcWalks"/>).</summary>
    private sealed record ArcWalks(
        int Turn,
        double Length,
        PlanarMotion[][] SwitchMids,
        float[] SwitchSeparations,
        double[] SwitchDurations,
        int[] SwitchSettled);

    /// <summary>A whole slice-pair walk: Idle → entry head → exit tail → Idle.</summary>
    private sealed record SliceWalk(WalkPrefix Prefix, Join ToIdle, PlanarMotion Motion, float Separation, double Duration);

    /// <summary>A forward-walk tail from a settled cycle position: solo playback and cycles, then a stop to Idle.</summary>
    private readonly record struct Tail(int Stop, int Cycles, double Solo, PlanarMotion Motion, double Duration);

    /// <summary>
    /// A repeated-pivot continuation: Idle → entry → the restart seam <paramref name="Repeats"/> times (extra
    /// native loops placed before the first and after the last restart only) → Idle.
    /// <paramref name="UnitYaw"/> and <paramref name="UnitSeconds"/> are ONE restart unit's charge — the restart
    /// fade, the native solo back to the seam's outgoing time, and the unit's entry seek — the shared extra-native
    /// reserve pays per additional repeat.
    /// </summary>
    private readonly record struct PivotContinuation(Join Entry, Join Restart, Join Exit, int BeforeCycles,
        int AfterCycles, int Repeats, double UnitYaw, double UnitSeconds, PlanarMotion Motion, float Separation,
        double Duration, int Steps, double CycleYaw);

    /// <summary>One complete restart unit of a looping pivot's self seam (see <see cref="BuildRepeatedPivotFamily"/>).</summary>
    private readonly record struct RepeatedPivotUnit(Join Restart, double UnitYaw, double UnitSeconds, PlanarMotion SoloMotion);

    /// <summary>A looping pivot's repeated-continuation inputs: native loop charge, Idle entries/exits, complete units.</summary>
    private readonly record struct RepeatedPivotFamily(
        string Role, double CycleYaw, double CycleSeconds, Join[] Entries, Join[] Exits, RepeatedPivotUnit[] Units);

    private enum MainKind
    {
        None,
        Manoeuvre,
        PivotContinuation,
        Slice,
        Walk,
        ArcStop,
        ArcSwitch,
    }

    /// <summary>How to rebuild a candidate: indices into the menus.</summary>
    private readonly record struct Recipe(MainKind Main, int Index0 = -1, int Index1 = -1, int Cycles0 = 0)
    {
        public int Prefix { get; init; } = -1;

        public int Align { get; init; } = -1;

        public int Face { get; init; } = -1;

        /// <summary>Settled walk-cycle grid index a walk tail starts from.</summary>
        public int Settled { get; init; } = -1;

        /// <summary>Walk tail index at <see cref="Settled"/>.</summary>
        public int Tail { get; init; } = -1;

        public ArrivalMovementKind Kind
        {
            get; init;
        }
    }

    private readonly record struct Candidate(double Cost, int Steps, float Separation, double Duration, Recipe Recipe)
    {
        // Native cycles stay inside one pivot block. Only two distinct stationary blocks introduce an Idle bridge.
        public int StationaryIdleBridges => Recipe.Kind == ArrivalMovementKind.TurnOnly
            && Recipe.Main == MainKind.None && Recipe.Align >= 0 && Recipe.Face >= 0 ? 1 : 0;
    }

    /// <summary>A way out of the clip playing at a mid-clip start, ending in Idle.</summary>
    private sealed class StartPrefix
    {
        private StartPrefix(double exit, int exitCycles, ImmutableArray<(Join Join, double Exit, int Cycles)> segments, Join toIdle, PlanarMotion motion, double duration, bool continues, int steps)
        {
            Exit = exit;
            ExitCycles = exitCycles;
            Segments = segments;
            ToIdle = toIdle;
            Motion = motion;
            Duration = duration;
            Continues = continues;
            Steps = steps;
            Separation = Math.Max(toIdle.Separation, segments.IsEmpty ? 0f : segments.Max(segment => segment.Join.Separation));
        }

        public double Exit
        {
            get;
        }

        public int ExitCycles
        {
            get;
        }

        public ImmutableArray<(Join Join, double Exit, int Cycles)> Segments
        {
            get;
        }

        public Join ToIdle
        {
            get;
        }

        public PlanarMotion Motion
        {
            get;
        }

        public double Duration
        {
            get;
        }

        public bool Continues
        {
            get;
        }

        public int Steps
        {
            get;
        }

        public float Separation
        {
            get;
        }

        public static StartPrefix Direct(double exit, int exitCycles, PlanarMotion motion, Join toIdle, double elapsed)
            => new(exit, exitCycles, [], toIdle, motion, elapsed + toIdle.Settle, false, exitCycles);

        public static StartPrefix Through(double exit, int exitCycles, ImmutableArray<(Join Join, double Exit, int Cycles)> segments, Join toIdle, PlanarMotion motion, double duration, bool continues)
            => new(exit, exitCycles, segments, toIdle, motion, duration + toIdle.Settle, continues, segments.Sum(segment => 1 + segment.Cycles) + exitCycles);
    }

    /// <summary>Per-request search state: the target, the origin the Idle search runs from, and the best candidates.</summary>
    private sealed class Search(ArrivalPlannerConfig config, PlanarMotion target, bool stationaryOnly)
    {
        private readonly double _yawWeight = config.YawWeightMetresPerRadian;
        private readonly double _quantum = Math.Max(1e-9d, config.ResidualTieMetres);

        /// <summary>The target in the start frame.</summary>
        public PlanarMotion Target { get; } = target;

        /// <summary>The target in the origin frame of the current Idle search.</summary>
        public PlanarMotion LocalTarget
        {
            get; private set;
        } = target;

        public double LocalDistance => LocalTarget.Distance;

        public int PrefixIndex { get; private set; } = -1;

        public PlanarMotion PrefixMotion
        {
            get; private set;
        }

        public List<StartPrefix>? Prefixes
        {
            get; set;
        }

        public ArrivalMovementKind Kind
        {
            get; set;
        }

        private readonly List<PivotContinuation> _generatedPivotContinuations = [];

        /// <summary>
        /// Stores a search-generated continuation (the repeated-pivot family) and returns its index among the
        /// generated ones; recipe indices at or beyond the bind-time family's length resolve here (see
        /// <see cref="GeneratedPivotContinuation"/>).
        /// </summary>
        public int AddGeneratedPivotContinuation(PivotContinuation option)
        {
            _generatedPivotContinuations.Add(option);
            return _generatedPivotContinuations.Count - 1;
        }

        /// <summary>The search-generated continuation at <paramref name="index"/> (see <see cref="AddGeneratedPivotContinuation"/>).</summary>
        public PivotContinuation GeneratedPivotContinuation(int index) => _generatedPivotContinuations[index];

        private readonly Candidate[] _best = new Candidate[Math.Max(1, config.MaxFinalists)];
        private int _worst;

        /// <summary>The finalists, best first.</summary>
        public List<Candidate> Best
        {
            get
            {
                List<Candidate> sorted = [.. _best.AsSpan(0, Count)];
                sorted.Sort((left, right) => IsBetter(left, right) ? -1 : IsBetter(right, left) ? 1 : 0);
                return sorted;
            }
        }

        public int Count
        {
            get; private set;
        }

        public int Compositions
        {
            get; set;
        }

        public int SummaryPredictions
        {
            get; set;
        }

        public double EstimateError
        {
            get; set;
        }

        public double SearchMilliseconds
        {
            get; set;
        }

        public bool Exhausted
        {
            get; set;
        }

        public void Origin(int prefix, PlanarMotion motion)
        {
            PrefixIndex = prefix;
            PrefixMotion = motion;
            LocalTarget = Inverse(motion).Then(Target);
        }

        public double Cost(PlanarMotion reached)
        {
            double dx = Target.X - reached.X;
            double dy = Target.Y - reached.Y;
            return Math.Sqrt((dx * dx) + (dy * dy)) + (_yawWeight * Math.Abs(Mathf.Wrap(Target.Yaw - reached.Yaw, -Math.PI, Math.PI)));
        }

        public bool IsBetter(Candidate left, Candidate right)
        {
            long leftBucket = Bucket(left.Cost);
            long rightBucket = Bucket(right.Cost);
            return leftBucket != rightBucket
                ? leftBucket < rightBucket
                : stationaryOnly
                ? left.StationaryIdleBridges != right.StationaryIdleBridges
                    ? left.StationaryIdleBridges < right.StationaryIdleBridges
                    : left.Duration != right.Duration
                        ? left.Duration < right.Duration
                        : left.Steps != right.Steps
                            ? left.Steps < right.Steps
                            : left.Separation < right.Separation
                : left.Steps != right.Steps
                ? left.Steps < right.Steps
                : left.Separation != right.Separation
                    ? left.Separation < right.Separation
                    : left.Duration < right.Duration;
        }

        public void Offer(Candidate candidate)
        {
            if (Count < _best.Length)
            {
                _best[Count++] = candidate;
            }
            else if (IsBetter(candidate, _best[_worst]))
            {
                _best[_worst] = candidate;
            }
            else
            {
                return;
            }

            UpdateWorst();
        }

        /// <summary>
        /// Drops every finalist <paramref name="keep"/> rejects (terminal admissibility filtering, CORNER-08); the
        /// worst-finalist bookkeeping is recomputed for the survivors, so <see cref="Threshold"/> stays truthful.
        /// </summary>
        public void KeepOnly(Func<Candidate, bool> keep)
        {
            int write = 0;
            for (int read = 0; read < Count; read++)
            {
                if (keep(_best[read]))
                {
                    _best[write++] = _best[read];
                }
            }

            Count = write;
            UpdateWorst();
        }

        private void UpdateWorst()
        {
            _worst = 0;
            for (int index = 1; index < Count; index++)
            {
                if (IsBetter(_best[_worst], _best[index]))
                {
                    _worst = index;
                }
            }
        }

        public ArrivalPlanStatistics Statistics(long began)
            => new(Compositions, SummaryPredictions, Exhausted, EstimateError, SearchMilliseconds, Stopwatch.GetElapsedTime(began).TotalMilliseconds);

        public long Bucket(double cost) => (long)Math.Floor(cost / _quantum);

        /// <summary>
        /// The residual cost a new candidate must beat to enter the finalists (infinite until they are full), plus
        /// the tie quantum: a candidate whose distance alone misses by more cannot enter.
        /// </summary>
        public double Threshold => Count < _best.Length ? double.PositiveInfinity : _best[_worst].Cost + _quantum;

        private static PlanarMotion Inverse(PlanarMotion motion)
        {
            (double sin, double cos) = Math.SinCos(-motion.Yaw);
            return new PlanarMotion(-((cos * motion.X) - (sin * motion.Y)), -((sin * motion.X) + (cos * motion.Y)), -motion.Yaw);
        }
    }

    /// <summary>Lists a plan's segments and places each Idle seam at the earliest aligned Idle time the Idle reaches.</summary>
    private sealed class PlanBuilder(ArrivalPlanner planner, double startPosition, double startLead)
    {
        /// <summary>Playback from the open Idle's entry (or the start position) to <see cref="IdleFrom"/>.</summary>
        private double _idleLead = startLead;

        /// <summary>Index of the open Idle segment (its exit is set by the next block), or -1 for the start clip.</summary>
        private int _idle = -1;

        public List<ArrivalPlanSegment> Segments { get; } = [];

        /// <summary>Earliest time the open Idle may leave at.</summary>
        public double IdleFrom
        {
            get; set;
        }

        /// <summary>Exit of the start Idle, when the first block left it.</summary>
        public (double Exit, int Cycles)? StartIdleExit
        {
            get; private set;
        }

        private ArrivalStage _stage;

        public void AddPrefix(StartPrefix prefix)
        {
            foreach ((Join join, double exit, int cycles) in prefix.Segments)
            {
                _ = Add(join, exit, cycles, ArrivalStage.Prefix);
            }

            _stage = ArrivalStage.Prefix;
            _ = AddIdle(prefix.ToIdle);
        }

        /// <summary>Leaves the open Idle through its earliest aligned seam into the join's entry position.</summary>
        public bool OpenFromIdle(Join join, ArrivalStage stage)
        {
            _stage = stage;
            SeamClip idle = planner._idle;
            if (!planner._idleSlots.TryGetValue(join.In, out Dictionary<int, IdleSlot[]>? byEntry)
                || !byEntry.TryGetValue((int)Math.Round(join.B * join.Pair.Incoming.KeyRateHz), out IdleSlot[]? slots)
                || slots.Length == 0)
            {
                return false;
            }

            double from = idle.Normalise(IdleFrom);
            double Delay(IdleSlot candidate)
            {
                double value = candidate.A - from;
                return value < -TimeTolerance ? value + idle.DurationSeconds : Math.Max(0d, value);
            }
            IdleSlot[] reachable = [.. slots.Where(candidate => Delay(candidate) <= planner.Config.MaxIdleDwellSeconds + TimeTolerance)];
            if (reachable.Length == 0)
            {
                return false;
            }
            float minimum = reachable.Min(candidate => candidate.Separation);
            IdleSlot slot = reachable.Where(candidate => candidate.Separation <= minimum + 0.005f).MinBy(Delay);
            double delay = Delay(slot);

            double elapsed = _idleLead + Math.Max(0d, delay);
            if (_idle < 0)
            {
                StartIdleExit = (slot.A, planner.CyclesOf(idle.Role, startPosition, slot.A, elapsed));
            }
            else
            {
                ArrivalPlanSegment open = Segments[_idle];
                Segments[_idle] = open with
                {
                    ExitSeconds = slot.A,
                    ExitCycles = planner.CyclesOf(idle.Role, open.EntrySeconds, slot.A, elapsed)
                };
            }

            _pendingSeparation = slot.Separation;
            return true;
        }

        private float? _pendingSeparation;

        /// <summary>Adds the clip a join enters, leaving at <paramref name="exit"/> after <paramref name="cycles"/> extra cycles.</summary>
        public bool Add(Join into, double exit, int cycles, ArrivalStage stage)
        {
            Segments.Add(new ArrivalPlanSegment(
                into.In,
                into.Pair.Incoming.Kind,
                stage,
                into.B,
                into.Crossfade,
                exit,
                cycles,
                _pendingSeparation ?? into.Separation));
            _pendingSeparation = null;
            return true;
        }

        /// <summary>Adds the Idle a join enters and leaves it open.</summary>
        public bool AddIdle(Join into)
        {
            Segments.Add(new ArrivalPlanSegment(into.In, SeamClipRole.Idle, _stage, into.B, into.Crossfade, into.B, 0, into.Separation));
            _idle = Segments.Count - 1;
            IdleFrom = into.Settled;
            _idleLead = into.Settle;
            return true;
        }

        public bool AddManoeuvre(Manoeuvre manoeuvre, ArrivalStage stage)
            => OpenFromIdle(manoeuvre.In, stage)
               && Add(manoeuvre.In, manoeuvre.Out.A, manoeuvre.Cycles, stage)
               && AddIdle(manoeuvre.Out);

        public bool AddPivotContinuation(PivotContinuation option)
        {
            string role = option.Entry.In;
            double length = planner.Length(role);
            double before = option.Entry.Settle + planner.LoopBase(role, option.Entry.Settled, option.Restart.A)
                + (option.BeforeCycles * length);
            double middle = option.Restart.Settle + planner.LoopBase(role, option.Restart.Settled, option.Restart.A);
            double after = option.Restart.Settle + planner.LoopBase(role, option.Restart.Settled, option.Exit.A)
                + (option.AfterCycles * length);
            if (!OpenFromIdle(option.Entry, ArrivalStage.Face)
                || !Add(option.Entry, option.Restart.A,
                    planner.CyclesOf(role, option.Entry.B, option.Restart.A, before), ArrivalStage.Face))
            {
                return false;
            }

            // Each middle restart derives its own ExitCycles from its real fade/solo elapsed — a wrapped partial
            // plays with zero extra cycles; only an explicit extra loop adds one — never a copied count.
            for (int repeat = 1; repeat < option.Repeats; repeat++)
            {
                if (!Add(option.Restart, option.Restart.A,
                        planner.CyclesOf(role, option.Restart.B, option.Restart.A, middle), ArrivalStage.Face))
                {
                    return false;
                }
            }

            return Add(option.Restart, option.Exit.A,
                       planner.CyclesOf(role, option.Restart.B, option.Exit.A, after), ArrivalStage.Face)
                && AddIdle(option.Exit);
        }

        /// <summary>Ends the plan once the last Idle crossfade has ended.</summary>
        public void CloseIdle()
        {
            if (_idle < 0)
            {
                return;
            }

            ArrivalPlanSegment open = Segments[_idle];
            Segments[_idle] = open with
            {
                ExitSeconds = IdleFrom,
                ExitCycles = 0
            };
        }
    }
}
