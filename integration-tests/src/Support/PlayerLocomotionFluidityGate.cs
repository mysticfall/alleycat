using System.Globalization;
using System.Text;
using AlleyCat.Control.Locomotion;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// One recorded physics-tick observation of the player locomotion harness: the sampled foot and body world
/// positions, the animation graph node reported by the state machine playback, the elapsed bout time, and the
/// controller-owned response state recorded by scripted scenario runs. The optional fields default to null so
/// hand-built contact-oracle samples remain source-compatible.
/// </summary>
public sealed record FluiditySample(
    float TimeSeconds,
    string GraphNode,
    Vector3 LeftFootWorld,
    Vector3 RightFootWorld,
    Vector3 BodyWorld,
    FluidityContactFrame? Contacts = null,
    Vector2? ResponseVelocity = null,
    float? ResponseYawRate = null,
    Vector2? ActorPlanarVelocity = null,
    float? ActorYawRadians = null,
    Vector2 OrdinaryTravelDemand = default,
    PlayerLocomotionState? PlayerState = null,
    ulong MovementOperationCount = 0,
    Vector3 AuthoredRootDelta = default,
    Vector3 RequestedMovement = default,
    Vector3 RealisedMovement = default);

/// <summary>
/// One animation graph node segment observed during a bout. <see cref="ExitTimeSeconds"/> of the final segment is
/// the last sample time, so every window anchored on a segment is evaluated on a closed interval.
/// </summary>
public sealed record FluidityNodeSegment(string NodeName, float EnterTimeSeconds, float ExitTimeSeconds);

/// <summary>
/// An accepted crossfade-seam window whose samples are excluded from the per-sample fluidity metrics. CTRL-001
/// TR4 accepts a brief bounded idle→entry crossfade slide as presentation seam-smoothing, so the default
/// calibration excludes the first fraction of a second after the graph enters a <c>PlayerI2*</c> node. A node
/// change away from the matched node ends the window early even when the full duration has not elapsed.
/// </summary>
public sealed record FluidityExclusionWindow(string NodeNamePrefix, float DurationSeconds);

