using System.Globalization;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IntegrationTests.Support;

/// <summary>Coordinate frames retained for every diagnostic-only pre-IK pose.</summary>
public sealed record NPCWFToF2IPreIKPose(
    Transform3D RootSkeletonLocal,
    Transform3D HipsSkeletonLocal,
    Transform3D LeftFootSkeletonLocal,
    Transform3D RightFootSkeletonLocal,
    Transform3D RootWorld,
    Transform3D HipsWorld,
    Transform3D LeftFootWorld,
    Transform3D RightFootWorld,
    string CaptureOrderProof);

/// <summary>Per-tick release evidence; this is observational and has no production authority.</summary>
public sealed record NPCWFToF2IReleaseEvidence(
    string SelectedLane,
    string SelectedEvent,
    string SelectedExitRole,
    float TargetPhase,
    float PreviousAuthoritativePhase,
    float CurrentAuthoritativePhase,
    float ExpectedFrameAdvance,
    bool TerminalApproachActive,
    string ImminentCheckStatus,
    bool ExecutionAttempted,
    bool ExecutionAtRelease,
    bool ExecutionSucceeded,
    string CurrentGraphState,
    string FadingGraphState,
    ulong? ActualTransitionTick);

public sealed record NPCWFToF2ISeamSample(
    ulong PhysicsTick,
    float TimeSeconds,
    float RemainingDistance,
    Transform3D ActorWorld,
    NPCWFToF2IPreIKPose? PreIK,
    NPCWFToF2IReleaseEvidence Release,
    string OutgoingDeclaredSupport,
    string IncomingEntryDeclaredSupport);

/// <summary>Independent raw-animation poses at the authored wf release and f2i entry boundaries, when available.</summary>
public sealed record NPCWFToF2IAuthoredStitchEvidence(
    string OutgoingRole,
    string OutgoingEvent,
    string IncomingRole,
    string IncomingEvent,
    NPCWFToF2IPreIKPose? OutgoingPose,
    NPCWFToF2IPreIKPose? IncomingPose,
    string Source);

public sealed record NPCWFToF2ISeamThresholds(
    float MaximumFootStepMetres,
    float MaximumHipsStepMetres,
    float MaximumRootStepMetres,
    float MaximumPlantedFootDriftMetres,
    float MaximumFootRotationRadians,
    float MaximumHipsRotationRadians,
    float MaximumVelocityChangeMetresPerSecond,
    float MaximumJerkMetresPerSecondCubed,
    float MaximumTerminalHoldMovementMetres,
    int MaximumTerminalHoldTicks)
{
    /// <summary>
    /// Dimensional limits are deliberately route-independent: 60 mm is below a foot-length teleport, 35 mm is
    /// below a visible pelvis snap, and 8 mm is a conservative one-frame planted-foot sweep.
    /// </summary>
    public static NPCWFToF2ISeamThresholds DimensionalDefaults
    {
        get;
    } = new(
        0.060f, 0.035f, 0.060f, 0.008f, 0.45f, 0.30f, 2.5f, 180f, 0.001f, 2);
}

public sealed record NPCWFToF2ISeamRejection(ulong? PhysicsTick, string Channel, string Reason);

