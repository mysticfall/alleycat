using System.Globalization;
using System.Text;
using AlleyCat.IK;
using AlleyCat.Rigging;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

public enum NPCFootMotionSignal
{
    Position,
    VelocityVectorChange,
}

public readonly record struct NPCFootMotionPose(
    Transform3D RootWorld,
    Transform3D HipsWorld,
    Transform3D LeftFootWorld,
    Transform3D RightFootWorld,
    FootPosePointSet LeftPointsWorld,
    FootPosePointSet RightPointsWorld)
{
    public Transform3D Point(LimbSide side, FootPosePoint point)
        => (side == LimbSide.Left ? LeftPointsWorld : RightPointsWorld).Get(point);
}

public readonly record struct NPCFootContactEvidence(
    ContactLockLifecycle Lifecycle,
    string Transition,
    bool HasAnchor,
    Transform3D Anchor,
    bool ResolutionPublished,
    bool ContactPublished);

public readonly record struct NPCFootGraphEvidence(
    string State,
    string FadingFromState,
    float StateClock,
    float FadingFromClock,
    float NormalisedPhase,
    float PlaybackRate,
    string Role,
    string Clip,
    string ProgrammeStage,
    string Transition,
    string ReleaseLane,
    string ReleaseEvent,
    string SelectedExitRole,
    string FadingFromClip = "",
    float LinearFadeWeight = 1f,
    float LinearFadeDurationSeconds = 0f);

/// <summary>
/// One same-callback post-IK observation. Pre-IK and contact channels are optional attribution only.
/// <paramref name="ObserverTick"/> is owned by the final Skeleton3D callback that supplied <paramref name="PostIK"/>.
/// </summary>
public readonly record struct NPCFootMotionContinuitySample(
    ulong ObserverTick,
    float TimeSeconds,
    Transform3D ActorWorld,
    NPCFootMotionPose PostIK,
    NPCFootMotionPose? PreIK,
    NPCFootGraphEvidence Graph,
    NPCFootContactEvidence? LeftContact,
    NPCFootContactEvidence? RightContact,
    ulong SourceTick = 0);

public readonly record struct NPCFootBaselineKey(LimbSide Side, FootPosePoint Point, NPCFootMotionSignal Signal);

public sealed record NPCFootMotionBand(
    NPCFootBaselineKey Key,
    float MaximumOrdinary,
    float SafetyMargin,
    float MeasurementFloor,
    float LowReleaseFraction,
    float Low,
    float High,
    string Units);

public sealed record NPCFootRawClipSample(
    string Clip,
    float DurationSeconds,
    int Frame,
    float TimeSeconds,
    float Phase,
    FootPosePointSet Left,
    FootPosePointSet Right,
    NPCFootLegChainPose? LeftChain = null,
    NPCFootLegChainPose? RightChain = null);

public sealed record NPCFootMotionCalibration(
    IReadOnlyDictionary<string, IReadOnlyList<NPCFootRawClipSample>> Clips,
    IReadOnlyDictionary<NPCFootBaselineKey, NPCFootMotionBand> Bands,
    IReadOnlyList<string> Rejections)
{
    public bool IsValid => Rejections.Count == 0;

    public string BuildReport()
    {
        var report = new StringBuilder();
        _ = report.Append("rawClips=").Append(Clips.Count).Append("; bands=").Append(Bands.Count)
            .Append("; positionMargin=").Append(NPCFootMotionContinuityDetector.PositionSafetyMarginMetres)
            .Append("m; velocityChangeMargin=").Append(NPCFootMotionContinuityDetector.VelocityChangeSafetyMarginMetresPerSecond)
            .Append("m/s; lowFraction=").Append(NPCFootMotionContinuityDetector.LowReleaseFraction)
            .Append("; rejections=").Append(Rejections.Count).AppendLine();
        foreach (string rejection in Rejections)
        {
            _ = report.Append("REJECT ").AppendLine(rejection);
        }
        foreach (NPCFootMotionBand band in Bands.Values.OrderBy(value => value.Key.ToString(), StringComparer.Ordinal))
        {
            _ = report.Append("BAND ").Append(band.Key).Append(" rawMax=").Append(F(band.MaximumOrdinary))
                .Append(" margin=").Append(F(band.SafetyMargin)).Append(" floor=").Append(F(band.MeasurementFloor))
                .Append(" low=").Append(F(band.Low)).Append(" high=").Append(F(band.High))
                .Append(' ').Append(band.Units).AppendLine();
        }
        return report.ToString();
    }

    private static string F(float value) => value.ToString("G9", CultureInfo.InvariantCulture);
}

