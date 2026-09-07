using System.Globalization;
using System.Text;
using AlleyCat.IK;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;

#pragma warning disable CS1591, IDE0046

namespace AlleyCat.IntegrationTests.Support;

public readonly record struct NPCFootLegChainPose(
    Transform3D Hips,
    Transform3D UpperLeg,
    Transform3D LowerLeg,
    Transform3D Foot,
    Transform3D Toe);

public readonly record struct NPCTerminalSlideMovement(
    Vector3 AuthoredRoot,
    Vector3 Synthetic,
    Vector3 Contact,
    Vector3 CollisionTravel,
    Vector3 CollisionRemainder,
    Vector3 Realised);

public readonly record struct NPCTerminalSlideSample(
    NPCFootMotionContinuitySample Motion,
    NPCFootLegChainPose LeftChain,
    NPCFootLegChainPose RightChain,
    NPCTerminalSlideMovement Movement,
    Vector3 Destination,
    Vector3 DestinationForward,
    float FinitePlayPosition,
    float FinitePlayLength);

public enum NPCTerminalSlideEpisodeKind
{
    Contact,
    StationaryConfirmation,
    Settlement,
    IdleFade,
}

public enum NPCTerminalSlideCause
{
    None,
    AuthoredFootArticulation,
    ContactLockTargetDriftOrFailure,
    StoppedPoseSyntheticSlide,
    SettlementMovement,
    IdleCrossfadePoseMotion,
    CollisionMismatch,
    ActorCommonModeTranslationOverQuietPose,
    Unclassified,
}

public sealed record NPCTerminalSlideCalibration(
    float PlantedPerTickBoundMetres,
    float ContactEpisodeBoundMetres,
    float StationaryMovementToleranceMetres,
    IReadOnlyList<string> Notes)
{
    public bool IsValid => PlantedPerTickBoundMetres > 0f
        && ContactEpisodeBoundMetres > 0f
        && StationaryMovementToleranceMetres > 0f;

    public string BuildReport()
        => string.Create(CultureInfo.InvariantCulture,
            $"plantedPerTick={PlantedPerTickBoundMetres:F9}m; stanceEpisode={ContactEpisodeBoundMetres:F9}m; stationaryMovement={StationaryMovementToleranceMetres:F9}m; optionalNotes={string.Join(" | ", Notes)}");
}

public sealed record NPCTerminalSlidePointMetrics(
    FootPosePoint Point,
    float WorldPathMetres,
    float WorldNetMetres,
    float ActorResidualPathMetres,
    float ActorResidualNetMetres,
    float MaximumOneTickDriftMetres,
    float MaximumSpeedMetresPerSecond);

public sealed record NPCTerminalSlideAngularMetrics(
    float HipsPathRadians,
    float UpperLegPathRadians,
    float LowerLegPathRadians,
    float FootPathRadians,
    float ToePathRadians,
    float MaximumOneTickRadians)
{
    public float TotalPathRadians => HipsPathRadians + UpperLegPathRadians + LowerLegPathRadians + FootPathRadians + ToePathRadians;
}

public sealed record NPCTerminalSlideEpisode(
    LimbSide Side,
    NPCTerminalSlideEpisodeKind Kind,
    ulong StartTick,
    ulong EndTick,
    ulong PeakTick,
    NPCFootGraphEvidence StartGraph,
    NPCFootGraphEvidence EndGraph,
    NPCFootGraphEvidence PeakGraph,
    IReadOnlyList<NPCTerminalSlidePointMetrics> Points,
    Vector3 ActorDisplacement,
    float ActorForwardComponentMetres,
    float ActorRightComponentMetres,
    float DestinationComponentMetres,
    Vector3 AuthoredRootSum,
    Vector3 SyntheticSum,
    Vector3 ContactSum,
    Vector3 CollisionTravelSum,
    Vector3 CollisionRemainderSum,
    float ContactAnchorPathMetres,
    NPCTerminalSlideAngularMetrics Angles,
    float PreIKWorldPathMetres,
    float PostMinusPreIKPathMetres,
    bool VisibleSlide,
    NPCTerminalSlideCause Cause,
    string CausalEvidence);