public sealed record NPCWFToF2ISeamResult(
    string CaseName,
    IReadOnlyList<NPCWFToF2ISeamSample> Samples,
    NPCWFToF2ISeamThresholds Thresholds,
    IReadOnlyList<NPCWFToF2ISeamRejection> Rejections)
{
    public bool Passed => Rejections.Count == 0;

    public string BuildReport()
    {
        var text = new System.Text.StringBuilder();
        _ = text.AppendLine($"Focused wf->f2i seam diagnostic: {CaseName}: {(Passed ? "PASS" : "FAIL")}");
        _ = text.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Thresholds: foot-step={Thresholds.MaximumFootStepMetres:F3}m; hips-step={Thresholds.MaximumHipsStepMetres:F3}m; planted-drift={Thresholds.MaximumPlantedFootDriftMetres:F3}m; velocity-change={Thresholds.MaximumVelocityChangeMetresPerSecond:F2}m/s; jerk={Thresholds.MaximumJerkMetresPerSecondCubed:F1}m/s^3."));
        foreach (NPCWFToF2ISeamRejection rejection in Rejections)
        {
            _ = text.AppendLine($"- tick {rejection.PhysicsTick?.ToString() ?? "n/a"} [{rejection.Channel}] {rejection.Reason}");
        }

        _ = text.AppendLine("Raw seam window (true pre-IK skeleton-local origins; actor world origin; release chain):");
        foreach (NPCWFToF2ISeamSample sample in Samples)
        {
            NPCWFToF2IPreIKPose? pose = sample.PreIK;
            _ = text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"tick={sample.PhysicsTick} t={sample.TimeSeconds:F6} graph={sample.Release.CurrentGraphState} fading={sample.Release.FadingGraphState} phase={sample.Release.PreviousAuthoritativePhase:F6}->{sample.Release.CurrentAuthoritativePhase:F6} expected={sample.Release.ExpectedFrameAdvance:F6} approach={sample.Release.TerminalApproachActive} check={sample.Release.ImminentCheckStatus} execute={sample.Release.ExecutionAttempted}/{sample.Release.ExecutionAtRelease}/{sample.Release.ExecutionSucceeded} transition={sample.Release.ActualTransitionTick?.ToString() ?? "none"} lane/event/role={sample.Release.SelectedLane}/{sample.Release.SelectedEvent}/{sample.Release.SelectedExitRole} actor={FT(sample.ActorWorld)} root={FT(pose?.RootSkeletonLocal)} hips={FT(pose?.HipsSkeletonLocal)} left={FT(pose?.LeftFootSkeletonLocal)} right={FT(pose?.RightFootSkeletonLocal)} support={sample.OutgoingDeclaredSupport}->{sample.IncomingEntryDeclaredSupport}"));
        }

        return text.ToString();
    }

    private static string FT(Transform3D? value) => value is Transform3D transform
        ? string.Create(CultureInfo.InvariantCulture, $"p({transform.Origin.X:F5},{transform.Origin.Y:F5},{transform.Origin.Z:F5})/q({transform.Basis.GetRotationQuaternion().X:F5},{transform.Basis.GetRotationQuaternion().Y:F5},{transform.Basis.GetRotationQuaternion().Z:F5},{transform.Basis.GetRotationQuaternion().W:F5})")
        : "unavailable";
}

/// <summary>
/// Pure, fail-closed oracle for only the supplied wf-to-f2i seam. It evaluates multi-channel pre-IK evidence and
/// release/contact identity; it is intentionally not a general animation-fluidity score.
/// </summary>
public static class NPCWFToF2ISeamDetector
{
    private const int MinimumSamples = 6;

    public static NPCWFToF2ISeamResult Evaluate(
        string caseName,
        IReadOnlyList<NPCWFToF2ISeamSample>? samples,
        NPCWFToF2IAuthoredStitchEvidence? authored,
        NPCWFToF2ISeamThresholds? thresholds = null)
    {
        NPCWFToF2ISeamThresholds limits = thresholds ?? NPCWFToF2ISeamThresholds.DimensionalDefaults;
        IReadOnlyList<NPCWFToF2ISeamSample> retained = samples ?? [];
        List<NPCWFToF2ISeamRejection> failures = [];
        if (retained.Count < MinimumSamples)
        {
            failures.Add(new(null, "Evidence", $"missing/incomplete seam evidence: retained {retained.Count}, require at least {MinimumSamples} samples"));
            return new(caseName, retained, limits, failures);
        }

        ValidateAuthored(authored, failures);
        ValidateRelease(retained, failures);
        ValidatePoseEvidence(retained, failures);
        if (failures.Any(failure => failure.Channel == "Evidence"))
        {
            return new(caseName, retained, limits, failures);
        }

        EvaluateDynamics(retained, limits, failures);
        EvaluateTerminalHold(retained, limits, failures);
        return new(caseName, retained, limits, failures);
    }

    private static void ValidateAuthored(NPCWFToF2IAuthoredStitchEvidence? authored, List<NPCWFToF2ISeamRejection> failures)
    {
        if (authored is null || authored.OutgoingPose is null || authored.IncomingPose is null)
        {
            failures.Add(new(null, "Evidence", "missing independently sampled authored wf-at-release/f2i-at-entry pre-IK pose evidence"));
            return;
        }

        string expectedRole = authored.OutgoingEvent == "release_l" ? "f2i_l" : "f2i_r";
        if (authored.OutgoingRole != "wf" || authored.IncomingRole != expectedRole || authored.IncomingEvent != authored.OutgoingEvent)
        {
            failures.Add(new(null, "AuthoredIdentity", $"authored stitch mismatch: {authored.OutgoingRole}:{authored.OutgoingEvent}>{authored.IncomingRole}:{authored.IncomingEvent}"));
            return;
        }

        NPCWFToF2IPreIKPose outgoing = authored.OutgoingPose;
        NPCWFToF2IPreIKPose incoming = authored.IncomingPose;
        CheckAuthored("Root", outgoing.RootSkeletonLocal, incoming.RootSkeletonLocal, 0.060f, failures);
        CheckAuthored("Hips", outgoing.HipsSkeletonLocal, incoming.HipsSkeletonLocal, 0.035f, failures);
        CheckAuthored("LeftFoot", outgoing.LeftFootSkeletonLocal, incoming.LeftFootSkeletonLocal, 0.060f, failures);
        CheckAuthored("RightFoot", outgoing.RightFootSkeletonLocal, incoming.RightFootSkeletonLocal, 0.060f, failures);
    }