public sealed record NPCFootSignalEvidence(
    NPCFootMotionSignal Signal,
    ulong TriggerTick,
    float TriggerObserved,
    ulong PeakTick,
    float PeakObserved,
    float LowBand,
    float HighBand,
    string Units);

public sealed record NPCFootPointEvidence(
    FootPosePoint Point,
    NPCFootSignalEvidence? Position,
    NPCFootSignalEvidence? VelocityVectorChange);

public sealed record NPCFootMotionAttribution(
    float? PreIKStepMetres,
    float? PreviousContactCorrectionMetres,
    float? CurrentContactCorrectionMetres,
    string CausalClaim)
{
    public bool HasCausalClaim => !string.IsNullOrWhiteSpace(CausalClaim);
}

public sealed record NPCFootMotionEvent(
    LimbSide Side,
    ulong TriggerTick,
    ulong PeakTick,
    ulong EndTick,
    string ProgrammeStage,
    string Transition,
    string GraphState,
    float GraphPhase,
    IReadOnlyList<NPCFootPointEvidence> Points,
    NPCFootMotionAttribution Attribution)
{
    public float MaximumPositionStep => Points.Select(point => point.Position?.PeakObserved ?? 0f).DefaultIfEmpty().Max();
    public float MaximumVelocityVectorChange => Points.Select(point => point.VelocityVectorChange?.PeakObserved ?? 0f).DefaultIfEmpty().Max();
}

public sealed record NPCFootMotionContinuityResult(
    string CaseName,
    IReadOnlyList<NPCFootMotionContinuitySample> Samples,
    NPCFootMotionCalibration Calibration,
    IReadOnlyList<NPCFootMotionEvent> Events,
    IReadOnlyList<string> EvidenceFailures)
{
    public bool EvidenceValid => EvidenceFailures.Count == 0;

    public string BuildReport()
    {
        var report = new StringBuilder();
        _ = report.Append(CaseName).Append(": samples=").Append(Samples.Count).Append("; events=")
            .Append(Events.Count).Append("; evidenceFailures=").Append(EvidenceFailures.Count).AppendLine();
        foreach (string failure in EvidenceFailures)
        {
            _ = report.Append("EVIDENCE ").AppendLine(failure);
        }
        foreach (NPCFootMotionEvent item in Events)
        {
            _ = report.Append(item.TriggerTick).Append('-').Append(item.EndTick).Append(' ').Append(item.Side)
                .Append(" peak=").Append(item.PeakTick).Append(" graph=").Append(item.GraphState).Append('@')
                .Append(item.GraphPhase.ToString("G9", CultureInfo.InvariantCulture)).Append(" points=")
                .AppendJoin('|', item.Points.Select(point => point.Point)).Append(" step=")
                .Append(item.MaximumPositionStep.ToString("G9", CultureInfo.InvariantCulture)).Append("m change=")
                .Append(item.MaximumVelocityVectorChange.ToString("G9", CultureInfo.InvariantCulture)).Append("m/s attribution=")
                .Append(item.Attribution.PreIKStepMetres?.ToString("G9", CultureInfo.InvariantCulture) ?? "unavailable")
                .Append("m correction=")
                .Append(item.Attribution.PreviousContactCorrectionMetres?.ToString("G9", CultureInfo.InvariantCulture) ?? "unavailable")
                .Append("->")
                .Append(item.Attribution.CurrentContactCorrectionMetres?.ToString("G9", CultureInfo.InvariantCulture) ?? "unavailable")
                .AppendLine("m");
            foreach (NPCFootPointEvidence point in item.Points)
            {
                AppendSignal(report, point.Point, point.Position);
                AppendSignal(report, point.Point, point.VelocityVectorChange);
            }
        }
        return report.ToString();
    }

    private static void AppendSignal(StringBuilder report, FootPosePoint point, NPCFootSignalEvidence? signal)
    {
        if (signal is null)
        {
            return;
        }
        _ = report.Append("  ").Append(point).Append('/').Append(signal.Signal)
            .Append(" trigger=").Append(signal.TriggerTick).Append(':')
            .Append(signal.TriggerObserved.ToString("G9", CultureInfo.InvariantCulture))
            .Append(" peak=").Append(signal.PeakTick).Append(':')
            .Append(signal.PeakObserved.ToString("G9", CultureInfo.InvariantCulture))
            .Append(" bands=").Append(signal.LowBand.ToString("G9", CultureInfo.InvariantCulture))
            .Append('/').Append(signal.HighBand.ToString("G9", CultureInfo.InvariantCulture))
            .Append(' ').AppendLine(signal.Units);
    }
}

