using System.Globalization;
using System.Text;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Test-only thresholds for the reported terminal exit episode. Values are calibrated from the terminal windows of
/// nearby real-NPC controls for each focused run; they are not navigation-quality limits.
/// </summary>
public sealed record ExitStruggleThresholds(
    float FallbackTerminalRemainingDistance,
    float ArrivalRemainingDistance,
    float MaximumPauseRouteSpeed,
    int MinimumPauseTicks,
    float FootHopPlanarStep,
    float MaterialActorPlanarStep,
    float MaterialRemainingDistanceDrop,
    float RouteSpeedDiscontinuity,
    float RouteAccelerationDiscontinuity,
    int MaximumTicksBetweenFeet,
    int MaximumTicksFromHopToTerminalResolution);

/// <summary>Measured terminal-control envelope used to derive an <see cref="ExitStruggleThresholds"/> instance.</summary>
public sealed record ExitStruggleCalibration(
    float ControlMinimumPositiveRouteSpeed,
    float ControlMaximumFootPlanarStep,
    float ControlMaximumActorPlanarStep,
    float ControlMaximumRemainingDistanceDrop,
    float ControlMaximumRouteSpeed,
    float ControlMaximumPositiveRouteAcceleration,
    float ApproachRemainingDistanceMaximum,
    ExitStruggleThresholds Thresholds);

/// <summary>A pause run that remained outside the arrival band while travel or exit animation continued.</summary>
public sealed record ExitStrugglePause(int StartSampleIndex, int EndSampleIndex, float MinimumRemainingDistance);

/// <summary>Near-simultaneous rising planar foot-step crossings.</summary>
public sealed record ExitStrugglePairedHop(
    int LeftSampleIndex,
    int RightSampleIndex,
    float LeftPlanarStep,
    float RightPlanarStep)
{
    /// <summary>The first of the two foot-hop onsets.</summary>
    public int OnsetSampleIndex => Math.Min(LeftSampleIndex, RightSampleIndex);
}

/// <summary>A material terminal arrival/completion snap or positive realised route discontinuity.</summary>
public sealed record ExitStruggleTerminalResolution(
    int SampleIndex,
    bool ArrivalOrCompletion,
    float ActorPlanarStep,
    float RemainingDistanceDrop,
    float RouteSpeed,
    float RouteAcceleration)
{
    public bool IsMaterialArrivalOrCompletion => ArrivalOrCompletion;
}

/// <summary>One compound terminal episode, present only when a paired hop precedes an immediate terminal resolution.</summary>
public sealed record ExitStruggleEpisode(
    ExitStrugglePairedHop PairedHop,
    ExitStruggleTerminalResolution TerminalResolution);