    private static void CheckAuthored(
        string channel,
        Transform3D outgoing,
        Transform3D incoming,
        float dimensionalLimit,
        List<NPCWFToF2ISeamRejection> failures)
    {
        float position = outgoing.Origin.DistanceTo(incoming.Origin);
        float rotation = outgoing.Basis.GetRotationQuaternion().AngleTo(incoming.Basis.GetRotationQuaternion());
        if (position > dimensionalLimit || rotation > 0.45f)
        {
            failures.Add(new(null, $"Authored{channel}", $"independently sampled authored stitch differs by {position:F6} m/{rotation:F6} rad"));
        }
    }

    private static void ValidateRelease(IReadOnlyList<NPCWFToF2ISeamSample> samples, List<NPCWFToF2ISeamRejection> failures)
    {
        NPCWFToF2ISeamSample? transition = samples.FirstOrDefault(sample => sample.Release.ActualTransitionTick.HasValue);
        if (transition is null)
        {
            failures.Add(new(null, "Release", "missing actual wf->f2i transition tick"));
            return;
        }

        NPCWFToF2IReleaseEvidence release = transition.Release;
        string side = release.SelectedLane == "Left" ? "l" : release.SelectedLane == "Right" ? "r" : "?";
        string expectedEvent = $"release_{side}";
        string expectedRole = $"f2i_{side}";
        string expectedSupport = release.SelectedLane == "Left"
            ? "Right"
            : release.SelectedLane == "Right"
                ? "Left"
                : "Unavailable";
        if (release.SelectedEvent != expectedEvent || release.SelectedExitRole != expectedRole)
        {
            failures.Add(new(transition.PhysicsTick, "LaneEvent", $"wrong lane/event/entry identity: {release.SelectedLane}/{release.SelectedEvent}/{release.SelectedExitRole}, expected {expectedEvent}/{expectedRole}"));
        }

        if (transition.OutgoingDeclaredSupport != expectedSupport || transition.IncomingEntryDeclaredSupport != expectedSupport)
        {
            failures.Add(new(transition.PhysicsTick, "Contact", $"outgoing wf and incoming {expectedRole} support declarations disagree with the opposite-foot planted evidence for leading lane {release.SelectedLane}: {transition.OutgoingDeclaredSupport}->{transition.IncomingEntryDeclaredSupport}, expected {expectedSupport}"));
        }

        if (!samples.Any(sample => sample.PhysicsTick <= transition.PhysicsTick && sample.Release.ExecutionSucceeded))
        {
            failures.Add(new(transition.PhysicsTick, "Release", "graph transitioned without retained successful release execution evidence"));
        }
    }

    private static void ValidatePoseEvidence(IReadOnlyList<NPCWFToF2ISeamSample> samples, List<NPCWFToF2ISeamRejection> failures)
    {
        foreach (NPCWFToF2ISeamSample sample in samples)
        {
            if (sample.PreIK is null || string.IsNullOrWhiteSpace(sample.PreIK.CaptureOrderProof))
            {
                failures.Add(new(sample.PhysicsTick, "Evidence", "missing true pre-IK Root/Hips/left-foot/right-foot pose or modifier-order proof; post-IK output is not accepted"));
                continue;
            }

            Transform3D[] transforms = [sample.PreIK.RootSkeletonLocal, sample.PreIK.HipsSkeletonLocal, sample.PreIK.LeftFootSkeletonLocal, sample.PreIK.RightFootSkeletonLocal, sample.PreIK.RootWorld, sample.PreIK.HipsWorld, sample.PreIK.LeftFootWorld, sample.PreIK.RightFootWorld];
            if (transforms.Any(transform => !Finite(transform)))
            {
                failures.Add(new(sample.PhysicsTick, "Evidence", "non-finite pre-IK transform evidence"));
            }
        }
    }

