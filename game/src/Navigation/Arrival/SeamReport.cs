using System.Globalization;
using System.Text;
using System.Text.Json;
using AlleyCat.Control.Locomotion;

namespace AlleyCat.Navigation.Arrival;

/// <summary>Whether the user has agreed a seam tolerance (NAV-001 Technical Requirement 7).</summary>
public enum SeamToleranceAgreementStatus
{
    /// <summary>The tolerance in force is provisional and awaits the user's agreement.</summary>
    Pending,

    /// <summary>The user agreed the tolerance in force.</summary>
    Agreed,
}

/// <summary>
/// The seam tolerance in force and its user-agreement record. The tolerance bounds the root-relative per-foot ball
/// separation over a crossfade window (pose agreement); it is not relaxed without measurement and user agreement.
/// Per character and animation variant it comes from a <see cref="SeamToleranceConfig"/>.
/// </summary>
/// <param name="ToleranceMetres">Largest accepted per-foot separation, in runtime metres.</param>
/// <param name="Status">Agreement status.</param>
/// <param name="Record">Who agreed, when, and on what evidence; or why agreement is pending.</param>
public sealed record SeamTolerance(float ToleranceMetres, SeamToleranceAgreementStatus Status, string Record)
{
    /// <summary>
    /// Strict recommendation (75 mm), a recommendation rather than an agreed tolerance. It was picked from the seam
    /// reports' candidate tolerance sweeps on both reference rigs as the smallest swept step (of 50, 75, 100 mm) that
    /// still admits right-lane forward travel (<c>i2f_r → wf → f2i_r</c>), the left-turn right-lane arc through a
    /// forward-cycle lane switch (<c>wf ↔ al_r</c>, <c>al_r → f2i_r</c>), and every manoeuvre ↔ Idle join; at 50 mm
    /// the male rig has no entry into any cycle. Reports flag every pair whose best seam exceeds it as an expected
    /// visible artefact.
    /// </summary>
    public const float StrictRecommendationMetres = 0.075f;

    /// <summary>Validates the tolerance and its agreement record.</summary>
    /// <exception cref="ArgumentException">
    /// The tolerance is not finite and positive, or an agreed tolerance carries no agreement record.
    /// </exception>
    public SeamTolerance Validate()
    {
        return !float.IsFinite(ToleranceMetres) || ToleranceMetres <= 0f
            ? throw new ArgumentException("Seam tolerance must be finite and positive.", nameof(ToleranceMetres))
            : Status == SeamToleranceAgreementStatus.Agreed && string.IsNullOrWhiteSpace(Record)
            ? throw new ArgumentException("An agreed seam tolerance must record the agreement.", nameof(Record))
            : this;
    }
}

/// <summary>
/// Measured planted-leg extension for a pair's best seam: the foot pinned where the outgoing clip had it at the
/// crossfade end and held until the incoming clip lifts it, against the incoming clip holding its own pin.
/// </summary>
/// <param name="OutgoingRole">Clip faded out.</param>
/// <param name="IncomingRole">Clip faded in.</param>
/// <param name="Foot">Planted foot.</param>
/// <param name="PeakUsage">Peak hip-to-ankle distance over the leg chain length with the seam's pin offset.</param>
/// <param name="BaselineUsage">The same without the seam's pin offset (the incoming clip's own pin).</param>
public readonly record struct SeamReachMeasurement(
    string OutgoingRole,
    string IncomingRole,
    StandingLocomotionAnatomicalSide Foot,
    float PeakUsage,
    float BaselineUsage);

/// <summary>Distribution of the per-window maximum separation among a pair's phase-agreeing seams.</summary>
/// <param name="Kind">Seam kind.</param>
/// <param name="OutgoingRole">Clip faded out.</param>
/// <param name="IncomingRole">Clip faded in.</param>
/// <param name="EvaluatedCount">Native-grid windows that fit both clips.</param>
/// <param name="PhaseAgreeingCount">Windows that satisfy phase agreement.</param>
/// <param name="SupportedCount">Phase-agreeing windows with a planted foot throughout.</param>
/// <param name="Minimum">Smallest maximum separation, in metres; <see langword="null"/> when none agree.</param>
/// <param name="Percentile10">10th percentile, in metres.</param>
/// <param name="Median">Median, in metres.</param>
/// <param name="Maximum">Largest maximum separation, in metres.</param>
/// <param name="Best">The seam with the smallest separation.</param>
/// <param name="MinimumOutgoingSeconds">Earliest outgoing time evaluated (the shortest slice head).</param>
/// <param name="CrossfadeSeconds">The pair's crossfade duration.</param>
public sealed record SeamPairStatistics(
    SeamKind Kind,
    string OutgoingRole,
    string IncomingRole,
    int EvaluatedCount,
    int PhaseAgreeingCount,
    int SupportedCount,
    float? Minimum,
    float? Percentile10,
    float? Median,
    float? Maximum,
    SeamCandidate? Best,
    double MinimumOutgoingSeconds,
    double CrossfadeSeconds);