/// <summary>
/// Calibrated thresholds for the player locomotion fluidity gate. Every default cites the control-case
/// measurement it was calibrated from; the calibration artefacts live under game/temp/ctrl001-fluidity-gate
/// (regenerate with PlayerLocomotionFluidityGateCalibrationTests).
/// </summary>
/// <param name="StanceContactOffsetMetres">Foot-bone height above the tracked ground below which the foot counts as planted.</param>
/// <param name="StanceMaximumVerticalSpeed">Vertical speed below which a low foot counts as planted rather than lifting or landing.</param>
/// <param name="GroundTrackWindowSeconds">Rolling window over which the lower foot's minimum height is tracked as the ground level.</param>
/// <param name="MinimumStanceSamples">Shortest stance run (in ticks) that is measured; shorter runs are heel-strike transients.</param>
/// <param name="MinimumSwingSamples">Shortest swing run (in ticks) that counts as a step onset; shorter runs are heel-strike stance chatter.</param>
/// <param name="MaximumStanceSlipSpeed">Planar speed a planted foot may reach before the sample is a skating violation (m/s).</param>
/// <param name="MaximumStanceSlipDistance">Planar distance a planted foot may accumulate over one stance interval before it is a violation (m).</param>
/// <param name="MaximumSwingPlanarSpeed">Planar speed cap for any foot at any time: clean sustained walk maximum plus headroom (m/s).</param>
/// <param name="MaximumSpeedDiscontinuity">Frame-to-frame planar speed change cap per foot (m/s²).</param>
/// <param name="TravelPhaseWarmupSeconds">Grace after entering PlayerTravel before step-phase metrics are enforced.</param>
/// <param name="MinimumTravelStepIntervalSeconds">Lower bound for a same-foot step interval during sustained travel; a shorter interval is a hitch.</param>
/// <param name="MaximumTravelStepIntervalSeconds">Upper bound for a same-foot step interval during sustained travel; a longer interval is a hesitation.</param>
/// <param name="ExclusionWindows">Accepted crossfade-seam windows excluded from per-sample metrics.</param>
public sealed record FluidityGateThresholds(
    float StanceContactOffsetMetres,
    float StanceMaximumVerticalSpeed,
    float GroundTrackWindowSeconds,
    int MinimumStanceSamples,
    int MinimumSwingSamples,
    float MaximumStanceSlipSpeed,
    float MaximumStanceSlipDistance,
    float MaximumSwingPlanarSpeed,
    float MaximumSpeedDiscontinuity,
    float TravelPhaseWarmupSeconds,
    float MinimumTravelStepIntervalSeconds,
    float MaximumTravelStepIntervalSeconds,
    IReadOnlyList<FluidityExclusionWindow> ExclusionWindows)
{
    /// <summary>
    /// Thresholds calibrated from the recorded control cases (clean i2f→wf hold at full forward stick on the real
    /// player scene, 60 Hz synchronous physics ticks; artefacts 2026-08-18, game/temp/ctrl001-fluidity-gate).
    /// Each default quotes the measured clean maximum it must clear; the seam defects the gate exists to catch
    /// measure well above these bounds (see the scenario artefacts recorded in the same run).
    /// </summary>
    public static FluidityGateThresholds CalibratedDefaults
    {
        get;
    } = new(
        // Measured: planted feet hold within ~0.012 m of the tracked ground for whole stance intervals while
        // swing feet rise 0.15 m and more; 0.02 m separates the populations with margin (spec guidance
        // 0.015-0.02 m).
        StanceContactOffsetMetres: 0.02f,
        // Measured: stance ankle oscillation stays below ~0.09 m/s vertically while swing liftoff exceeds
        // 0.25 m/s. Toe-off micro-oscillation rides the 0.10 boundary (±0.12 m/s flicker), so the raw
        // vertical-speed test flickers there — the min-stance-run merge below the detector absorbs that
        // flicker instead of loosening this boundary (spec guidance ~0.10 m/s).
        StanceMaximumVerticalSpeed: 0.10f,
        // Half the 1.0667 s walk cycle guarantees the window always covers at least one planted foot.
        GroundTrackWindowSeconds: 0.5333f,
        // Measured: heel-strike and toe-off flag transients resolve within 2 ticks; 3 ticks (50 ms) is the
        // shortest honest stance. Sub-minimum runs are merged into swing before measurement (see
        // MergeShortStanceRunsIntoSwing).
        MinimumStanceSamples: 3,
        // Measured on control-wf-hold: chatter-produced swing runs span 1-7 ticks while every true travel
        // swing runs 31+ ticks; 10 ticks (0.167 s) sits between the populations and keeps residual
        // double-onset artefacts out of the phase metrics.
        MinimumSwingSamples: 10,
        // Measured across settled calibration runs, before and after the CTRL-001 travel-exit release gate:
        // clean walk stance (ankle roll over the toe while planted) stays at or below 0.767 m/s, and the
        // release-gated travel exits drag the planted foot through the authored *2i stitch-anchor pose gap at
        // 1.071 m/s in unloaded runs, reaching 1.150 m/s under concurrent CPU load (the harness recording
        // degrades slightly with machine load — Jolt's separate physics thread against the synchronous
        // stepping loop). The pre-gate travel exits skated planted feet at 1.245-1.718 m/s. With the gated
        // exit accepted, 1.20 m/s sits between the measured populations [1.150 .. 1.245] — the margins are
        // thin by construction (the seam cost approaches the old defect floor), so regression discrimination
        // leans on the discontinuity ceiling, which retains a wide margin (101 vs 144+).
        MaximumStanceSlipSpeed: 1.20f,
        // Coarse gross-skate backstop: clean per-interval stance travel (toe-off ankle arc) accumulates up to
        // 0.228 m and no measured seam case exceeds 0.232 m before the slip-SPEED or swing metrics fire, so
        // this bound never discriminates today's populations — it exists to catch gross multi-interval skates.
        // Slip speed is the precise discriminator.
        MaximumStanceSlipDistance: 0.30f,
        // Measured across settled calibration runs before and after the CTRL-001 travel-exit release gate: clean
        // sustained-walk swing stayed at or below 2.832 m/s; the release-gated exits sweep the releasing foot
        // through the authored *2i stitch-anchor pose gap (measured directly at ~0.068 m root-relative between
        // wf at its release event and f2i_l at t=0 — an ANIM-002 content property) at 2.680-3.102 m/s across
        // five runs with exact landing rates. The ungated travel exits swept at 3.356-5.926 m/s. With the gated
        // exit accepted, 3.25 m/s sits between the measured populations [3.102 .. 3.356] — the margins are thin
        // by construction (the seam cost nearly reaches the old defect floor), so regression discrimination
        // leans on the stance-slip and discontinuity ceilings, which retain wide margins (1.071 vs 1.245+, and
        // 101 vs 144+).
        MaximumSwingPlanarSpeed: 3.25f,
        // Measured: the clean control's worst single-tick foot-speed step is 100.8-101.0 m/s² across runs
        // (the authored i2f→PlayerTravel boundary, including the one-tick pose freeze every state-machine
        // switch shows); the calmest abort splice reaches a run-stable 108.955 m/s² and stays accepted, while
        // release-during-travel sweeps measure 144.3-224.3 m/s². 125 m/s² sits between the measured populations
        // [108.955 .. 144.3] with ~15% margin on both sides.
        MaximumSpeedDiscontinuity: 125f,
        // Measured: travel phase locks within the first cycle; 1.0 s (≈0.94 cycles) skips the entry→travel
        // boundary step while keeping more than two full cycles of onsets under test in the 5 s control hold.
        TravelPhaseWarmupSeconds: 1.0f,
        // Measured: clean same-foot step intervals are 1.067 s (the wf cycle at rate 1.0) with zero spread;
        // 0.80 s (0.75 cycles) rejects a skipped-step hitch with margin.
        MinimumTravelStepIntervalSeconds: 0.80f,
        // Measured: same 1.067 s clean baseline; 1.40 s (1.31 cycles) bounds hesitation.
        MaximumTravelStepIntervalSeconds: 1.40f,
        // CTRL-001 TR4: a brief bounded idle→entry crossfade slide is accepted presentation seam-smoothing, so
        // the first 0.25 s after the graph enters an i2* node is excluded; seam defects at the exit splice and
        // the travel handoff happen on other nodes and stay fully in scope.
        ExclusionWindows:
        [
            new FluidityExclusionWindow("PlayerI2F", 0.25f),
            new FluidityExclusionWindow("PlayerI2B", 0.25f),
        ]);
}