/// <summary>Test-only, route-agnostic post-IK visual foot-point pop detector.</summary>
public static class NPCFootMotionContinuityDetector
{
    public const float NativeRate = 60f;
    public const float PositionSafetyMarginMetres = 0.005f;
    public const float VelocityChangeSafetyMarginMetresPerSecond = 0.30f;
    public const float PositionMeasurementFloorMetres = 0.0005f;
    public const float VelocityChangeMeasurementFloorMetresPerSecond = 0.03f;
    public const float LowReleaseFraction = 0.50f;

    public static NPCFootMotionCalibration CreateCalibration(IReadOnlyList<NPCFootRawClipSample> samples)
    {
        List<string> rejections = [];
        var clips = new Dictionary<string, IReadOnlyList<NPCFootRawClipSample>>(StringComparer.Ordinal);
        foreach (IGrouping<string, NPCFootRawClipSample> group in samples.GroupBy(sample => sample.Clip, StringComparer.Ordinal))
        {
            NPCFootRawClipSample[] ordered = [.. group.OrderBy(sample => sample.Frame)];
            bool contiguous = ordered.Length >= 3 && ordered[0].Frame == 0
                && ordered.Zip(ordered.Skip(1), static (before, after) => after.Frame == before.Frame + 1).All(static value => value);
            if (!contiguous || ordered.Any(sample => !Finite(sample.Left) || !Finite(sample.Right)))
            {
                rejections.Add($"Raw clip '{group.Key}' is missing contiguous finite 60 Hz samples.");
                continue;
            }
            clips.Add(group.Key, ordered);
        }

        var maxima = new Dictionary<NPCFootBaselineKey, float>();
        foreach (IReadOnlyList<NPCFootRawClipSample> clip in clips.Values)
        {
            foreach (LimbSide side in Enum.GetValues<LimbSide>())
            {
                foreach (FootPosePoint point in Enum.GetValues<FootPosePoint>())
                {
                    Vector3 previousVelocity = Vector3.Zero;
                    for (int index = 1; index < clip.Count; index++)
                    {
                        Vector3 step = Point(clip[index], side, point).Origin - Point(clip[index - 1], side, point).Origin;
                        RetainMaximum(maxima, new(side, point, NPCFootMotionSignal.Position), step.Length());
                        Vector3 velocity = step * NativeRate;
                        if (index > 1)
                        {
                            RetainMaximum(maxima, new(side, point, NPCFootMotionSignal.VelocityVectorChange),
                                velocity.DistanceTo(previousVelocity));
                        }
                        previousVelocity = velocity;
                    }
                }
            }
        }

        var bands = new Dictionary<NPCFootBaselineKey, NPCFootMotionBand>();
        foreach (LimbSide side in Enum.GetValues<LimbSide>())
        {
            foreach (FootPosePoint point in Enum.GetValues<FootPosePoint>())
            {
                foreach (NPCFootMotionSignal signal in Enum.GetValues<NPCFootMotionSignal>())
                {
                    NPCFootBaselineKey key = new(side, point, signal);
                    if (!maxima.TryGetValue(key, out float maximum))
                    {
                        rejections.Add($"Raw/native maximum unavailable for {key}.");
                        continue;
                    }
                    float margin = signal == NPCFootMotionSignal.Position
                        ? PositionSafetyMarginMetres : VelocityChangeSafetyMarginMetresPerSecond;
                    float floor = signal == NPCFootMotionSignal.Position
                        ? PositionMeasurementFloorMetres : VelocityChangeMeasurementFloorMetresPerSecond;
                    float high = Math.Max(floor, maximum + margin);
                    float low = high * LowReleaseFraction;
                    bands.Add(key, new(key, maximum, margin, floor, LowReleaseFraction, low, high,
                        signal == NPCFootMotionSignal.Position ? "m" : "m/s"));
                }
            }
        }
        return new(clips, bands, rejections);
    }