public sealed record NPCTerminalSlideResult(
    string CaseName,
    NPCTerminalSlideCalibration Calibration,
    IReadOnlyList<NPCTerminalSlideEpisode> Episodes,
    IReadOnlyList<string> EvidenceFailures)
{
    public bool EvidenceValid => EvidenceFailures.Count == 0;

    public string BuildReport()
    {
        var text = new StringBuilder();
        _ = text.Append(CaseName).Append(": episodes=").Append(Episodes.Count).Append("; evidence=")
            .Append(EvidenceFailures.Count).AppendLine().AppendLine(Calibration.BuildReport());
        foreach (string failure in EvidenceFailures)
        {
            _ = text.Append("EVIDENCE ").AppendLine(failure);
        }
        foreach (NPCTerminalSlideEpisode episode in Episodes)
        {
            _ = text.Append(episode.StartTick).Append('-').Append(episode.EndTick).Append(' ').Append(episode.Side)
                .Append('/').Append(episode.Kind).Append(" peak=").Append(episode.PeakTick).Append(" graph=")
                .Append(episode.StartGraph.State).Append('/').Append(episode.StartGraph.ProgrammeStage).Append('@')
                .Append(episode.StartGraph.NormalisedPhase.ToString("G9", CultureInfo.InvariantCulture))
                .Append(" fade=").Append(episode.StartGraph.LinearFadeWeight.ToString("G9", CultureInfo.InvariantCulture))
                .Append(" slide=").Append(episode.VisibleSlide).Append(" cause=").Append(episode.Cause)
                .Append(" actor=").Append(episode.ActorDisplacement).Append(" root/synthetic/contact/collision=")
                .Append(episode.AuthoredRootSum).Append('/').Append(episode.SyntheticSum).Append('/')
                .Append(episode.ContactSum).Append('/').Append(episode.CollisionTravelSum).AppendLine();
            _ = text.Append("  endGraph=").Append(episode.EndGraph.State).Append('/').Append(episode.EndGraph.ProgrammeStage)
                .Append('@').Append(F(episode.EndGraph.NormalisedPhase)).Append(" fade=").Append(F(episode.EndGraph.LinearFadeWeight))
                .Append(" peakGraph=").Append(episode.PeakGraph.State).Append('@').Append(F(episode.PeakGraph.NormalisedPhase))
                .Append(" actor destination/forward/right=").Append(F(episode.DestinationComponentMetres)).Append('/')
                .Append(F(episode.ActorForwardComponentMetres)).Append('/').Append(F(episode.ActorRightComponentMetres))
                .Append("m angles hips/thigh/shin/foot/toe/max=").Append(F(episode.Angles.HipsPathRadians)).Append('/')
                .Append(F(episode.Angles.UpperLegPathRadians)).Append('/').Append(F(episode.Angles.LowerLegPathRadians)).Append('/')
                .Append(F(episode.Angles.FootPathRadians)).Append('/').Append(F(episode.Angles.ToePathRadians)).Append('/')
                .Append(F(episode.Angles.MaximumOneTickRadians)).Append("rad preIK/correction/anchor=")
                .Append(F(episode.PreIKWorldPathMetres)).Append('/').Append(F(episode.PostMinusPreIKPathMetres)).Append('/')
                .Append(F(episode.ContactAnchorPathMetres)).AppendLine("m");
            foreach (NPCTerminalSlidePointMetrics point in episode.Points)
            {
                _ = text.Append("  ").Append(point.Point).Append(" worldPath/net=").Append(F(point.WorldPathMetres))
                    .Append('/').Append(F(point.WorldNetMetres)).Append("m residualPath/net=")
                    .Append(F(point.ActorResidualPathMetres)).Append('/').Append(F(point.ActorResidualNetMetres))
                    .Append("m maxTick/speed=").Append(F(point.MaximumOneTickDriftMetres)).Append('/')
                    .Append(F(point.MaximumSpeedMetresPerSecond)).AppendLine("m/s");
            }
        }
        return text.ToString();
    }

    private static string F(float value) => value.ToString("G9", CultureInfo.InvariantCulture);
}

/// <summary>Diagnostic-only accumulated contact-patch slide detector. It does not participate in production.</summary>
public static class NPCTerminalSlideDetector
{
    public const float AuthoritativePlantedPerTickBoundMetres = 0.008f;
    public const float AuthoritativeStanceEpisodeBoundMetres = 0.45f;
    public const float NavigationNumericalMovementToleranceMetres = NPCAnimationProgramme.NumericalConvergenceThreshold;
    private static readonly FootPosePoint[] _contactPatch =
        [FootPosePoint.HeelSole, FootPosePoint.Midsole, FootPosePoint.Ball, FootPosePoint.ToeTip];