/// <summary>Metric names reported by the fluidity gate.</summary>
public static class FluidityMetrics
{
    public const string StanceSlipSpeed = "StanceSlipSpeed";
    public const string StanceSlipDistance = "StanceSlipDistance";
    public const string SwingSpeedCeiling = "SwingSpeedCeiling";
    public const string SpeedDiscontinuity = "SpeedDiscontinuity";
    public const string PhaseAlternation = "PhaseAlternation";
    public const string StepIntervalHitch = "StepIntervalHitch";
    public const string StepIntervalHesitation = "StepIntervalHesitation";
}

/// <summary>One threshold violation with enough context to locate the offending transition seam.</summary>
public sealed record FluidityViolation(
    string Metric,
    string Foot,
    float Value,
    float Threshold,
    float TimeSeconds,
    string Context);

/// <summary>Per-foot measurement summary for one evaluated bout.</summary>
public sealed record FluidityFootStatistics(
    int StanceIntervalCount,
    float MaximumStanceSlipSpeed,
    float MaximumStanceSlipSpeedTimeSeconds,
    float MaximumStanceSlipDistance,
    float MaximumSwingPlanarSpeed,
    float MaximumSwingPlanarSpeedTimeSeconds,
    float MaximumSpeedDiscontinuity,
    float MaximumSpeedDiscontinuityTimeSeconds,
    IReadOnlyList<float> SwingOnsetTimesSeconds,
    IReadOnlyList<float> TravelStepIntervalsSeconds);

/// <summary>Case-level measurement summary for one evaluated bout.</summary>
public sealed record FluidityCaseStatistics(
    int SampleCount,
    float TickSeconds,
    float DurationSeconds,
    float? TravelStartSeconds,
    int ExcludedSampleCount,
    FluidityFootStatistics LeftFoot,
    FluidityFootStatistics RightFoot);