/// <summary>Independent detector result and retained raw terminal diagnostics.</summary>
public sealed record ExitStruggleResult(
    string CaseName,
    bool TerminalApproachObserved,
    int TerminalStartSampleIndex,
    IReadOnlyList<ExitStrugglePause> Pauses,
    IReadOnlyList<ExitStrugglePairedHop> PairedHops,
    IReadOnlyList<ExitStruggleTerminalResolution> TerminalResolutions,
    ExitStruggleEpisode? Episode,
    IReadOnlyList<NPCFluiditySample> Samples,
    ExitStruggleThresholds Thresholds)
{
    public bool EpisodeDetected => Episode is not null;

    public string BuildReport(ExitStruggleCalibration? calibration = null)
    {
        StringBuilder builder = new();
        _ = builder.AppendLine($"Exit-struggle detector report: {CaseName}");
        _ = builder.AppendLine($"Terminal window: {(TerminalApproachObserved ? "terminal approach" : "remaining-distance fallback")} from sample {TerminalStartSampleIndex}");
        if (calibration is not null)
        {
            _ = builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Calibration terminal envelope: p10 positive realised route speed {calibration.ControlMinimumPositiveRouteSpeed:0.000} m/s, " +
                $"maximum foot step {calibration.ControlMaximumFootPlanarStep:0.000} m/tick, " +
                $"maximum actor step {calibration.ControlMaximumActorPlanarStep:0.000} m/tick, " +
                $"maximum remaining drop {calibration.ControlMaximumRemainingDistanceDrop:0.000} m/tick, " +
                $"maximum route speed {calibration.ControlMaximumRouteSpeed:0.000} m/s, " +
                $"maximum positive acceleration {calibration.ControlMaximumPositiveRouteAcceleration:0.000} m/s², " +
                $"maximum approach remaining {calibration.ApproachRemainingDistanceMaximum:0.000} m."));
        }

        _ = builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Thresholds: fallback remaining {Thresholds.FallbackTerminalRemainingDistance:0.000} m, arrival {Thresholds.ArrivalRemainingDistance:0.000} m, " +
            $"foot hop > {Thresholds.FootHopPlanarStep:0.000} m/tick, material actor step >= {Thresholds.MaterialActorPlanarStep:0.000} m/tick, " +
            $"material remaining drop >= {Thresholds.MaterialRemainingDistanceDrop:0.000} m/tick, route speed >= {Thresholds.RouteSpeedDiscontinuity:0.000} m/s, " +
            $"route acceleration >= {Thresholds.RouteAccelerationDiscontinuity:0.000} m/s²."));
        _ = builder.AppendLine(
            "Threshold rationale: terminal-window healthy maxima are multiplied by 1.25 for actor/route " +
            "resolution and 1.50 for common-mode foot hops; small floors retain sensitivity when a control " +
            "envelope is zero. The fallback terminal band is 1.25× maximum approach remaining (floor 0.08 m).");
        _ = builder.AppendLine($"Candidates: pauses {Pauses.Count} (diagnostic only), paired hops {PairedHops.Count}, terminal resolutions {TerminalResolutions.Count}");
        _ = builder.AppendLine(EpisodeDetected
            ? "Result: REPRODUCED."
            : $"Result: NO PRIMARY EPISODE. {DescribeMissingRequirement()}");
        AppendCandidateWindows(builder);
        if (Episode is { } episode)
        {
            _ = builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"Episode: hop onset {episode.PairedHop.OnsetSampleIndex}, L{episode.PairedHop.LeftSampleIndex} {episode.PairedHop.LeftPlanarStep:0.000} m / " +
                $"R{episode.PairedHop.RightSampleIndex} {episode.PairedHop.RightPlanarStep:0.000} m; terminal resolution {episode.TerminalResolution.SampleIndex} " +
                $"arrival/completion {episode.TerminalResolution.ArrivalOrCompletion}, actor step {episode.TerminalResolution.ActorPlanarStep:0.000} m, " +
                $"remaining drop {episode.TerminalResolution.RemainingDistanceDrop:0.000} m, speed {episode.TerminalResolution.RouteSpeed:0.000} m/s @ " +
                $"{episode.TerminalResolution.RouteAcceleration:0.000} m/s²."));
        }

        return builder.ToString();
    }

    private string DescribeMissingRequirement() =>
        TerminalStartSampleIndex >= Samples.Count
            ? "No terminal-approach or fallback remaining-distance observation window."
            : PairedHops.Count == 0
                ? "No terminal paired left/right hop onset outside arrival before completion."
                : TerminalResolutions.Count == 0
                    ? "No material terminal arrival/completion or positive realised route discontinuity candidate."
                    : "Paired-hop and terminal-resolution candidates did not occur within the immediate terminal window.";

    private void AppendCandidateWindows(StringBuilder builder)
    {
        if (Pauses.Count == 0 && PairedHops.Count == 0 && TerminalResolutions.Count == 0)
        {
            _ = builder.AppendLine("Candidate windows: none.");
            return;
        }

        foreach (ExitStrugglePause pause in Pauses)
        {
            AppendSampleWindow(builder, $"Pause observation [{pause.StartSampleIndex}..{pause.EndSampleIndex}] (diagnostic only)", pause.StartSampleIndex);
        }

        foreach (ExitStrugglePairedHop hop in PairedHops)
        {
            AppendSampleWindow(builder,
                $"Paired-hop candidate onset {hop.OnsetSampleIndex} (L{hop.LeftSampleIndex}/R{hop.RightSampleIndex})",
                hop.OnsetSampleIndex);
        }

        foreach (ExitStruggleTerminalResolution resolution in TerminalResolutions)
        {
            AppendSampleWindow(builder, $"Terminal-resolution candidate {resolution.SampleIndex}", resolution.SampleIndex);
        }
    }

    private void AppendSampleWindow(StringBuilder builder, string title, int centre)
    {
        int first = Math.Max(TerminalStartSampleIndex, centre - 2);
        int last = Math.Min(Samples.Count - 1, centre + 2);
        _ = builder.AppendLine($"{title} samples [{first}..{last}]: idx time actor routeStep routeProgress remaining tangent stage/lane/node fading phase rate approach entries/exits authored synthetic collided leftFoot rightFoot");
        for (int index = first; index <= last; index++)
        {
            NPCFluiditySample sample = Samples[index];
            _ = builder.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  {index} {sample.TimeSeconds:0.000} {Format(sample.ActorWorldPosition)} {Format(sample.RealisedActorStepWorld)} " +
                $"{sample.RouteProgress:0.000} {sample.RemainingRouteDistance:0.000} {Format(sample.RouteTangentWorld)} " +
                $"{sample.ProgrammeStage}/{sample.PhaseLane}/{sample.GraphNode} {sample.FadingFromNode} {sample.NormalisedPhase:0.000} {sample.PlaybackRate:0.000} " +
                $"{sample.TerminalApproachActive} {sample.EntryCount}/{sample.ExitCount} {Format(sample.AuthoredRootWorldStep)} " +
                $"{Format(sample.RequestedSyntheticWorldStep)} {sample.MovementCollided} {Format(sample.LeftFootWorld)} {Format(sample.RightFootWorld)}"));
        }
    }

    private static string Format(Vector3 value)
        => string.Create(CultureInfo.InvariantCulture, $"({value.X:0.000},{value.Y:0.000},{value.Z:0.000})");
}