    public static NPCTerminalSlideCalibration CreateCalibration(IReadOnlyList<NPCFootRawClipSample> raw)
    {
        List<string> notes = [];
        NPCFootRawClipSample[] endpoints = [.. raw
            .Where(sample => sample.Clip.Contains("f2i_", StringComparison.Ordinal) && sample.Phase >= 0.99999f)];
        if (endpoints.Length == 0)
        {
            notes.Add("Optional raw finite-endpoint sample is unavailable; objective contact/stationary windows remain enabled.");
        }
        return new(
            AuthoritativePlantedPerTickBoundMetres,
            AuthoritativeStanceEpisodeBoundMetres,
            NavigationNumericalMovementToleranceMetres,
            notes);
    }

    public static NPCTerminalSlideResult Evaluate(
        string caseName,
        IReadOnlyList<NPCTerminalSlideSample>? samples,
        NPCTerminalSlideCalibration calibration)
    {
        IReadOnlyList<NPCTerminalSlideSample> retained = samples ?? [];
        List<string> failures = Validate(retained, calibration);
        if (failures.Count > 0)
        {
            return new(caseName, calibration, [], failures);
        }
        List<NPCTerminalSlideEpisode> episodes = [];
        foreach (LimbSide side in Enum.GetValues<LimbSide>())
        {
            AddContactEpisodes(retained, calibration, side, episodes);
            AddStationaryConfirmationEpisodes(retained, side, episodes);
            AddStageEpisodes(retained, side, NPCTerminalSlideEpisodeKind.Settlement,
                sample => sample.Motion.Graph.ProgrammeStage == "Settlement", episodes);
            AddStageEpisodes(retained, side, NPCTerminalSlideEpisodeKind.IdleFade,
                sample => sample.Motion.Graph.State == "NPCIdle" && sample.Motion.Graph.FadingFromClip.Length > 0, episodes);
        }
        return new(caseName, calibration,
            [.. episodes.OrderBy(value => value.StartTick).ThenBy(value => value.Side).ThenBy(value => value.Kind)], []);
    }

    private static void AddContactEpisodes(IReadOnlyList<NPCTerminalSlideSample> samples, NPCTerminalSlideCalibration calibration,
        LimbSide side, List<NPCTerminalSlideEpisode> episodes)
        => AddRuns(samples, side, NPCTerminalSlideEpisodeKind.Contact,
            sample => Contact(sample.Motion, side) is { Lifecycle: ContactLockLifecycle.Acquire or ContactLockLifecycle.Support },
            calibration.ContactEpisodeBoundMetres, episodes);

    private static void AddStationaryConfirmationEpisodes(IReadOnlyList<NPCTerminalSlideSample> samples,
        LimbSide side, List<NPCTerminalSlideEpisode> episodes)
        => AddRuns(samples, side, NPCTerminalSlideEpisodeKind.StationaryConfirmation,
            (sample, index) => index > 0
                && sample.Motion.Graph.ProgrammeStage == "Exit"
                && sample.Motion.Graph.NormalisedPhase >= 0.99999f
                && sample.FinitePlayLength > 0f
                && sample.FinitePlayPosition >= sample.FinitePlayLength - NavigationNumericalMovementToleranceMetres
                && sample.Movement.AuthoredRoot.Length() <= NavigationNumericalMovementToleranceMetres
                && !IsAuthoredContact(sample.Motion, side)
                && MaximumActorResidualStep(samples[index - 1], sample, side) <= NavigationNumericalMovementToleranceMetres,
            NavigationNumericalMovementToleranceMetres, episodes);

    private static void AddStageEpisodes(IReadOnlyList<NPCTerminalSlideSample> samples, LimbSide side,
        NPCTerminalSlideEpisodeKind kind, Func<NPCTerminalSlideSample, bool> include,
        List<NPCTerminalSlideEpisode> episodes)
        => AddRuns(samples, side, kind, include,
            kind == NPCTerminalSlideEpisodeKind.IdleFade
                ? AuthoritativeStanceEpisodeBoundMetres
                : NavigationNumericalMovementToleranceMetres,
            episodes);