    public static NPCFootMotionContinuityResult Evaluate(
        string caseName,
        IReadOnlyList<NPCFootMotionContinuitySample>? samples,
        NPCFootMotionCalibration calibration)
    {
        IReadOnlyList<NPCFootMotionContinuitySample> retained = samples ?? [];
        List<string> failures = ValidatePrimaryEvidence(retained, calibration);
        if (failures.Count > 0)
        {
            return new(caseName, retained, calibration, [], failures);
        }

        List<NPCFootMotionEvent> events = [];
        foreach (LimbSide side in Enum.GetValues<LimbSide>())
        {
            EvaluateFoot(retained, calibration, side, events);
        }
        return new(caseName, retained, calibration, [.. events.OrderBy(item => item.TriggerTick).ThenBy(item => item.Side)], []);
    }

    private static void EvaluateFoot(
        IReadOnlyList<NPCFootMotionContinuitySample> samples,
        NPCFootMotionCalibration calibration,
        LimbSide side,
        List<NPCFootMotionEvent> events)
    {
        var previousVelocities = new Dictionary<FootPosePoint, Vector3>();
        ActiveEvent? active = null;
        for (int index = 1; index < samples.Count; index++)
        {
            var metrics = new Dictionary<FootPosePoint, PointMetric>();
            bool anyHigh = false;
            bool allLow = true;
            float delta = samples[index].TimeSeconds - samples[index - 1].TimeSeconds;
            foreach (FootPosePoint point in Enum.GetValues<FootPosePoint>())
            {
                Vector3 step = ActorResidualStep(samples[index - 1], samples[index], side, point);
                Vector3 velocity = step / delta;
                float velocityChange = index > 1 && previousVelocities.TryGetValue(point, out Vector3 previous)
                    ? velocity.DistanceTo(previous) : 0f;
                previousVelocities[point] = velocity;
                NPCFootMotionBand positionBand = calibration.Bands[new(side, point, NPCFootMotionSignal.Position)];
                NPCFootMotionBand velocityBand = calibration.Bands[new(side, point, NPCFootMotionSignal.VelocityVectorChange)];
                var metric = new PointMetric(step.Length(), velocityChange, positionBand, velocityBand);
                metrics.Add(point, metric);
                anyHigh |= metric.Position > positionBand.High || metric.VelocityChange > velocityBand.High;
                allLow &= metric.Position < positionBand.Low && metric.VelocityChange < velocityBand.Low;
            }

            if (active is null && anyHigh)
            {
                active = new(side, index, samples[index]);
            }
            if (active is null)
            {
                continue;
            }

            active.Observe(samples[index].ObserverTick, metrics, anyHigh);
            active.BelowLowCount = allLow ? active.BelowLowCount + 1 : 0;
            if (active.BelowLowCount >= 2)
            {
                events.Add(active.Build(samples, index));
                active = null;
            }
        }
        if (active is not null)
        {
            events.Add(active.Build(samples, samples.Count - 1));
        }
    }