/// <summary>
/// Detects the reported terminal paired-foot-hop pattern exclusively from collision-realised actor and skeleton data.
/// A pause is retained as diagnostic context, but deliberately cannot participate in episode qualification.
/// </summary>
public static class ExitStruggleDetector
{
    public static ExitStruggleCalibration Calibrate(IReadOnlyList<NPCFluidityBoutRecord> controls)
    {
        ArgumentNullException.ThrowIfNull(controls);
        if (controls.Count == 0)
        {
            throw new ArgumentException("At least one nearby control is required for exit-struggle calibration.", nameof(controls));
        }

        float approachRemainingMaximum = 0f;
        foreach (NPCFluidityBoutRecord control in controls)
        {
            foreach (NPCFluiditySample sample in control.Samples)
            {
                if (sample.TerminalApproachActive)
                {
                    approachRemainingMaximum = Math.Max(approachRemainingMaximum, sample.RemainingRouteDistance);
                }
            }
        }

        // The fallback is intentionally generous enough to preserve terminal evidence if the explicit approach flag is absent.
        float fallbackTerminalRemainingDistance = Math.Max(0.08f, approachRemainingMaximum * 1.25f);
        List<float> positiveRouteSpeeds = [];
        float maximumFootStep = 0f;
        float maximumActorStep = 0f;
        float maximumRemainingDistanceDrop = 0f;
        float maximumRouteSpeed = 0f;
        float maximumPositiveRouteAcceleration = 0f;
        foreach (NPCFluidityBoutRecord control in controls)
        {
            int terminalStart = FindTerminalStart(control.Samples, fallbackTerminalRemainingDistance);
            if (terminalStart < 0)
            {
                continue;
            }

            for (int index = Math.Max(terminalStart + 1, 1); index < control.Samples.Count; index++)
            {
                NPCFluiditySample previous = control.Samples[index - 1];
                NPCFluiditySample current = control.Samples[index];
                float tick = Tick(control.Samples, index);
                float routeSpeed = Math.Max(0f, current.RealisedRouteLongitudinal / tick);
                float previousRouteSpeed = Math.Max(0f, previous.RealisedRouteLongitudinal / tick);

                if (routeSpeed > 0f)
                {
                    positiveRouteSpeeds.Add(routeSpeed);
                }

                maximumFootStep = Math.Max(maximumFootStep, Math.Max(
                    PlanarStep(current.LeftFootWorld, previous.LeftFootWorld),
                    PlanarStep(current.RightFootWorld, previous.RightFootWorld)));
                maximumActorStep = Math.Max(maximumActorStep, PlanarMagnitude(current.RealisedActorStepWorld));
                maximumRemainingDistanceDrop = Math.Max(
                    maximumRemainingDistanceDrop,
                    previous.RemainingRouteDistance - current.RemainingRouteDistance);
                maximumRouteSpeed = Math.Max(maximumRouteSpeed, routeSpeed);
                maximumPositiveRouteAcceleration = Math.Max(
                    maximumPositiveRouteAcceleration,
                    (routeSpeed - previousRouteSpeed) / tick);
            }
        }

        if (positiveRouteSpeeds.Count == 0)
        {
            throw new InvalidOperationException("Nearby controls did not provide positive collision-realised terminal route movement.");
        }

        positiveRouteSpeeds.Sort();
        float minimumPositiveRouteSpeed = positiveRouteSpeeds[Math.Min(positiveRouteSpeeds.Count - 1, positiveRouteSpeeds.Count / 10)];
        // Terminal-window multipliers intentionally sit above healthy observed maxima: 1.50× rejects normal
        // alternating foot motion, while 1.25× preserves a material margin above normal terminal actor/route change.
        // Floors keep a stationary healthy envelope from collapsing the detector to a zero-step comparison.
        ExitStruggleThresholds thresholds = new(
            FallbackTerminalRemainingDistance: fallbackTerminalRemainingDistance,
            ArrivalRemainingDistance: 0.03f,
            MaximumPauseRouteSpeed: Math.Max(0.005f, minimumPositiveRouteSpeed * 0.20f),
            MinimumPauseTicks: 3,
            FootHopPlanarStep: Math.Max(0.01f, maximumFootStep * 1.50f),
            MaterialActorPlanarStep: Math.Max(0.01f, maximumActorStep * 1.25f),
            MaterialRemainingDistanceDrop: Math.Max(0.005f, maximumRemainingDistanceDrop * 1.25f),
            RouteSpeedDiscontinuity: Math.Max(0.02f, maximumRouteSpeed * 1.25f),
            RouteAccelerationDiscontinuity: Math.Max(0.25f, maximumPositiveRouteAcceleration * 1.25f),
            MaximumTicksBetweenFeet: 1,
            MaximumTicksFromHopToTerminalResolution: 4);
        return new ExitStruggleCalibration(
            minimumPositiveRouteSpeed,
            maximumFootStep,
            maximumActorStep,
            maximumRemainingDistanceDrop,
            maximumRouteSpeed,
            maximumPositiveRouteAcceleration,
            approachRemainingMaximum,
            thresholds);
    }