/// <summary>Deterministic, printable evaluation result for one recorded bout.</summary>
public sealed record FluidityGateResult(
    string CaseName,
    bool Passed,
    IReadOnlyList<FluidityViolation> Violations,
    FluidityCaseStatistics Statistics,
    IReadOnlyList<FluidityContactRejection> RejectedSamples)
{
    /// <summary>Builds the full human-readable report for this result (invariant culture, stable ordering).</summary>
    public string BuildReport()
    {
        StringBuilder builder = new();
        _ = builder.AppendLine($"Fluidity gate report: {CaseName}");
        _ = builder.AppendLine(
            $"Tick: {Fmt(Statistics.TickSeconds)} s ({1f / Statistics.TickSeconds:0.##} Hz)  Samples: {Statistics.SampleCount}  Duration: {Fmt(Statistics.DurationSeconds)} s  Excluded: {Statistics.ExcludedSampleCount}");
        _ = builder.AppendLine(
            Statistics.TravelStartSeconds is float travelStart
                ? $"Travel start: {Fmt(travelStart)} s"
                : "Travel start: <never observed>");
        AppendFootStatistics(builder, "left", Statistics.LeftFoot);
        AppendFootStatistics(builder, "right", Statistics.RightFoot);
        _ = builder.AppendLine(Passed ? "Result: PASS" : $"Result: FAIL ({Violations.Count} violations)");
        foreach (FluidityViolation violation in Violations.OrderBy(v => v.TimeSeconds).ThenBy(v => v.Metric).ThenBy(v => v.Foot))
        {
            _ = builder.AppendLine(
                $"  {violation.Metric}({violation.Foot}): {Fmt(violation.Value)} > {Fmt(violation.Threshold)} @ {Fmt(violation.TimeSeconds)} s [{violation.Context}]");
        }

        foreach (FluidityContactRejection rejection in RejectedSamples.OrderBy(rejection => rejection.SampleIndex).ThenBy(rejection => rejection.Foot))
        {
            _ = builder.AppendLine($"  rejected contact sample {rejection.SampleIndex} ({rejection.Foot}) @ {Fmt(rejection.TimeSeconds)} s: {rejection.Reason}");
        }

        return builder.ToString();
    }

    private static void AppendFootStatistics(StringBuilder builder, string foot, FluidityFootStatistics statistics)
    {
        _ = builder.AppendLine(
            $"{foot}: stance intervals {statistics.StanceIntervalCount}, max slip speed {Fmt(statistics.MaximumStanceSlipSpeed)} m/s @ {Fmt(statistics.MaximumStanceSlipSpeedTimeSeconds)} s, max slip distance {Fmt(statistics.MaximumStanceSlipDistance)} m, max swing speed {Fmt(statistics.MaximumSwingPlanarSpeed)} m/s @ {Fmt(statistics.MaximumSwingPlanarSpeedTimeSeconds)} s, max speed discontinuity {Fmt(statistics.MaximumSpeedDiscontinuity)} m/s^2 @ {Fmt(statistics.MaximumSpeedDiscontinuityTimeSeconds)} s");
        _ = builder.AppendLine($"{foot}: swing onset times (s): {FormatList(statistics.SwingOnsetTimesSeconds)}");
        _ = builder.AppendLine($"{foot}: travel step intervals (s): {FormatList(statistics.TravelStepIntervalsSeconds)}");
    }

    private static string FormatList(IReadOnlyList<float> values)
        => values.Count == 0 ? "<none>" : string.Join(", ", values.Select(Fmt));

    private static string Fmt(float value) => value.ToString("0.000", CultureInfo.InvariantCulture);
}

/// <summary>
/// Pure analysis library for the CTRL-001 player locomotion fluidity gate. Consumes recorded per-physics-tick
/// samples, supplied authoritative contact evidence, and the observed graph-node timeline. Support is never
/// inferred from ankle height: missing or rejected evidence fails closed. No scene access — all inputs are plain
/// records, so results are deterministic and reproducible from dumped samples.
/// </summary>
public static class PlayerLocomotionFluidityGate
{
    private const string TravelNodeName = "PlayerTravel";