    private static void EvaluateDynamics(IReadOnlyList<NPCWFToF2ISeamSample> samples, NPCWFToF2ISeamThresholds limits, List<NPCWFToF2ISeamRejection> failures)
    {
        float rootStepLimit = NearbyStepLimit(samples, pose => pose.RootSkeletonLocal.Origin, limits.MaximumRootStepMetres, 0.010f);
        float hipsStepLimit = NearbyStepLimit(samples, pose => pose.HipsSkeletonLocal.Origin, limits.MaximumHipsStepMetres, 0.008f);
        float leftStepLimit = NearbyStepLimit(samples, pose => pose.LeftFootSkeletonLocal.Origin, limits.MaximumFootStepMetres, 0.015f);
        float rightStepLimit = NearbyStepLimit(samples, pose => pose.RightFootSkeletonLocal.Origin, limits.MaximumFootStepMetres, 0.015f);
        Vector3? previousHipsVelocity = null;
        Vector3? previousLeftVelocity = null;
        Vector3? previousRightVelocity = null;
        for (int index = 1; index < samples.Count; index++)
        {
            NPCWFToF2ISeamSample before = samples[index - 1];
            NPCWFToF2ISeamSample current = samples[index];
            float delta = current.TimeSeconds - before.TimeSeconds;
            if (!float.IsFinite(delta) || delta <= 0f)
            {
                failures.Add(new(current.PhysicsTick, "Evidence", "sample time did not advance"));
                continue;
            }

            NPCWFToF2IPreIKPose a = before.PreIK!;
            NPCWFToF2IPreIKPose b = current.PreIK!;
            CheckStep(current.PhysicsTick, "Root", a.RootSkeletonLocal, b.RootSkeletonLocal, rootStepLimit, limits.MaximumFootRotationRadians, failures);
            CheckStep(current.PhysicsTick, "Hips", a.HipsSkeletonLocal, b.HipsSkeletonLocal, hipsStepLimit, limits.MaximumHipsRotationRadians, failures);
            CheckStep(current.PhysicsTick, "LeftFoot", a.LeftFootSkeletonLocal, b.LeftFootSkeletonLocal, leftStepLimit, limits.MaximumFootRotationRadians, failures);
            CheckStep(current.PhysicsTick, "RightFoot", a.RightFootSkeletonLocal, b.RightFootSkeletonLocal, rightStepLimit, limits.MaximumFootRotationRadians, failures);

            Vector3 hipsVelocity = (b.HipsSkeletonLocal.Origin - a.HipsSkeletonLocal.Origin) / delta;
            Vector3 leftVelocity = (b.LeftFootSkeletonLocal.Origin - a.LeftFootSkeletonLocal.Origin) / delta;
            Vector3 rightVelocity = (b.RightFootSkeletonLocal.Origin - a.RightFootSkeletonLocal.Origin) / delta;
            CheckDynamics(current.PhysicsTick, "Hips", hipsVelocity, previousHipsVelocity, delta, limits, failures);
            CheckDynamics(current.PhysicsTick, "LeftFoot", leftVelocity, previousLeftVelocity, delta, limits, failures);
            CheckDynamics(current.PhysicsTick, "RightFoot", rightVelocity, previousRightVelocity, delta, limits, failures);
            previousHipsVelocity = hipsVelocity;
            previousLeftVelocity = leftVelocity;
            previousRightVelocity = rightVelocity;

            Vector3 actorStep = current.ActorWorld.Origin - before.ActorWorld.Origin;
            string support = current.IncomingEntryDeclaredSupport;
            Transform3D supportBefore = support == "Left" ? a.LeftFootWorld : a.RightFootWorld;
            Transform3D supportCurrent = support == "Left" ? b.LeftFootWorld : b.RightFootWorld;
            float plantedDrift = supportCurrent.Origin.DistanceTo(supportBefore.Origin);
            if (plantedDrift > limits.MaximumPlantedFootDriftMetres)
            {
                failures.Add(new(current.PhysicsTick, "SupportFoot", $"planted {support.ToLowerInvariant()} foot swept {plantedDrift:F6} m in world space while actor common-mode step was {actorStep.Length():F6} m"));
            }

            string swing = support == "Left" ? "Right" : "Left";
            Vector3 swingStep = swing == "Left" ? b.LeftFootWorld.Origin - a.LeftFootWorld.Origin : b.RightFootWorld.Origin - a.RightFootWorld.Origin;
            if (index > 1)
            {
                NPCWFToF2IPreIKPose prior = samples[index - 2].PreIK!;
                Vector3 priorSwing = swing == "Left" ? a.LeftFootWorld.Origin - prior.LeftFootWorld.Origin : a.RightFootWorld.Origin - prior.RightFootWorld.Origin;
                if (priorSwing.Length() > 0.003f && swingStep.Length() > 0.003f && priorSwing.Dot(swingStep) < -0.25f * priorSwing.Length() * swingStep.Length())
                {
                    failures.Add(new(current.PhysicsTick, "SwingFoot", $"{swing.ToLowerInvariant()} swing foot reversed direction at seam; prior={priorSwing}, current={swingStep}"));
                }
            }
        }
    }