    private static List<string> ValidatePrimaryEvidence(
        IReadOnlyList<NPCFootMotionContinuitySample> samples,
        NPCFootMotionCalibration calibration)
    {
        List<string> failures = [];
        if (!calibration.IsValid || calibration.Bands.Count != 20)
        {
            failures.Add($"Raw/native calibration unavailable: {string.Join(" | ", calibration.Rejections)}");
        }
        if (samples.Count < 3)
        {
            failures.Add($"At least three post-IK observations are required; observed {samples.Count}.");
            return failures;
        }
        for (int index = 0; index < samples.Count; index++)
        {
            NPCFootMotionContinuitySample sample = samples[index];
            if (!Finite(sample.ActorWorld) || !Finite(sample.PostIK))
            {
                failures.Add($"Non-finite primary post-IK observation at row {index}.");
            }
            if (index == 0)
            {
                continue;
            }
            ulong expectedTick = samples[index - 1].ObserverTick + 1;
            float delta = sample.TimeSeconds - samples[index - 1].TimeSeconds;
            if (sample.ObserverTick != expectedTick || !float.IsFinite(delta) || delta <= 0f)
            {
                failures.Add($"Post-IK observer continuity failed at row {index}: expected tick {expectedTick}, observed {sample.ObserverTick}; delta={delta:G9}.");
            }
        }
        return failures;
    }

    private static Vector3 ActorResidualStep(
        NPCFootMotionContinuitySample before,
        NPCFootMotionContinuitySample current,
        LimbSide side,
        FootPosePoint point)
    {
        Transform3D prior = before.PostIK.Point(side, point);
        Transform3D now = current.PostIK.Point(side, point);
        Transform3D rigidTransport = current.ActorWorld * before.ActorWorld.AffineInverse();
        return now.Origin - (rigidTransport * prior).Origin;
    }

    private static NPCFootMotionAttribution BuildAttribution(
        IReadOnlyList<NPCFootMotionContinuitySample> samples,
        int triggerIndex,
        LimbSide side,
        FootPosePoint representativePoint)
    {
        NPCFootMotionContinuitySample before = samples[triggerIndex - 1];
        NPCFootMotionContinuitySample current = samples[triggerIndex];
        float? preIKStep = before.PreIK is NPCFootMotionPose priorPre && current.PreIK is NPCFootMotionPose currentPre
            ? (currentPre.Point(side, representativePoint).Origin
                - (current.ActorWorld * before.ActorWorld.AffineInverse() * priorPre.Point(side, representativePoint)).Origin).Length()
            : null;
        float? previousCorrection = before.PreIK is NPCFootMotionPose beforePre
            ? before.PostIK.Point(side, representativePoint).Origin.DistanceTo(beforePre.Point(side, representativePoint).Origin)
            : null;
        float? currentCorrection = current.PreIK is NPCFootMotionPose nowPre
            ? current.PostIK.Point(side, representativePoint).Origin.DistanceTo(nowPre.Point(side, representativePoint).Origin)
            : null;
        NPCFootContactEvidence? contact = side == LimbSide.Left ? current.LeftContact : current.RightContact;
        bool contactCanAttribute = contact is NPCFootContactEvidence evidence
            && (evidence.Lifecycle != ContactLockLifecycle.None || evidence.ResolutionPublished || evidence.ContactPublished);
        string claim = preIKStep.HasValue && previousCorrection.HasValue && currentCorrection.HasValue && contactCanAttribute
            ? "post-IK discontinuity with available pre-IK/contact attribution"
            : string.Empty;
        return new(preIKStep, previousCorrection, currentCorrection, claim);
    }

    private static void RetainMaximum(Dictionary<NPCFootBaselineKey, float> maxima, NPCFootBaselineKey key, float value)
    {
        if (float.IsFinite(value) && (!maxima.TryGetValue(key, out float current) || value > current))
        {
            maxima[key] = value;
        }
    }

    private static Transform3D Point(NPCFootRawClipSample sample, LimbSide side, FootPosePoint point)
        => (side == LimbSide.Left ? sample.Left : sample.Right).Get(point);

    private static bool Finite(NPCFootMotionPose pose)
        => Finite(pose.RootWorld) && Finite(pose.HipsWorld)
            && Enum.GetValues<FootPosePoint>().All(point => Finite(pose.LeftPointsWorld.Get(point)) && Finite(pose.RightPointsWorld.Get(point)));

    private static bool Finite(FootPosePointSet points)
        => Enum.GetValues<FootPosePoint>().All(point => Finite(points.Get(point)));

    private static bool Finite(Transform3D transform)
        => Finite(transform.Origin) && Finite(transform.Basis.X) && Finite(transform.Basis.Y) && Finite(transform.Basis.Z);