    private static void AddRuns(
        IReadOnlyList<NPCTerminalSlideSample> samples,
        LimbSide side,
        NPCTerminalSlideEpisodeKind kind,
        Func<NPCTerminalSlideSample, bool> include,
        float bound,
        List<NPCTerminalSlideEpisode> episodes)
        => AddRuns(samples, side, kind, (sample, _) => include(sample), bound, episodes);

    private static void AddRuns(
        IReadOnlyList<NPCTerminalSlideSample> samples,
        LimbSide side,
        NPCTerminalSlideEpisodeKind kind,
        Func<NPCTerminalSlideSample, int, bool> include,
        float bound,
        List<NPCTerminalSlideEpisode> episodes)
    {
        int start = -1;
        for (int index = 0; index <= samples.Count; index++)
        {
            bool inside = index < samples.Count && include(samples[index], index);
            if (inside && start < 0)
            {
                start = index;
            }
            if ((!inside || index == samples.Count) && start >= 0)
            {
                int end = index - 1;
                if (end > start)
                {
                    episodes.Add(BuildEpisode(samples, start, end, side, kind, bound));
                }
                start = -1;
            }
        }
    }

    private static NPCTerminalSlideEpisode BuildEpisode(IReadOnlyList<NPCTerminalSlideSample> samples, int start, int end,
        LimbSide side, NPCTerminalSlideEpisodeKind kind, float bound)
    {
        List<NPCTerminalSlidePointMetrics> points = [];
        ulong peakTick = samples[start].Motion.ObserverTick;
        float peakStep = 0f;
        foreach (FootPosePoint point in _contactPatch)
        {
            float worldPath = 0f;
            float residualPath = 0f;
            float maximumStep = 0f;
            for (int index = start + 1; index <= end; index++)
            {
                Vector3 before = samples[index - 1].Motion.PostIK.Point(side, point).Origin;
                Vector3 current = samples[index].Motion.PostIK.Point(side, point).Origin;
                float step = before.DistanceTo(current);
                worldPath += step;
                Transform3D transport = samples[index].Motion.ActorWorld * samples[index - 1].Motion.ActorWorld.AffineInverse();
                residualPath += current.DistanceTo((transport * samples[index - 1].Motion.PostIK.Point(side, point)).Origin);
                if (step > peakStep)
                {
                    peakStep = step;
                    peakTick = samples[index].Motion.ObserverTick;
                }
                maximumStep = Math.Max(maximumStep, step);
            }
            Vector3 first = samples[start].Motion.PostIK.Point(side, point).Origin;
            Vector3 last = samples[end].Motion.PostIK.Point(side, point).Origin;
            Transform3D fullTransport = samples[end].Motion.ActorWorld * samples[start].Motion.ActorWorld.AffineInverse();
            float duration = samples[end].Motion.TimeSeconds - samples[start].Motion.TimeSeconds;
            points.Add(new(point, worldPath, first.DistanceTo(last), residualPath,
                last.DistanceTo((fullTransport * samples[start].Motion.PostIK.Point(side, point)).Origin),
                maximumStep, maximumStep / Math.Max(duration / (end - start), float.Epsilon)));
        }

        Vector3 actor = samples[end].Motion.ActorWorld.Origin - samples[start].Motion.ActorWorld.Origin;
        Vector3 forward = samples[start].DestinationForward;
        forward.Y = 0f;
        forward = forward.LengthSquared() > 0f ? forward.Normalized() : Vector3.Zero;
        Vector3 destination = samples[start].Destination - samples[start].Motion.ActorWorld.Origin;
        destination.Y = 0f;
        destination = destination.LengthSquared() > 0f ? destination.Normalized() : Vector3.Zero;
        Vector3 root = Vector3.Zero, synthetic = Vector3.Zero, contact = Vector3.Zero, collision = Vector3.Zero, remainder = Vector3.Zero;
        float prePath = 0f, correctionPath = 0f;
        float anchorPath = 0f;
        for (int index = start + 1; index <= end; index++)
        {
            root += samples[index].Movement.AuthoredRoot;
            synthetic += samples[index].Movement.Synthetic;
            contact += samples[index].Movement.Contact;
            collision += samples[index].Movement.CollisionTravel;
            remainder += samples[index].Movement.CollisionRemainder;
            NPCFootContactEvidence? previousContact = Contact(samples[index - 1].Motion, side);
            NPCFootContactEvidence? currentContact = Contact(samples[index].Motion, side);
            if (previousContact is { HasAnchor: true } previousAnchor && currentContact is { HasAnchor: true } currentAnchor)
            {
                anchorPath += previousAnchor.Anchor.Origin.DistanceTo(currentAnchor.Anchor.Origin);
            }
            if (samples[index - 1].Motion.PreIK is NPCFootMotionPose preBefore && samples[index].Motion.PreIK is NPCFootMotionPose preNow)
            {
                prePath += preBefore.Point(side, FootPosePoint.Midsole).Origin.DistanceTo(preNow.Point(side, FootPosePoint.Midsole).Origin);
                float beforeCorrection = preBefore.Point(side, FootPosePoint.Midsole).Origin.DistanceTo(samples[index - 1].Motion.PostIK.Point(side, FootPosePoint.Midsole).Origin);
                float nowCorrection = preNow.Point(side, FootPosePoint.Midsole).Origin.DistanceTo(samples[index].Motion.PostIK.Point(side, FootPosePoint.Midsole).Origin);
                correctionPath += Math.Abs(nowCorrection - beforeCorrection);
            }
        }
        NPCTerminalSlideAngularMetrics angles = AngularMetrics(samples, start, end, side);
        float maximumPath = points.Max(point => point.WorldPathMetres);
        float maximumNet = points.Max(point => point.WorldNetMetres);
        bool visible = kind switch
        {
            NPCTerminalSlideEpisodeKind.Contact or NPCTerminalSlideEpisodeKind.IdleFade =>
                points.Max(point => point.MaximumOneTickDriftMetres) > AuthoritativePlantedPerTickBoundMetres
                || maximumPath > bound || maximumNet > bound,
            NPCTerminalSlideEpisodeKind.StationaryConfirmation or NPCTerminalSlideEpisodeKind.Settlement =>
                maximumPath > bound || maximumNet > bound || actor.Length() > bound,
            _ => false,
        };
        NPCTerminalSlideCause cause = Classify(samples, start, end, kind, visible, points, angles, synthetic, contact, remainder);
        string evidence = string.Create(CultureInfo.InvariantCulture,
            $"worldPath/net={maximumPath:F6}/{maximumNet:F6}m bound={bound:F6}m; actorResidual={points.Max(point => point.ActorResidualPathMetres):F6}m; chain={angles.TotalPathRadians:F6}rad; preIK/correction={prePath:F6}/{correctionPath:F6}m");
        return new(side, kind, samples[start].Motion.ObserverTick, samples[end].Motion.ObserverTick, peakTick,
            samples[start].Motion.Graph, samples[end].Motion.Graph,
            samples.Single(sample => sample.Motion.ObserverTick == peakTick).Motion.Graph,
            points, actor, actor.Dot(forward),
            actor.Dot(DestinationRight(samples[start])), actor.Dot(destination), root, synthetic,
            contact, collision, remainder, anchorPath, angles, prePath, correctionPath, visible, cause, evidence);
    }