    private static float NearbyStepLimit(
        IReadOnlyList<NPCWFToF2ISeamSample> samples,
        Func<NPCWFToF2IPreIKPose, Vector3> select,
        float dimensionalMaximum,
        float minimumAllowance)
    {
        float[] steps = [.. samples.Zip(samples.Skip(1), (before, current) => select(current.PreIK!).DistanceTo(select(before.PreIK!))).Order()];
        float median = steps[steps.Length / 2];
        float[] deviations = [.. steps.Select(step => Mathf.Abs(step - median)).Order()];
        float medianAbsoluteDeviation = deviations[deviations.Length / 2];
        // The local robust population catches a channel-specific outlier; the dimensional cap prevents a generally
        // broken window from normalising itself. The allowance is measurement-scale based, not route fitted.
        return Math.Min(dimensionalMaximum, median + Math.Max(6f * medianAbsoluteDeviation, minimumAllowance));
    }

    private static void EvaluateTerminalHold(IReadOnlyList<NPCWFToF2ISeamSample> samples, NPCWFToF2ISeamThresholds limits, List<NPCWFToF2ISeamRejection> failures)
    {
        int held = 0;
        for (int index = 1; index < samples.Count; index++)
        {
            NPCWFToF2ISeamSample before = samples[index - 1];
            NPCWFToF2ISeamSample current = samples[index];
            bool terminal = current.Release.TerminalApproachActive && current.Release.CurrentGraphState == "NPCTravel"
                && current.Release.CurrentAuthoritativePhase >= 0.999f && current.RemainingDistance > 0.05f;
            float motion = current.PreIK!.RootWorld.Origin.DistanceTo(before.PreIK!.RootWorld.Origin)
                + current.ActorWorld.Origin.DistanceTo(before.ActorWorld.Origin);
            held = terminal && motion <= limits.MaximumTerminalHoldMovementMetres ? held + 1 : 0;
            if (held > limits.MaximumTerminalHoldTicks)
            {
                failures.Add(new(current.PhysicsTick, "TerminalHold", $"pre-release wf terminal pose held for {held} consecutive ticks with {current.RemainingDistance:F4} m remaining; Root+actor movement={motion:F6} m"));
                return;
            }
        }
    }

    private static void CheckStep(ulong tick, string channel, Transform3D before, Transform3D current, float maxPosition, float maxRotation, List<NPCWFToF2ISeamRejection> failures)
    {
        float position = current.Origin.DistanceTo(before.Origin);
        float rotation = before.Basis.GetRotationQuaternion().AngleTo(current.Basis.GetRotationQuaternion());
        if (position > maxPosition)
        {
            failures.Add(new(tick, channel, $"single-frame pre-IK position change {position:F6} m exceeded {maxPosition:F6} m"));
        }
        if (rotation > maxRotation)
        {
            failures.Add(new(tick, channel, $"single-frame pre-IK rotation change {rotation:F6} rad exceeded {maxRotation:F6} rad"));
        }
    }

    private static void CheckDynamics(ulong tick, string channel, Vector3 velocity, Vector3? previousVelocity, float delta, NPCWFToF2ISeamThresholds limits, List<NPCWFToF2ISeamRejection> failures)
    {
        if (previousVelocity is not Vector3 previous)
        {
            return;
        }
        Vector3 velocityChange = velocity - previous;
        float accelerationMagnitude = velocityChange.Length() / delta;
        float jerkProxy = accelerationMagnitude / delta;
        if (velocityChange.Length() > limits.MaximumVelocityChangeMetresPerSecond)
        {
            failures.Add(new(tick, channel, $"velocity changed by {velocityChange.Length():F4} m/s (v={velocity}, prior={previous})"));
        }
        if (jerkProxy > limits.MaximumJerkMetresPerSecondCubed)
        {
            failures.Add(new(tick, channel, $"disruptive acceleration/jerk proxy {jerkProxy:F2} m/s^3 exceeded {limits.MaximumJerkMetresPerSecondCubed:F2}"));
        }
    }

    private static bool Finite(Transform3D transform)
        => transform.Origin.IsFinite() && transform.Basis.X.IsFinite() && transform.Basis.Y.IsFinite() && transform.Basis.Z.IsFinite();
}