/// <summary>
/// One aligned exit seam of a cyclic clip or looping confined manoeuvre: the best exit at one outgoing grid time.
/// </summary>
/// <param name="OutgoingTime">Cycle or loop playback position the exit starts at.</param>
/// <param name="ExitRole">Clip entered: an exit clip from a cycle, Idle from a looping manoeuvre.</param>
/// <param name="IncomingTime">Exit clip entry position.</param>
/// <param name="Separation">Maximum per-foot separation over the window, in metres.</param>
public readonly record struct SeamAlignedExit(double OutgoingTime, string ExitRole, double IncomingTime, float Separation);

/// <summary>
/// Aligned exits of one clip subject to cyclic clip availability (NAV-001 Technical Requirement 7) under one tolerance:
/// a cyclic clip, whose exits are its cycle → exit seams, or a looping confined manoeuvre, whose exits are its seams
/// into Idle (<see cref="SeamTable.AvailabilityExitKind"/>).
/// </summary>
/// <param name="CycleRole">Cyclic clip or looping manoeuvre.</param>
/// <param name="Kind">Planner role: <see cref="SeamClipRole.Cycle"/> or <see cref="SeamClipRole.Confined"/>.</param>
/// <param name="CycleSeconds">Cycle or loop duration.</param>
/// <param name="Exits">The best aligned exit at each outgoing grid time that has one, by outgoing time.</param>
/// <param name="LongestWaitSeconds">
/// Longest playback wait from any position until the next aligned exit, wrapping across cycles;
/// <see langword="null"/> when the clip has none.
/// </param>
public sealed record SeamCycleExits(
    string CycleRole,
    SeamClipRole Kind,
    double CycleSeconds,
    IReadOnlyList<SeamAlignedExit> Exits,
    double? LongestWaitSeconds)
{
    /// <summary>Whether the clip has at least one aligned exit seam per cycle or loop, and so is available.</summary>
    public bool IsAvailable => Exits.Count > 0;
}

/// <summary>What one candidate tolerance would make available.</summary>
/// <param name="ToleranceMetres">Candidate tolerance.</param>
/// <param name="AlignedPairsByKind">Per kind: pairs with at least one aligned seam, and all pairs of that kind.</param>
/// <param name="Cycles">Aligned exits of every cyclic clip and looping manoeuvre.</param>
public sealed record SeamToleranceSweep(
    float ToleranceMetres,
    IReadOnlyDictionary<SeamKind, (int Aligned, int Total)> AlignedPairsByKind,
    IReadOnlyList<SeamCycleExits> Cycles);

/// <summary>
/// Bind-time seam report of NAV-001 Technical Requirement 7: the seam tolerance in force and its agreement record,
/// the measured seam-separation data, the aligned exits of each cyclic clip and looping confined manoeuvre, the
/// unavailable ones, and the pairs and seam kinds that the pairs' crossfade durations make impossible. It warns about
/// pairs whose best seam exceeds the strict recommendation (an expected visible artefact) or needs a planted-leg
/// extension above <see cref="ReachUsageWarningThreshold"/> (a reach risk), and sweeps candidate tolerances.
/// </summary>
public sealed class SeamReport
{
    /// <summary>
    /// Planted-leg usage above which a seam's pin offset is a reach risk: the peak extension measured on unstretched
    /// clips.
    /// </summary>
    public const float ReachUsageWarningThreshold = 0.994f;