    private static NPCTerminalSlideCause Classify(IReadOnlyList<NPCTerminalSlideSample> samples, int start, int end,
        NPCTerminalSlideEpisodeKind kind, bool visible, IReadOnlyList<NPCTerminalSlidePointMetrics> points,
        NPCTerminalSlideAngularMetrics angles, Vector3 synthetic, Vector3 contact, Vector3 remainder)
    {
        if (!visible)
        {
            return NPCTerminalSlideCause.None;
        }
        float world = points.Max(point => point.WorldNetMetres);
        float residual = points.Max(point => point.ActorResidualNetMetres);
        if (remainder.Length() > NavigationNumericalMovementToleranceMetres)
        {
            return NPCTerminalSlideCause.CollisionMismatch;
        }
        bool commonMode = residual <= NavigationNumericalMovementToleranceMetres;
        if (contact.Length() > AuthoritativePlantedPerTickBoundMetres || (kind == NPCTerminalSlideEpisodeKind.Contact && residual > world * 0.5f))
        {
            return NPCTerminalSlideCause.ContactLockTargetDriftOrFailure;
        }
        return kind == NPCTerminalSlideEpisodeKind.StationaryConfirmation
            && synthetic.Length() > NavigationNumericalMovementToleranceMetres
            && commonMode
            && samples.Skip(start + 1).Take(end - start).All(sample =>
                sample.Movement.AuthoredRoot.Length() <= NavigationNumericalMovementToleranceMetres
                && sample.Movement.Contact.Length() <= NavigationNumericalMovementToleranceMetres
                && sample.Movement.CollisionRemainder.Length() <= NavigationNumericalMovementToleranceMetres)
            ? NPCTerminalSlideCause.StoppedPoseSyntheticSlide
            : kind == NPCTerminalSlideEpisodeKind.Settlement
            ? NPCTerminalSlideCause.SettlementMovement
            : kind == NPCTerminalSlideEpisodeKind.IdleFade
            ? NPCTerminalSlideCause.IdleCrossfadePoseMotion
            : commonMode
            ? NPCTerminalSlideCause.ActorCommonModeTranslationOverQuietPose
            : angles.TotalPathRadians > 0.10f
            ? NPCTerminalSlideCause.AuthoredFootArticulation
            : NPCTerminalSlideCause.Unclassified;
    }