    /// <summary>Evaluates one recorded bout against the given thresholds and expected authoritative support intervals.</summary>
    public static FluidityGateResult Evaluate(
        string caseName,
        IReadOnlyList<FluiditySample> samples,
        IReadOnlyList<FluidityNodeSegment> nodeTimeline,
        FluidityGateThresholds thresholds,
        FluidityExpectedSupportPopulation? expectedSupport = null)
    {
        if (samples.Count < 3)
        {
            return Fail(caseName, samples, new FluidityViolation(
                "SampleCount", "both", samples.Count, 3f, 0f, "insufficient samples for analysis"));
        }

        float tick = samples[1].TimeSeconds - samples[0].TimeSeconds;
        if (!float.IsFinite(tick) || tick <= 0f)
        {
            return Fail(caseName, samples, new FluidityViolation(
                "SampleTick", "both", tick, 0.0001f, samples[1].TimeSeconds, "non-monotonic sample times"));
        }

        FluidityContactValidation contact = FluidityContactOracle.Validate(
            samples,
            static sample => sample.TimeSeconds,
            static sample => sample.Contacts,
            expectedSupport);

        bool[] excluded = BuildExclusionMask(samples, nodeTimeline, thresholds);
        // Both feet share one time/node view of the bout; the previous-node column is derived once from the
        // node timeline so every violation can name the transition it happened on ("PlayerF2IL from PlayerI2FL").
        SampleView view = BuildSampleView(samples, nodeTimeline);
        FootTracks left = BuildFootTracks(samples, static sample => sample.LeftFootWorld, static sample => sample.Contacts?.Left, tick, contact.LeftFullSupport, view);
        FootTracks right = BuildFootTracks(samples, static sample => sample.RightFootWorld, static sample => sample.Contacts?.Right, tick, contact.RightFullSupport, view);

        float? travelStart = nodeTimeline
            .Where(segment => segment.NodeName == TravelNodeName)
            .Select(segment => (float?)segment.EnterTimeSeconds)
            .FirstOrDefault();

        List<FluidityViolation> violations = [];
        violations.AddRange(contact.Rejections.Select(rejection => new FluidityViolation(
            "ContactEvidence",
            rejection.Foot,
            rejection.SampleIndex,
            0f,
            rejection.TimeSeconds,
            rejection.Reason)));
        FluidityFootStatistics leftStatistics = EvaluateFootSlipAndSpeed(
            "left", left, excluded, tick, thresholds, violations);
        FluidityFootStatistics rightStatistics = EvaluateFootSlipAndSpeed(
            "right", right, excluded, tick, thresholds, violations);
        (leftStatistics, rightStatistics) = EvaluatePhaseConsistency(
            ("left", left, leftStatistics), ("right", right, rightStatistics),
            excluded, thresholds, travelStart, violations);

        FluidityCaseStatistics statistics = new(
            samples.Count,
            tick,
            samples[^1].TimeSeconds,
            travelStart,
            excluded.Count(static value => value),
            leftStatistics,
            rightStatistics);
        return new FluidityGateResult(caseName, violations.Count == 0, violations, statistics, contact.Rejections);
    }

    private static FluidityGateResult Fail(
        string caseName,
        IReadOnlyList<FluiditySample> samples,
        FluidityViolation violation)
        => new(caseName, false, [violation], CreateEmptyStatistics(samples), []);

    private sealed class SampleView
    {
        public required float[] Time
        {
            get; init;
        }
        public required string[] Nodes
        {
            get; init;
        }
        public required string[] PreviousNodes
        {
            get; init;
        }
    }

    private sealed class FootTracks
    {
        public required Vector2[] Planar
        {
            get; init;
        }
        public required float[] Height
        {
            get; init;
        }
        public required float[] PlanarSpeed
        {
            get; init;
        }
        public required float[] VerticalSpeed
        {
            get; init;
        }
        public required bool[] Stance
        {
            get; init;
        }
        public required SampleView View
        {
            get; init;
        }
    }

    private static SampleView BuildSampleView(IReadOnlyList<FluiditySample> samples, IReadOnlyList<FluidityNodeSegment> nodeTimeline)
    {
        SampleView view = new()
        {
            Time = new float[samples.Count],
            Nodes = new string[samples.Count],
            PreviousNodes = new string[samples.Count],
        };
        int segmentIndex = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            view.Time[i] = samples[i].TimeSeconds;
            view.Nodes[i] = samples[i].GraphNode;
            while (segmentIndex + 1 < nodeTimeline.Count
                && samples[i].TimeSeconds > nodeTimeline[segmentIndex].ExitTimeSeconds)
            {
                segmentIndex++;
            }

            view.PreviousNodes[i] = segmentIndex > 0 ? nodeTimeline[segmentIndex - 1].NodeName : "<start>";
        }