    private SeamReport(
        string rig,
        double crossfadeSeconds,
        double entryLagSeconds,
        SeamTolerance tolerance,
        IReadOnlyList<SeamPairStatistics> pairs,
        IReadOnlyList<SeamCycleExits> cycles,
        IReadOnlyList<SeamPairStatistics> impossiblePairs,
        IReadOnlyList<SeamKind> impossibleKinds,
        IReadOnlyList<SeamToleranceSweep> sweep,
        IReadOnlyList<SeamReachMeasurement> reachRisks,
        IReadOnlyList<string> plannerSetClips,
        IReadOnlyList<SeamClipExclusion> excludedClips,
        IReadOnlyList<SeamPairExclusion> excludedPairs)
    {
        PlannerSetClips = plannerSetClips;
        ExcludedClips = excludedClips;
        ExcludedPairs = excludedPairs;
        Rig = rig;
        CrossfadeSeconds = crossfadeSeconds;
        EntryLagSeconds = entryLagSeconds;
        Tolerance = tolerance;
        Pairs = pairs;
        Cycles = cycles;
        ImpossiblePairs = impossiblePairs;
        ImpossibleKinds = impossibleKinds;
        Sweep = sweep;
        ReachRisks = reachRisks;
    }

    /// <summary>Rig the table was bound to.</summary>
    public string Rig
    {
        get;
    }

    /// <summary>Base crossfade duration: every pair's except those listed in <see cref="ShortenedCrossfades"/>.</summary>
    public double CrossfadeSeconds
    {
        get;
    }

    /// <summary>Entry lag the seams were evaluated with (the runtime physics delta).</summary>
    public double EntryLagSeconds
    {
        get;
    }

    /// <summary>Pairs whose crossfade the crossfade policy shortened below the base duration.</summary>
    public IEnumerable<SeamPairStatistics> ShortenedCrossfades
        => Pairs.Where(pair => pair.CrossfadeSeconds < CrossfadeSeconds - 1e-9d);

    /// <summary>
    /// The limiting pair: the planner-set pair whose best usable seam has the largest separation, which bounds the
    /// smallest tolerance admitting every pair; <see langword="null"/> when no pair has a usable seam.
    /// </summary>
    public SeamPairStatistics? LimitingPair
        => Pairs.Where(pair => pair.Minimum is not null).MaxBy(pair => pair.Minimum);

    /// <summary>Seam tolerance in force and its agreement record.</summary>
    public SeamTolerance Tolerance
    {
        get;
    }

    /// <summary>Measured seam-separation data per pair.</summary>
    public IReadOnlyList<SeamPairStatistics> Pairs
    {
        get;
    }

    /// <summary>Aligned exits of each cyclic clip and looping confined manoeuvre under the tolerance in force.</summary>
    public IReadOnlyList<SeamCycleExits> Cycles
    {
        get;
    }

    /// <summary>Clips in the planner set (some analysed pair includes them), by role.</summary>
    public IReadOnlyList<string> PlannerSetClips
    {
        get;
    }

    /// <summary>
    /// Clips outside the planner set, with reasons: excluded from planning, distinct from unavailable, and not defects.
    /// </summary>
    public IReadOnlyList<SeamClipExclusion> ExcludedClips
    {
        get;
    }

    /// <summary>Candidate pairs outside the planner set, with reasons; never analysed.</summary>
    public IReadOnlyList<SeamPairExclusion> ExcludedPairs
    {
        get;
    }

    /// <summary>
    /// Planner-set cyclic clips and looping confined manoeuvres with no aligned exit seam per cycle or loop; the
    /// planner never enters them. Excluded clips are never listed here.
    /// </summary>
    public IEnumerable<string> UnavailableCycles => Cycles.Where(cycle => !cycle.IsAvailable).Select(cycle => cycle.CycleRole);

    /// <summary>Pairs with no phase-agreeing window of the crossfade duration.</summary>
    public IReadOnlyList<SeamPairStatistics> ImpossiblePairs
    {
        get;
    }

    /// <summary>Seam kinds whose every pair is impossible under the crossfade duration.</summary>
    public IReadOnlyList<SeamKind> ImpossibleKinds
    {
        get;
    }

    /// <summary>Shortest slice head per slice-join outgoing clip, in source seconds.</summary>
    public IEnumerable<KeyValuePair<string, double>> SliceHeads
        => Pairs.Where(pair => pair.Kind == SeamKind.SliceJoin)
            .GroupBy(pair => pair.OutgoingRole, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => KeyValuePair.Create(group.Key, group.Max(pair => pair.MinimumOutgoingSeconds)));