    private static bool Finite(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private sealed class ActiveEvent(LimbSide side, int triggerIndex, NPCFootMotionContinuitySample triggerSample)
    {
        private readonly Dictionary<FootPosePoint, MutablePointEvidence> _points = [];
        private ulong _peakTick = triggerSample.ObserverTick;
        private float _peakRatio;

        public int BelowLowCount
        {
            get; set;
        }

        public void Observe(ulong tick, IReadOnlyDictionary<FootPosePoint, PointMetric> metrics, bool anyHigh)
        {
            foreach ((FootPosePoint point, PointMetric metric) in metrics)
            {
                bool positionHigh = metric.Position > metric.PositionBand.High;
                bool velocityHigh = metric.VelocityChange > metric.VelocityBand.High;
                if (!_points.TryGetValue(point, out MutablePointEvidence? evidence))
                {
                    if (!positionHigh && !velocityHigh)
                    {
                        continue;
                    }
                    evidence = new(point);
                    _points.Add(point, evidence);
                }
                evidence.Observe(tick, metric, positionHigh, velocityHigh);
                float ratio = Math.Max(metric.Position / metric.PositionBand.High, metric.VelocityChange / metric.VelocityBand.High);
                if (ratio > _peakRatio)
                {
                    _peakRatio = ratio;
                    _peakTick = tick;
                }
            }
            if (anyHigh)
            {
                BelowLowCount = 0;
            }
        }

        public NPCFootMotionEvent Build(IReadOnlyList<NPCFootMotionContinuitySample> samples, int endIndex)
        {
            NPCFootPointEvidence[] points = [.. _points.Values.OrderBy(value => value.Point).Select(value => value.Build())];
            FootPosePoint representative = points.OrderByDescending(point => point.Position?.PeakObserved ?? 0f).First().Point;
            return new(side, triggerSample.ObserverTick, _peakTick, samples[endIndex].ObserverTick,
                triggerSample.Graph.ProgrammeStage, triggerSample.Graph.Transition, triggerSample.Graph.State,
                triggerSample.Graph.NormalisedPhase, points,
                BuildAttribution(samples, triggerIndex, side, representative));
        }
    }

    private sealed class MutablePointEvidence(FootPosePoint point)
    {
        private MutableSignalEvidence? _position;
        private MutableSignalEvidence? _velocity;
        public FootPosePoint Point => point;

        public void Observe(ulong tick, PointMetric metric, bool positionHigh, bool velocityHigh)
        {
            if (positionHigh || _position is not null)
            {
                _position ??= new(NPCFootMotionSignal.Position, tick, metric.Position, metric.PositionBand);
                _position.Observe(tick, metric.Position);
            }
            if (velocityHigh || _velocity is not null)
            {
                _velocity ??= new(NPCFootMotionSignal.VelocityVectorChange, tick, metric.VelocityChange, metric.VelocityBand);
                _velocity.Observe(tick, metric.VelocityChange);
            }
        }

        public NPCFootPointEvidence Build() => new(point, _position?.Build(), _velocity?.Build());
    }

    private sealed class MutableSignalEvidence(
        NPCFootMotionSignal signal,
        ulong triggerTick,
        float triggerObserved,
        NPCFootMotionBand band)
    {
        private readonly NPCFootMotionSignal _signal = signal;
        private readonly ulong _triggerTick = triggerTick;
        private readonly float _triggerObserved = triggerObserved;
        private readonly NPCFootMotionBand _band = band;
        private ulong _peakTick = triggerTick;
        private float _peakObserved = triggerObserved;

        public void Observe(ulong tick, float observed)
        {
            if (observed > _peakObserved)
            {
                _peakObserved = observed;
                _peakTick = tick;
            }
        }

        public NPCFootSignalEvidence Build()
            => new(_signal, _triggerTick, _triggerObserved, _peakTick, _peakObserved, _band.Low, _band.High, _band.Units);
    }

    private readonly record struct PointMetric(
        float Position,
        float VelocityChange,
        NPCFootMotionBand PositionBand,
        NPCFootMotionBand VelocityBand);
}