        return view;
    }

    private static FootTracks BuildFootTracks(
        IReadOnlyList<FluiditySample> samples,
        Func<FluiditySample, Vector3> selectFoot,
        Func<FluiditySample, FluidityFootContactEvidence?> selectContact,
        float tick,
        bool[] fullSupport,
        SampleView view)
    {
        int count = samples.Count;
        FootTracks tracks = new()
        {
            Planar = new Vector2[count],
            Height = new float[count],
            PlanarSpeed = new float[count],
            VerticalSpeed = new float[count],
            Stance = new bool[count],
            View = view,
        };
        for (int i = 0; i < count; i++)
        {
            Vector3 world = selectFoot(samples[i]);
            tracks.Planar[i] = new Vector2(world.X, world.Z);
            tracks.Height[i] = world.Y;
            if (i > 0)
            {
                tracks.PlanarSpeed[i] = tracks.Planar[i].DistanceTo(tracks.Planar[i - 1]) / tick;
                tracks.VerticalSpeed[i] = Mathf.Abs(tracks.Height[i] - tracks.Height[i - 1]) / tick;
            }
        }

        for (int i = 0; i < count; i++)
        {
            tracks.Stance[i] = fullSupport[i] && selectContact(samples[i])?.Phase == FluidityContactPhase.FullSupport;
        }

        return tracks;
    }

    private static bool[] BuildExclusionMask(
        IReadOnlyList<FluiditySample> samples,
        IReadOnlyList<FluidityNodeSegment> nodeTimeline,
        FluidityGateThresholds thresholds)
    {
        bool[] excluded = new bool[samples.Count];
        if (thresholds.ExclusionWindows.Count == 0 || nodeTimeline.Count == 0 || samples.Count == 0)
        {
            return excluded;
        }

        foreach (FluidityNodeSegment segment in nodeTimeline)
        {
            foreach (FluidityExclusionWindow window in thresholds.ExclusionWindows)
            {
                if (!segment.NodeName.StartsWith(window.NodeNamePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                // A node change away from the matched node ends the window early so contact samples cannot
                // inherit an accepted slide window from an unrelated state.
                float end = Mathf.Min(segment.ExitTimeSeconds, segment.EnterTimeSeconds + window.DurationSeconds);
                for (int i = 0; i < samples.Count; i++)
                {
                    float time = samples[i].TimeSeconds;
                    if (time >= segment.EnterTimeSeconds && time <= end)
                    {
                        excluded[i] = true;
                    }
                }
            }
        }

        return excluded;
    }

    private static FluidityFootStatistics EvaluateFootSlipAndSpeed(
        string foot,
        FootTracks tracks,
        bool[] excluded,
        float tick,
        FluidityGateThresholds thresholds,
        List<FluidityViolation> violations)
    {
        int count = tracks.Planar.Length;
        float maxSlipSpeed = 0f;
        float maxSlipSpeedTime = 0f;
        float maxSlipDistance = 0f;
        int stanceIntervals = 0;

        int runStart = -1;
        for (int i = 0; i <= count; i++)
        {
            bool inStance = i < count && tracks.Stance[i];
            if (inStance && runStart < 0)
            {
                runStart = i;
            }
            else if (!inStance && runStart >= 0)
            {
                int runEnd = i - 1;
                (float speed, float speedTime, float distance) = MeasureStanceRun(tracks, excluded, tick, runStart, runEnd);
                stanceIntervals++;
                if (speed > maxSlipSpeed)
                {
                    maxSlipSpeed = speed;
                    maxSlipSpeedTime = speedTime;
                }

                maxSlipDistance = Mathf.Max(maxSlipDistance, distance);
                if (speed > thresholds.MaximumStanceSlipSpeed)
                {
                    violations.Add(new FluidityViolation(
                        FluidityMetrics.StanceSlipSpeed, foot, speed, thresholds.MaximumStanceSlipSpeed,
                        speedTime, ContextAt(tracks, speedTime)));
                }

                if (distance > thresholds.MaximumStanceSlipDistance)
                {
                    violations.Add(new FluidityViolation(
                        FluidityMetrics.StanceSlipDistance, foot, distance, thresholds.MaximumStanceSlipDistance,
                        tracks.View.Time[runStart], ContextAt(tracks, speedTime)));
                }

                runStart = -1;
            }
        }

        float maxSwingSpeed = 0f;
        float maxSwingTime = 0f;
        float maxDiscontinuity = 0f;
        float maxDiscontinuityTime = 0f;
        for (int i = 1; i < count; i++)
        {
            if (!excluded[i] && tracks.PlanarSpeed[i] > maxSwingSpeed)
            {
                maxSwingSpeed = tracks.PlanarSpeed[i];
                maxSwingTime = tracks.View.Time[i];
            }

            if (!excluded[i] && !excluded[i - 1])
            {
                float discontinuity = Mathf.Abs(tracks.PlanarSpeed[i] - tracks.PlanarSpeed[i - 1]) / tick;
                if (discontinuity > maxDiscontinuity)
                {
                    maxDiscontinuity = discontinuity;
                    maxDiscontinuityTime = tracks.View.Time[i];
                }
            }
        }

        if (maxSwingSpeed > thresholds.MaximumSwingPlanarSpeed)
        {
            violations.Add(new FluidityViolation(
                FluidityMetrics.SwingSpeedCeiling, foot, maxSwingSpeed, thresholds.MaximumSwingPlanarSpeed,
                maxSwingTime, ContextAt(tracks, maxSwingTime)));
        }

        if (maxDiscontinuity > thresholds.MaximumSpeedDiscontinuity)
        {
            violations.Add(new FluidityViolation(
                FluidityMetrics.SpeedDiscontinuity, foot, maxDiscontinuity, thresholds.MaximumSpeedDiscontinuity,
                maxDiscontinuityTime, ContextAt(tracks, maxDiscontinuityTime)));
        }

        return new FluidityFootStatistics(
            stanceIntervals,
            maxSlipSpeed,
            maxSlipSpeedTime,
            maxSlipDistance,
            maxSwingSpeed,
            maxSwingTime,
            maxDiscontinuity,
            maxDiscontinuityTime,
            [],
            []);
    }

    private static (float Speed, float SpeedTime, float Distance) MeasureStanceRun(
        FootTracks tracks,
        bool[] excluded,
        float tick,
        int runStart,
        int runEnd)
    {
        float speed = 0f;
        float speedTime = tracks.View.Time[runStart];
        float distance = 0f;
        for (int i = runStart + 1; i <= runEnd; i++)
        {
            if (excluded[i] || excluded[i - 1])
            {
                continue;
            }

            float step = tracks.Planar[i].DistanceTo(tracks.Planar[i - 1]);
            distance += step;
            if (step / tick > speed)
            {
                speed = step / tick;
                speedTime = tracks.View.Time[i];
            }
        }

        return (speed, speedTime, distance);
    }

    /// <summary>
    /// Enforces step-phase consistency during sustained travel: swing onsets must alternate left/right strictly
    /// and each same-foot step interval must stay inside the calibrated hitch/hesitation bounds. Onsets outside
    /// sustained travel (entry, exit, idle, warmup) are ignored, matching the transition-lenient treatment.
    /// Returns the per-foot statistics extended with the observed onset times and step intervals.
    /// </summary>
    private static (FluidityFootStatistics Left, FluidityFootStatistics Right) EvaluatePhaseConsistency(
        (string Name, FootTracks Tracks, FluidityFootStatistics Statistics) left,
        (string Name, FootTracks Tracks, FluidityFootStatistics Statistics) right,
        bool[] excluded,
        FluidityGateThresholds thresholds,
        float? travelStart,
        List<FluidityViolation> violations)
    {
        if (travelStart is not float travel)
        {
            return (left.Statistics, right.Statistics);
        }

        List<int> leftOnsets = CollectTravelOnsets(left.Tracks, excluded, thresholds, travel);
        List<int> rightOnsets = CollectTravelOnsets(right.Tracks, excluded, thresholds, travel);
        if (leftOnsets.Count + rightOnsets.Count >= 2)
        {
            // Strict alternation across the merged onset sequence; both feet share the same time/node view.
            List<(string Foot, int Index)> merged = [.. leftOnsets.Select(index => (Foot: left.Name, Index: index))
                .Concat(rightOnsets.Select(index => (Foot: right.Name, Index: index)))
                .OrderBy(entry => entry.Index)];
            for (int i = 1; i < merged.Count; i++)
            {
                if (merged[i].Foot == merged[i - 1].Foot)
                {
                    float interval = left.Tracks.View.Time[merged[i].Index] - left.Tracks.View.Time[merged[i - 1].Index];
                    violations.Add(new FluidityViolation(
                        FluidityMetrics.PhaseAlternation, merged[i].Foot, interval,
                        thresholds.MinimumTravelStepIntervalSeconds, left.Tracks.View.Time[merged[i].Index],
                        ContextAt(left.Tracks, left.Tracks.View.Time[merged[i].Index])));
                }
            }
        }

        return (
            WithStepPhase(left, leftOnsets, thresholds, violations),
            WithStepPhase(right, rightOnsets, thresholds, violations));
    }

    private static FluidityFootStatistics WithStepPhase(
        (string Name, FootTracks Tracks, FluidityFootStatistics Statistics) foot,
        List<int> onsets,
        FluidityGateThresholds thresholds,
        List<FluidityViolation> violations)
    {
        List<float> onsetTimes = [.. onsets.Select(index => foot.Tracks.View.Time[index])];
        List<float> intervals = [];
        for (int i = 1; i < onsets.Count; i++)
        {
            float interval = foot.Tracks.View.Time[onsets[i]] - foot.Tracks.View.Time[onsets[i - 1]];
            intervals.Add(interval);
            if (interval < thresholds.MinimumTravelStepIntervalSeconds)
            {
                violations.Add(new FluidityViolation(
                    FluidityMetrics.StepIntervalHitch, foot.Name, interval,
                    thresholds.MinimumTravelStepIntervalSeconds, foot.Tracks.View.Time[onsets[i]],
                    ContextAt(foot.Tracks, foot.Tracks.View.Time[onsets[i]])));
            }
            else if (interval > thresholds.MaximumTravelStepIntervalSeconds)
            {
                violations.Add(new FluidityViolation(
                    FluidityMetrics.StepIntervalHesitation, foot.Name, interval,
                    thresholds.MaximumTravelStepIntervalSeconds, foot.Tracks.View.Time[onsets[i]],
                    ContextAt(foot.Tracks, foot.Tracks.View.Time[onsets[i]])));
            }
        }

        return foot.Statistics with
        {
            SwingOnsetTimesSeconds = onsetTimes,
            TravelStepIntervalsSeconds = intervals
        };
    }

    private static List<int> CollectTravelOnsets(
        FootTracks tracks,
        bool[] excluded,
        FluidityGateThresholds thresholds,
        float travelStart)
    {
        List<int> onsets = [];
        for (int i = 1; i < tracks.Stance.Length; i++)
        {
            if (!tracks.Stance[i - 1] || tracks.Stance[i])
            {
                continue;
            }

            // Debounce heel-strike chatter: only a swing run long enough to be a true step counts as an onset.
            int runEnd = i;
            while (runEnd < tracks.Stance.Length && !tracks.Stance[runEnd])
            {
                runEnd++;
            }

            if (runEnd - i < thresholds.MinimumSwingSamples
                || tracks.View.Nodes[i] != TravelNodeName
                || excluded[i]
                || excluded[i - 1]
                || tracks.View.Time[i] - travelStart < thresholds.TravelPhaseWarmupSeconds)
            {
                continue;
            }

            onsets.Add(i);
        }

        return onsets;
    }

    private static string ContextAt(FootTracks tracks, float time)
    {
        int index = 0;
        float nearest = float.PositiveInfinity;
        for (int i = 0; i < tracks.View.Time.Length; i++)
        {
            float distance = Mathf.Abs(tracks.View.Time[i] - time);
            if (distance < nearest)
            {
                nearest = distance;
                index = i;
            }
        }

        return $"{tracks.View.Nodes[index]} from {tracks.View.PreviousNodes[index]}";
    }

    private static FluidityCaseStatistics CreateEmptyStatistics(IReadOnlyList<FluiditySample> samples) => new(
        samples.Count,
        samples.Count >= 2 ? samples[1].TimeSeconds - samples[0].TimeSeconds : 0f,
        samples.Count > 0 ? samples[^1].TimeSeconds : 0f,
        null,
        0,
        new FluidityFootStatistics(0, 0f, 0f, 0f, 0f, 0f, 0f, 0f, [], []),
        new FluidityFootStatistics(0, 0f, 0f, 0f, 0f, 0f, 0f, 0f, [], []));
}