    /// <summary>Pairs with no aligned seam under the tolerance in force.</summary>
    public IEnumerable<SeamPairStatistics> UnalignedPairs
        => Pairs.Where(pair => pair.Minimum is not { } minimum || minimum > Tolerance.ToleranceMetres);

    /// <summary>
    /// Expected visible artefacts: pairs whose best seam separation exceeds
    /// <see cref="SeamTolerance.StrictRecommendationMetres"/>.
    /// </summary>
    public IEnumerable<SeamPairStatistics> ArtefactWarnings
        => Pairs.Where(pair => pair.Minimum is { } minimum && minimum > SeamTolerance.StrictRecommendationMetres);

    /// <summary>
    /// Reach risks: the most extended planted foot of each pair whose best seam needs usage above
    /// <see cref="ReachUsageWarningThreshold"/>.
    /// </summary>
    public IReadOnlyList<SeamReachMeasurement> ReachRisks
    {
        get;
    }

    /// <summary>Availability under each candidate tolerance.</summary>
    public IReadOnlyList<SeamToleranceSweep> Sweep
    {
        get;
    }

    /// <summary>Creates the report of a table under the tolerance in force.</summary>
    /// <param name="rig">Rig label.</param>
    /// <param name="table">The bound seam table.</param>
    /// <param name="tolerance">Tolerance in force.</param>
    /// <param name="candidateTolerances">Candidate tolerances to sweep, in metres.</param>
    /// <param name="reach">Planted-leg reach measured for the pairs' best seams, if available.</param>
    public static SeamReport Create(
        string rig,
        SeamTable table,
        SeamTolerance tolerance,
        IEnumerable<float>? candidateTolerances = null,
        IEnumerable<SeamReachMeasurement>? reach = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rig);
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(tolerance);
        _ = tolerance.Validate();
        SeamReachMeasurement[] reachRisks =
        [
            .. (reach ?? [])
                .Where(value => value.PeakUsage > ReachUsageWarningThreshold)
                .GroupBy(value => (value.OutgoingRole, value.IncomingRole))
                .Select(group => group.MaxBy(value => value.PeakUsage))
                .OrderByDescending(value => value.PeakUsage),
        ];