    private static List<string> Validate(IReadOnlyList<NPCTerminalSlideSample> samples, NPCTerminalSlideCalibration calibration)
    {
        List<string> failures = [];
        if (!calibration.IsValid)
        {
            failures.Add($"Independent native calibration invalid: {calibration.BuildReport()}");
        }
        if (samples.Count < 3)
        {
            failures.Add($"At least three contiguous samples are required; observed {samples.Count}.");
            return failures;
        }
        for (int index = 1; index < samples.Count; index++)
        {
            if (samples[index].Motion.ObserverTick != samples[index - 1].Motion.ObserverTick + 1
                || samples[index].Motion.SourceTick == 0
                || samples[index].Motion.SourceTick != samples[index - 1].Motion.SourceTick + 1)
            {
                failures.Add($"Same-callback/source evidence is missing or non-contiguous at row {index}.");
            }
        }
        return failures;
    }

    private static NPCFootContactEvidence? Contact(NPCFootMotionContinuitySample sample, LimbSide side)
        => side == LimbSide.Left ? sample.LeftContact : sample.RightContact;
    private static NPCFootLegChainPose Chain(NPCTerminalSlideSample sample, LimbSide side) => side == LimbSide.Left ? sample.LeftChain : sample.RightChain;
    private static bool IsAuthoredContact(NPCFootMotionContinuitySample sample, LimbSide side)
        => Contact(sample, side) is { Lifecycle: ContactLockLifecycle.Acquire or ContactLockLifecycle.Support };

    private static float MaximumActorResidualStep(NPCTerminalSlideSample before, NPCTerminalSlideSample current, LimbSide side)
    {
        Transform3D transport = current.Motion.ActorWorld * before.Motion.ActorWorld.AffineInverse();
        return _contactPatch.Max(point => current.Motion.PostIK.Point(side, point).Origin.DistanceTo(
            (transport * before.Motion.PostIK.Point(side, point)).Origin));
    }

    private static Vector3 DestinationRight(NPCTerminalSlideSample sample)
    {
        Vector3 forward = sample.DestinationForward;
        forward.Y = 0f;
        return forward.LengthSquared() > 0f ? forward.Normalized().Cross(Vector3.Up).Normalized() : Vector3.Zero;
    }

    private static NPCTerminalSlideAngularMetrics AngularMetrics(IReadOnlyList<NPCTerminalSlideSample> samples, int start, int end, LimbSide side)
    {
        float hips = 0f, upper = 0f, lower = 0f, foot = 0f, toe = 0f, maximum = 0f;
        for (int index = start + 1; index <= end; index++)
        {
            NPCFootLegChainPose before = Chain(samples[index - 1], side);
            NPCFootLegChainPose now = Chain(samples[index], side);
            float[] steps = [Angle(before.Hips, now.Hips), Angle(before.UpperLeg, now.UpperLeg), Angle(before.LowerLeg, now.LowerLeg), Angle(before.Foot, now.Foot), Angle(before.Toe, now.Toe)];
            hips += steps[0];
            upper += steps[1];
            lower += steps[2];
            foot += steps[3];
            toe += steps[4];
            maximum = Math.Max(maximum, steps.Max());
        }
        return new(hips, upper, lower, foot, toe, maximum);
    }

    private static float Angle(Transform3D before, Transform3D now)
        => before.Basis.Orthonormalized().GetRotationQuaternion().AngleTo(now.Basis.Orthonormalized().GetRotationQuaternion());
}