    public static ExitStruggleResult Evaluate(
        string caseName,
        IReadOnlyList<NPCFluiditySample> samples,
        ExitStruggleThresholds thresholds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseName);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(thresholds);

        int approachIndex = IndexOf(samples, static sample => sample.TerminalApproachActive);
        int terminalStart = FindTerminalStart(samples, thresholds.FallbackTerminalRemainingDistance);
        if (terminalStart < 0)
        {
            return new ExitStruggleResult(caseName, approachIndex >= 0, samples.Count, [], [], [], null, samples, thresholds);
        }

        List<ExitStrugglePause> pauses = FindPauses(samples, terminalStart, thresholds);
        List<ExitStrugglePairedHop> pairedHops = FindPairedHops(samples, terminalStart, thresholds);
        List<ExitStruggleTerminalResolution> terminalResolutions = FindTerminalResolutions(samples, terminalStart, thresholds);
        ExitStruggleEpisode? episode = FindEpisode(pairedHops, terminalResolutions, thresholds);
        return new ExitStruggleResult(
            caseName,
            approachIndex >= 0,
            terminalStart,
            pauses,
            pairedHops,
            terminalResolutions,
            episode,
            samples,
            thresholds);
    }

    private static List<ExitStrugglePause> FindPauses(
        IReadOnlyList<NPCFluiditySample> samples,
        int terminalStart,
        ExitStruggleThresholds thresholds)
    {
        List<ExitStrugglePause> pauses = [];
        int pauseStart = -1;
        float minimumRemaining = float.PositiveInfinity;
        for (int index = terminalStart; index < samples.Count; index++)
        {
            NPCFluiditySample sample = samples[index];
            bool paused = IsTravelOrExit(sample)
                && sample.RemainingRouteDistance > thresholds.ArrivalRemainingDistance
                && sample.RealisedRouteLongitudinal / Tick(samples, index) <= thresholds.MaximumPauseRouteSpeed;
            if (paused)
            {
                pauseStart = pauseStart < 0 ? index : pauseStart;
                minimumRemaining = Math.Min(minimumRemaining, sample.RemainingRouteDistance);
                continue;
            }

            AddPauseIfLongEnough(pauses, pauseStart, index - 1, minimumRemaining, thresholds);
            pauseStart = -1;
            minimumRemaining = float.PositiveInfinity;
        }

        AddPauseIfLongEnough(pauses, pauseStart, samples.Count - 1, minimumRemaining, thresholds);
        return pauses;
    }

    private static void AddPauseIfLongEnough(
        List<ExitStrugglePause> pauses,
        int start,
        int end,
        float minimumRemaining,
        ExitStruggleThresholds thresholds)
    {
        if (start >= 0 && end - start + 1 >= thresholds.MinimumPauseTicks)
        {
            pauses.Add(new ExitStrugglePause(start, end, minimumRemaining));
        }
    }

    private static List<ExitStrugglePairedHop> FindPairedHops(
        IReadOnlyList<NPCFluiditySample> samples,
        int terminalStart,
        ExitStruggleThresholds thresholds)
    {
        List<(int Index, float Step)> leftCrossings = [];
        List<(int Index, float Step)> rightCrossings = [];
        for (int index = Math.Max(terminalStart + 1, 1); index < samples.Count; index++)
        {
            float leftPrevious = FootStep(samples, index - 1, left: true);
            float rightPrevious = FootStep(samples, index - 1, left: false);
            float left = FootStep(samples, index, left: true);
            float right = FootStep(samples, index, left: false);
            if (leftPrevious <= thresholds.FootHopPlanarStep && left > thresholds.FootHopPlanarStep)
            {
                leftCrossings.Add((index, left));
            }

            if (rightPrevious <= thresholds.FootHopPlanarStep && right > thresholds.FootHopPlanarStep)
            {
                rightCrossings.Add((index, right));
            }
        }

        List<ExitStrugglePairedHop> paired = [];
        foreach ((int leftIndex, float leftStep) in leftCrossings)
        {
            foreach ((int rightIndex, float rightStep) in rightCrossings)
            {
                int latestCrossing = Math.Max(leftIndex, rightIndex);
                if (Math.Abs(leftIndex - rightIndex) > thresholds.MaximumTicksBetweenFeet
                    || !HasTerminalPairedHopContext(samples, terminalStart, leftIndex, rightIndex, thresholds)
                    // Both feet must still be moving together at the second crossing. This rejects a normal
                    // alternating stride whose independent rising crossings happen to be adjacent.
                    || FootStep(samples, latestCrossing, left: true) <= thresholds.FootHopPlanarStep
                    || FootStep(samples, latestCrossing, left: false) <= thresholds.FootHopPlanarStep)
                {
                    continue;
                }

                paired.Add(new ExitStrugglePairedHop(leftIndex, rightIndex, leftStep, rightStep));
                break;
            }
        }

        return paired;
    }

    private static bool HasTerminalPairedHopContext(
        IReadOnlyList<NPCFluiditySample> samples,
        int terminalStart,
        int leftCrossing,
        int rightCrossing,
        ExitStruggleThresholds thresholds)
    {
        int onset = Math.Min(leftCrossing, rightCrossing);
        int latestCrossing = Math.Max(leftCrossing, rightCrossing);
        int preHop = onset - 1;
        if (preHop < terminalStart)
        {
            return false;
        }

        NPCFluiditySample previous = samples[preHop];
        NPCFluiditySample left = samples[leftCrossing];
        NPCFluiditySample right = samples[rightCrossing];
        if (!IsTravelOrExit(previous)
            || !IsTravelOrExit(left)
            || !IsTravelOrExit(right)
            || previous.RemainingRouteDistance <= thresholds.ArrivalRemainingDistance
            || left.RemainingRouteDistance <= thresholds.ArrivalRemainingDistance
            || right.RemainingRouteDistance <= thresholds.ArrivalRemainingDistance)
        {
            return false;
        }

        for (int index = 0; index <= latestCrossing; index++)
        {
            if (IsComplete(samples[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static List<ExitStruggleTerminalResolution> FindTerminalResolutions(
        IReadOnlyList<NPCFluiditySample> samples,
        int terminalStart,
        ExitStruggleThresholds thresholds)
    {
        List<ExitStruggleTerminalResolution> resolutions = [];
        for (int index = Math.Max(terminalStart + 1, 1); index < samples.Count; index++)
        {
            NPCFluiditySample previous = samples[index - 1];
            NPCFluiditySample current = samples[index];
            float tick = Tick(samples, index);
            float routeSpeed = Math.Max(0f, current.RealisedRouteLongitudinal / tick);
            float previousRouteSpeed = Math.Max(0f, previous.RealisedRouteLongitudinal / tick);
            float routeAcceleration = (routeSpeed - previousRouteSpeed) / tick;
            float actorPlanarStep = PlanarMagnitude(current.RealisedActorStepWorld);
            float remainingDistanceDrop = previous.RemainingRouteDistance - current.RemainingRouteDistance;
            bool arrivalOrCompletion = current.RemainingRouteDistance <= thresholds.ArrivalRemainingDistance || IsComplete(current);
            bool materialArrivalOrCompletion = arrivalOrCompletion
                && (actorPlanarStep >= thresholds.MaterialActorPlanarStep
                    || remainingDistanceDrop >= thresholds.MaterialRemainingDistanceDrop);
            bool positiveRouteDiscontinuity = routeSpeed >= thresholds.RouteSpeedDiscontinuity
                && routeAcceleration >= thresholds.RouteAccelerationDiscontinuity;
            if (materialArrivalOrCompletion || positiveRouteDiscontinuity)
            {
                resolutions.Add(new ExitStruggleTerminalResolution(
                    index,
                    arrivalOrCompletion,
                    actorPlanarStep,
                    remainingDistanceDrop,
                    routeSpeed,
                    routeAcceleration));
            }
        }

        return resolutions;
    }

    private static ExitStruggleEpisode? FindEpisode(
        IReadOnlyList<ExitStrugglePairedHop> pairedHops,
        IReadOnlyList<ExitStruggleTerminalResolution> terminalResolutions,
        ExitStruggleThresholds thresholds)
    {
        foreach (ExitStrugglePairedHop hop in pairedHops)
        {
            ExitStruggleTerminalResolution? terminalResolution = terminalResolutions.FirstOrDefault(candidate =>
                candidate.SampleIndex >= hop.OnsetSampleIndex
                && candidate.SampleIndex <= hop.OnsetSampleIndex + thresholds.MaximumTicksFromHopToTerminalResolution);
            if (terminalResolution is not null)
            {
                return new ExitStruggleEpisode(hop, terminalResolution);
            }
        }

        return null;
    }

    private static bool IsTravelOrExit(NPCFluiditySample sample)
        => sample.ProgrammeStage is "SustainedTravel" or "Exit"
            || sample.GraphNode == "NPCTravel";

    private static bool IsComplete(NPCFluiditySample sample) => sample.ProgrammeStage == "Complete";

    private static int FindTerminalStart(IReadOnlyList<NPCFluiditySample> samples, float fallbackTerminalRemainingDistance)
    {
        int approachIndex = IndexOf(samples, static sample => sample.TerminalApproachActive);
        return approachIndex >= 0
            ? approachIndex
            : IndexOf(samples, sample => sample.RemainingRouteDistance <= fallbackTerminalRemainingDistance);
    }

    private static int IndexOf(IReadOnlyList<NPCFluiditySample> samples, Func<NPCFluiditySample, bool> predicate)
    {
        for (int index = 0; index < samples.Count; index++)
        {
            if (predicate(samples[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static float Tick(IReadOnlyList<NPCFluiditySample> samples, int index)
        => index > 0 ? Math.Max(samples[index].TimeSeconds - samples[index - 1].TimeSeconds, float.Epsilon) : 1f / 60f;

    private static float FootStep(IReadOnlyList<NPCFluiditySample> samples, int index, bool left)
        => index <= 0
            ? 0f
            : left
            ? PlanarStep(samples[index].LeftFootWorld, samples[index - 1].LeftFootWorld)
            : PlanarStep(samples[index].RightFootWorld, samples[index - 1].RightFootWorld);

    private static float PlanarStep(Vector3 current, Vector3 previous)
        => new Vector2(current.X - previous.X, current.Z - previous.Z).Length();

    private static float PlanarMagnitude(Vector3 value) => new Vector2(value.X, value.Z).Length();
}