        SeamPairStatistics[] pairs = [.. table.Pairs.Select(Summarise)];
        SeamKind[] kinds = [.. table.Pairs.Select(pair => pair.Kind).Distinct().Order()];
        SeamKind[] impossibleKinds =
            [.. kinds.Where(kind => table.Pairs.Where(pair => pair.Kind == kind).All(pair => pair.IsImpossible))];
        SeamToleranceSweep[] sweep =
            [.. (candidateTolerances ?? []).Order().Select(value => CreateSweep(table, value, kinds))];
        return new SeamReport(
            rig,
            table.BaseCrossfadeSeconds,
            table.EntryLagSeconds,
            tolerance,
            pairs,
            CycleExits(table, tolerance.ToleranceMetres),
            [.. pairs.Where(pair => pair.PhaseAgreeingCount == 0)],
            impossibleKinds,
            sweep,
            reachRisks,
            [.. table.Clips.Keys.Where(table.IsInPlannerSet).Order(StringComparer.Ordinal)],
            table.ExcludedClips,
            table.ExcludedPairs);
    }

    /// <summary>Planner-set cyclic clips and looping confined manoeuvres, by role.</summary>
    private static IEnumerable<SeamClip> PlannedCycles(SeamTable table)
        => table.Clips.Values
            .Where(clip => SeamTable.AvailabilityExitKind(clip) is not null && table.IsInPlannerSet(clip.Role))
            .OrderBy(clip => clip.Role, StringComparer.Ordinal);

    /// <summary>
    /// Aligned exits of every planned cyclic clip and looping confined manoeuvre in the table under a tolerance.
    /// </summary>
    public static IReadOnlyList<SeamCycleExits> CycleExits(SeamTable table, float toleranceMetres)
    {
        ArgumentNullException.ThrowIfNull(table);
        List<SeamCycleExits> result = [];
        foreach (SeamClip cycle in PlannedCycles(table))
        {
            SortedDictionary<double, SeamAlignedExit> best = [];
            SeamKind exitKind = SeamTable.AvailabilityExitKind(cycle)!.Value;
            foreach (SeamPair pair in table.Pairs.Where(pair => pair.Kind == exitKind && pair.Outgoing == cycle))
            {
                foreach (SeamCandidate candidate in pair.UsableCandidates.Where(value => value.IsAlignedWithin(toleranceMetres)))
                {
                    if (!best.TryGetValue(candidate.OutgoingTime, out SeamAlignedExit current)
                        || candidate.MaxSeparation < current.Separation)
                    {
                        best[candidate.OutgoingTime] = new SeamAlignedExit(
                            candidate.OutgoingTime,
                            pair.Incoming.Role,
                            candidate.IncomingTime,
                            candidate.MaxSeparation);
                    }
                }
            }

            SeamAlignedExit[] exits = [.. best.Values];
            result.Add(new SeamCycleExits(cycle.Role, cycle.Kind, cycle.DurationSeconds, exits, LongestWait(exits, cycle.DurationSeconds)));
        }

        return result;
    }

    /// <summary>Plain-text dump for evidence and logs.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        _ = text.AppendLine(Invariant($"Seam report: rig={Rig} base_crossfade={CrossfadeSeconds * 1000d:0.#} ms entry_lag={EntryLagSeconds * 1000d:0.###} ms (seam (a, b): request at outgoing a, enter incoming at b one tick later; blend window d - lag, outgoing from a + lag)"));
        string limiting = LimitingPair is { } worst
            ? Invariant($"{PairLabel(worst)} ({worst.Kind}) best {Mm(worst.Minimum)} mm")
            : "none";
        _ = text.AppendLine(Invariant($"Headline: limiting pair {limiting}; unavailable cyclic clips and looping manoeuvres: {Join(UnavailableCycles)}; impossible pairs: {Join(ImpossiblePairs.Select(PairLabel))}; shortened crossfades: {Join(ShortenedCrossfades.Select(pair => Invariant($"{PairLabel(pair)}={pair.CrossfadeSeconds * 1000d:0.#} ms")))}"));
        _ = text.AppendLine(Invariant($"Seam tolerance in force: {Mm(Tolerance.ToleranceMetres)} mm ({Tolerance.Status}): {Tolerance.Record}"));
        _ = text.AppendLine(Invariant($"Strict recommendation (not agreed; the smallest swept step that admits right-lane forward travel, the wf↔al_r arc switch and every Idle join on both reference rigs, per their candidate tolerance sweeps): {Mm(SeamTolerance.StrictRecommendationMetres)} mm; reach warning above planted-leg usage {ReachUsageWarningThreshold:0.000}."));
        _ = text.AppendLine(Invariant($"Planner set clips: {Join(PlannerSetClips)}"));
        _ = text.AppendLine(Invariant($"Excluded clips (not planned; distinct from unavailable): {Join(ExcludedClips.Select(clip => $"{clip.Role} ({clip.Reason})"))}"));
        _ = text.AppendLine(Invariant($"Excluded pairs (not planned): {Join(ExcludedPairs.Select(pair => $"{pair.Pair.OutgoingRole}->{pair.Pair.IncomingRole} ({pair.Reason})"))}"));
        _ = text.AppendLine("Separation: largest root-relative per-foot ball separation over the whole window, mm.");
        _ = text.AppendLine();
        _ = text.AppendLine("Measured seam-separation data of the planner set (min..max over usable seams: phase-agreeing, and for slice joins with a foot planted throughout):");
        _ = text.AppendLine("kind            outgoing -> incoming    xfade_ms evaluated agreeing supported    min    p10 median    max  best(a,b) L/R");
        foreach (SeamPairStatistics pair in Pairs)
        {
            string best = pair.Best is { } seam
                ? Invariant($"({seam.OutgoingTime:0.000},{seam.IncomingTime:0.000}) {Mm(seam.LeftSeparation)}/{Mm(seam.RightSeparation)}")
                : "-";
            _ = text.AppendLine(Invariant(
                $"{pair.Kind,-15} {pair.OutgoingRole + " -> " + pair.IncomingRole,-22} {pair.CrossfadeSeconds * 1000d,8:0.#} {pair.EvaluatedCount,9} {pair.PhaseAgreeingCount,8} {pair.SupportedCount,9} {Mm(pair.Minimum),6} {Mm(pair.Percentile10),6} {Mm(pair.Median),6} {Mm(pair.Maximum),6}  {best}"));
        }

        _ = text.AppendLine();
        _ = text.AppendLine(Invariant($"Aligned exits per cyclic clip and looping manoeuvre at {Mm(Tolerance.ToleranceMetres)} mm (a looping manoeuvre exits into Idle):"));
        foreach (SeamCycleExits cycle in Cycles)
        {
            string exits = string.Join(
                ' ',
                cycle.Exits.Select(exit => Invariant($"{exit.OutgoingTime:0.000}->{exit.ExitRole}@{exit.IncomingTime:0.000}({Mm(exit.Separation)})")));
            _ = text.AppendLine(Invariant(
                $"  {cycle.CycleRole,-6} {cycle.Kind,-8} cycle={cycle.CycleSeconds:0.000}s aligned_exit_times={cycle.Exits.Count} longest_wait={(cycle.LongestWaitSeconds is { } wait ? wait.ToString("0.000", CultureInfo.InvariantCulture) + "s" : "-")} {exits}"));
        }

        _ = text.AppendLine(Invariant($"Unavailable cyclic clips and looping manoeuvres: {Join(UnavailableCycles)}"));
        _ = text.AppendLine(Invariant($"Slice joins' shortest head: {Join(SliceHeads.Select(head => Invariant($"{head.Key}>={head.Value:0.000}s")))}"));
        _ = text.AppendLine(Invariant($"Pairs impossible under the crossfade duration: {Join(ImpossiblePairs.Select(pair => $"{pair.OutgoingRole}->{pair.IncomingRole}"))}"));
        _ = text.AppendLine(Invariant($"Seam kinds impossible under the crossfade duration: {Join(ImpossibleKinds.Select(kind => kind.ToString()))}"));
        _ = text.AppendLine(Invariant($"Pairs with no aligned seam under the tolerance in force: {Join(UnalignedPairs.Select(PairLabel))}"));

        _ = text.AppendLine();
        _ = text.AppendLine(Invariant($"Warnings: expected visible artefact (best seam above {Mm(SeamTolerance.StrictRecommendationMetres)} mm):"));
        foreach (SeamPairStatistics pair in ArtefactWarnings.OrderByDescending(pair => pair.Minimum))
        {
            _ = text.AppendLine(Invariant($"  {pair.Kind,-15} {PairLabel(pair),-22} best {Mm(pair.Minimum)} mm"));
        }

        _ = text.AppendLine(Invariant($"Warnings: reach risk (best seam needs planted-leg usage above {ReachUsageWarningThreshold:0.000}):"));
        foreach (SeamReachMeasurement risk in ReachRisks)
        {
            _ = text.AppendLine(Invariant(
                $"  {risk.OutgoingRole + "->" + risk.IncomingRole,-22} foot={risk.Foot,-5} usage {risk.PeakUsage:0.0000} (own pin {risk.BaselineUsage:0.0000})"));
        }

        if (Sweep.Count > 0)
        {
            _ = text.AppendLine();
            _ = text.AppendLine("Candidate tolerance sweep (pairs with an aligned seam / pairs; cyclic clips and looping manoeuvres available):");
            foreach (SeamToleranceSweep row in Sweep)
            {
                string kinds = string.Join(
                    ' ',
                    row.AlignedPairsByKind.OrderBy(entry => entry.Key).Select(entry => Invariant($"{entry.Key}={entry.Value.Aligned}/{entry.Value.Total}")));
                string cycles = string.Join(
                    ' ',
                    row.Cycles.Select(cycle => Invariant($"{cycle.CycleRole}:{cycle.Exits.Count}")));
                _ = text.AppendLine(Invariant(
                    $"  {Mm(row.ToleranceMetres),5} mm  {kinds}  cycles_and_loops_available={row.Cycles.Count(cycle => cycle.IsAvailable)}/{row.Cycles.Count} exit_times[{cycles}] unavailable=[{Join(row.Cycles.Where(cycle => !cycle.IsAvailable).Select(cycle => cycle.CycleRole))}]"));
            }
        }

        return text.ToString();
    }

    /// <summary>Structured JSON dump.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("rig", Rig);
            json.WriteNumber("crossfade_seconds", CrossfadeSeconds);
            json.WriteNumber("entry_lag_seconds", EntryLagSeconds);
            if (LimitingPair is { } limitingPair)
            {
                json.WriteStartObject("limiting_pair");
                json.WriteString("outgoing", limitingPair.OutgoingRole);
                json.WriteString("incoming", limitingPair.IncomingRole);
                WriteOptional(json, "best_m", limitingPair.Minimum);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("limiting_pair");
            }
            json.WriteStartObject("seam_tolerance");
            json.WriteNumber("tolerance_m", Tolerance.ToleranceMetres);
            json.WriteString("agreement_status", Tolerance.Status.ToString());
            json.WriteString("agreement_record", Tolerance.Record);
            json.WriteNumber("strict_recommendation_m", SeamTolerance.StrictRecommendationMetres);
            json.WriteEndObject();

            json.WriteStartArray("measured_pairs");
            foreach (SeamPairStatistics pair in Pairs)
            {
                json.WriteStartObject();
                json.WriteString("kind", pair.Kind.ToString());
                json.WriteString("outgoing", pair.OutgoingRole);
                json.WriteString("incoming", pair.IncomingRole);
                json.WriteNumber("crossfade_seconds", pair.CrossfadeSeconds);
                json.WriteNumber("evaluated", pair.EvaluatedCount);
                json.WriteNumber("phase_agreeing", pair.PhaseAgreeingCount);
                json.WriteNumber("supported", pair.SupportedCount);
                WriteOptional(json, "min_m", pair.Minimum);
                WriteOptional(json, "p10_m", pair.Percentile10);
                WriteOptional(json, "median_m", pair.Median);
                WriteOptional(json, "max_m", pair.Maximum);
                if (pair.Best is { } best)
                {
                    json.WriteStartObject("best");
                    WriteCandidate(json, best);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("cyclic_clips_and_looping_manoeuvres");
            foreach (SeamCycleExits cycle in Cycles)
            {
                WriteCycle(json, cycle, includeExits: true);
            }

            json.WriteEndArray();
            WriteStrings(json, "unavailable_cyclic_clips_and_looping_manoeuvres", UnavailableCycles);
            WriteStrings(json, "planner_set_clips", PlannerSetClips);
            json.WriteStartArray("excluded_clips");
            foreach (SeamClipExclusion clip in ExcludedClips)
            {
                json.WriteStartObject();
                json.WriteString("role", clip.Role);
                json.WriteString("reason", clip.Reason.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("excluded_pairs");
            foreach (SeamPairExclusion pair in ExcludedPairs)
            {
                json.WriteStartObject();
                json.WriteString("kind", pair.Pair.Kind.ToString());
                json.WriteString("outgoing", pair.Pair.OutgoingRole);
                json.WriteString("incoming", pair.Pair.IncomingRole);
                json.WriteString("reason", pair.Reason.ToString());
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartObject("slice_minimum_head_seconds");
            foreach (KeyValuePair<string, double> head in SliceHeads)
            {
                json.WriteNumber(head.Key, head.Value);
            }

            json.WriteEndObject();
            WriteStrings(json, "impossible_pairs", ImpossiblePairs.Select(pair => $"{pair.OutgoingRole}->{pair.IncomingRole}"));
            WriteStrings(json, "impossible_kinds", ImpossibleKinds.Select(kind => kind.ToString()));
            WriteStrings(json, "unaligned_pairs", UnalignedPairs.Select(PairLabel));
            WriteStrings(json, "artefact_warnings", ArtefactWarnings.Select(PairLabel));
            json.WriteNumber("reach_usage_warning_threshold", ReachUsageWarningThreshold);
            json.WriteStartArray("reach_risks");
            foreach (SeamReachMeasurement risk in ReachRisks)
            {
                json.WriteStartObject();
                json.WriteString("outgoing", risk.OutgoingRole);
                json.WriteString("incoming", risk.IncomingRole);
                json.WriteString("foot", risk.Foot.ToString());
                json.WriteNumber("peak_usage", risk.PeakUsage);
                json.WriteNumber("baseline_usage", risk.BaselineUsage);
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("tolerance_sweep");
            foreach (SeamToleranceSweep row in Sweep)
            {
                json.WriteStartObject();
                json.WriteNumber("tolerance_m", row.ToleranceMetres);
                json.WriteStartObject("aligned_pairs_by_kind");
                foreach (KeyValuePair<SeamKind, (int Aligned, int Total)> entry in row.AlignedPairsByKind.OrderBy(entry => entry.Key))
                {
                    json.WriteStartObject(entry.Key.ToString());
                    json.WriteNumber("aligned", entry.Value.Aligned);
                    json.WriteNumber("total", entry.Value.Total);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
                json.WriteStartArray("cyclic_clips_and_looping_manoeuvres");
                foreach (SeamCycleExits cycle in row.Cycles)
                {
                    WriteCycle(json, cycle, includeExits: false);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SeamPairStatistics Summarise(SeamPair pair)
    {
        float[] separations = [.. pair.UsableCandidates.Select(candidate => candidate.MaxSeparation).Order()];
        return new SeamPairStatistics(
            pair.Kind,
            pair.Outgoing.Role,
            pair.Incoming.Role,
            pair.EvaluatedCount,
            pair.Candidates.Length,
            pair.Candidates.Count(candidate => candidate.HasSupportThroughout),
            separations.Length > 0 ? separations[0] : null,
            Percentile(separations, 0.1d),
            Percentile(separations, 0.5d),
            separations.Length > 0 ? separations[^1] : null,
            pair.Best,
            pair.MinimumOutgoingSeconds,
            pair.CrossfadeSeconds);
    }

    /// <summary>Nearest-rank percentile of sorted values.</summary>
    private static float? Percentile(float[] sorted, double fraction)
        => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static SeamToleranceSweep CreateSweep(SeamTable table, float tolerance, SeamKind[] kinds)
    {
        Dictionary<SeamKind, (int Aligned, int Total)> byKind = [];
        foreach (SeamKind kind in kinds)
        {
            SeamPair[] ofKind = [.. table.Pairs.Where(pair => pair.Kind == kind)];
            byKind[kind] = (
                ofKind.Count(pair => pair.UsableCandidates.Any(candidate => candidate.IsAlignedWithin(tolerance))),
                ofKind.Length);
        }

        return new SeamToleranceSweep(tolerance, byKind, CycleExits(table, tolerance));
    }

    private static double? LongestWait(SeamAlignedExit[] exits, double cycle)
    {
        if (exits.Length == 0)
        {
            return null;
        }

        double longest = exits[0].OutgoingTime + cycle - exits[^1].OutgoingTime;
        for (int index = 1; index < exits.Length; index++)
        {
            longest = Math.Max(longest, exits[index].OutgoingTime - exits[index - 1].OutgoingTime);
        }

        return longest;
    }

    private static void WriteCycle(Utf8JsonWriter json, SeamCycleExits cycle, bool includeExits)
    {
        json.WriteStartObject();
        json.WriteString("cycle", cycle.CycleRole);
        json.WriteString("kind", cycle.Kind.ToString());
        json.WriteNumber("cycle_seconds", cycle.CycleSeconds);
        json.WriteBoolean("available", cycle.IsAvailable);
        json.WriteNumber("aligned_exit_times", cycle.Exits.Count);
        WriteOptional(json, "longest_wait_seconds", cycle.LongestWaitSeconds);
        if (includeExits)
        {
            json.WriteStartArray("aligned_exits");
            foreach (SeamAlignedExit exit in cycle.Exits)
            {
                json.WriteStartObject();
                json.WriteNumber("a", exit.OutgoingTime);
                json.WriteString("exit", exit.ExitRole);
                json.WriteNumber("b", exit.IncomingTime);
                json.WriteNumber("separation_m", exit.Separation);
                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        json.WriteEndObject();
    }

    private static void WriteCandidate(Utf8JsonWriter json, SeamCandidate candidate)
    {
        json.WriteNumber("a", candidate.OutgoingTime);
        json.WriteNumber("b", candidate.IncomingTime);
        json.WriteNumber("left_m", candidate.LeftSeparation);
        json.WriteNumber("right_m", candidate.RightSeparation);
        json.WriteBoolean("support_throughout", candidate.HasSupportThroughout);
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, float? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteOptional(Utf8JsonWriter json, string name, double? value)
    {
        if (value is { } number)
        {
            json.WriteNumber(name, number);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    private static void WriteStrings(Utf8JsonWriter json, string name, IEnumerable<string> values)
    {
        json.WriteStartArray(name);
        foreach (string value in values)
        {
            json.WriteStringValue(value);
        }

        json.WriteEndArray();
    }

    private static string PairLabel(SeamPairStatistics pair) => $"{pair.OutgoingRole}->{pair.IncomingRole}";

    private static string Mm(float? metres)
        => metres is { } value ? (value * 1000f).ToString("0.0", CultureInfo.InvariantCulture) : "-";

    private static string Join(IEnumerable<string> values)
    {
        string joined = string.Join(", ", values);
        return joined.Length == 0 ? "none" : joined;
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);
}
