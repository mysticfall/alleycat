using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

#pragma warning disable CS1591

/// <summary>Shared projection from coordinator state and native signal observations into IK003 per-foot evidence.</summary>
public static class ContactLockPerFootTraceBuilder
{
    public static IK003PerFootResolverSample Build(
        ContactLockCoordinator coordinator,
        LimbSide side,
        ulong physicsTick,
        IK003NativeSignalObservation native,
        CharacterIKFootActuationTrace actuation = default,
        ContactLockMovementConsumptionTrace? consumption = null)
    {
        ContactLockResolverTrace resolver = coordinator.GetResolverTrace(side);
        if (!resolver.IsAvailable || resolver.PhysicsTick != physicsTick)
        {
            ContactLockLiveState live = coordinator.LiveState.PhysicsTick == physicsTick ? coordinator.LiveState : default;
            ContactLockSnapshot unavailable = coordinator.GetSnapshot(side);
            resolver = new ContactLockResolverTrace(
                false,
                physicsTick,
                side,
                live,
                Transform3D.Identity,
                default,
                unavailable.LiveState.PhysicsTick == physicsTick ? unavailable : ContactLockSnapshot.Empty,
                new DerivedPerPhysicsContactSampleV1(null, physicsTick, live.Phase, string.Empty, "absent", "unavailable"),
                false,
                false);
        }

        ContactLockSnapshot snapshot = resolver.Snapshot;
        ContactLockReleaseHandoff handoff = snapshot.Handoff;
        bool returnedTarget = resolver.IsAvailable && resolver.ResolutionPublished;
        IK003TraceErrors errors = CalculateErrors(
            resolver.RawSynchronisedSource,
            snapshot.Anchor,
            native,
            snapshot.HasAnchor);
        IK003PerFootResolverSample sample = new(
            resolver.IsAvailable,
            physicsTick,
            side.ToString().ToLowerInvariant(),
            IK003CurrentLiveObservation.From(resolver.LiveState),
            new IK003LifecycleWindow(resolver.LifecycleWindow.Start, resolver.LifecycleWindow.End),
            IK003AnimSampleObservation.From(resolver.DerivedSample),
            snapshot.Lifecycle.ToString(),
            snapshot.LifecycleTransition,
            ToWorldTransform(resolver.RawSynchronisedSource),
            snapshot.HasAnchor,
            ToWorldTransform(snapshot.Anchor),
            snapshot.CaptureTick,
            handoff.IsObserved ? "release_handoff" : returnedTarget ? "contact_lock" : "animation_synchronised_fallback",
            handoff.IsObserved ? "non_contact_handoff" : returnedTarget ? "applicable" : "refused",
            returnedTarget ? null : snapshot.Feasibility.ToString(),
            ToArray(snapshot.Requested.Translation),
            snapshot.Requested.YawRadians,
            ToArray(snapshot.Authorised.Translation),
            snapshot.Authorised.YawRadians,
            ToArray(snapshot.Applied.Translation),
            snapshot.Applied.YawRadians,
            ToArray(snapshot.Residual.Translation),
            snapshot.Residual.YawRadians,
            handoff.IsObserved ? "ordinary_fallback_provider" : "none",
            ToWorldTransform(handoff.SampledFallback),
            !handoff.IsObserved || handoff.ReturnedTarget.Basis == handoff.SampledFallback.Basis,
            ToHandoffObservation(handoff),
            native.Observed && native.PhysicsTick == physicsTick ? native : IK003NativeSignalObservation.Empty,
            ToArray(errors.NativeTargetEndFootError),
            ToArray(errors.PostIKAnchorError),
            ToArray(errors.RawSourceAnchorDeviation),
            resolver.ContactPublished)
        {
            Actuation = BuildActuation(actuation, side, physicsTick),
            SignalErrors = BuildSignalErrors(
                resolver.RawSynchronisedSource,
                snapshot.Anchor,
                snapshot.HasAnchor,
                native,
                physicsTick),
            Correction = BuildCorrection(snapshot, side, physicsTick, consumption),
        };
        return sample;
    }

    public static IK003SelectedSupportContribution BuildSelected(ContactLockMovementConsumptionTrace consumption)
    {
        ContactLockMotionContribution contribution = consumption.Contribution;
        ContactLockMotionValue residual = new(
            contribution.Requested.Translation - consumption.RealisedTranslation,
            contribution.Requested.YawRadians - consumption.RealisedYaw);
        return new IK003SelectedSupportContribution(
            contribution.IsAuthorised ? contribution.Side.ToString().ToLowerInvariant() : "none",
            contribution.IsAuthorised,
            ToArray(contribution.Requested.Translation),
            contribution.Requested.YawRadians,
            ToArray(contribution.Authorised.Translation),
            contribution.Authorised.YawRadians,
            ToArray(consumption.Applied ? contribution.Authorised.Translation : Vector3.Zero),
            consumption.Applied ? contribution.Authorised.YawRadians : 0f,
            contribution.Reason.ToString())
        {
            RealisedTranslation = ToArray(consumption.RealisedTranslation),
            RealisedYaw = consumption.RealisedYaw,
            ResidualTranslation = ToArray(residual.Translation),
            ResidualYaw = residual.YawRadians,
            Collided = consumption.Collided,
            Diverged = consumption.Diverged,
            ExactOnceDisposition = consumption.Applied ? "consumed" : "collision_divergence",
        };
    }

    public static IK003SelectedSupportContribution EmptySelected => new(
        "none", false, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, ContactLockFeasibilityReason.NoSupport.ToString());

    public static IK003WorldTransform ToWorldTransform(Transform3D value)
        => new(ToArray(value.Origin), ToArray(value.Basis.X), ToArray(value.Basis.Y), ToArray(value.Basis.Z));

    private static IK003TraceErrors CalculateErrors(
        Transform3D rawSource,
        Transform3D anchor,
        IK003NativeSignalObservation native,
        bool hasAnchor)
    {
        Vector3 target = native.Observed ? ToVector3(native.Target.Origin) : Vector3.Zero;
        Vector3 endFoot = native.Observed ? ToVector3(native.EndFoot.Origin) : Vector3.Zero;
        return new(
            hasAnchor ? rawSource.Origin - anchor.Origin : Vector3.Zero,
            native.Observed ? target - endFoot : Vector3.Zero,
            hasAnchor && native.Observed ? endFoot - anchor.Origin : Vector3.Zero);
    }

    private static IK003FootActuationObservation BuildActuation(
        CharacterIKFootActuationTrace value,
        LimbSide side,
        ulong physicsTick)
        => value.Observed && value.PhysicsTick == physicsTick && value.Side == side
            ? new(
                true,
                physicsTick,
                side.ToString().ToLowerInvariant(),
                value.ProviderSelection,
                value.ProviderIdentity,
                ToWorldTransform(value.SelectedTarget),
                value.TargetGate,
                value.ModifierInfluence,
                value.TargetTransformApplied)
            : IK003FootActuationObservation.Empty;

    private static IK003SignalErrorObservation BuildSignalErrors(
        Transform3D rawSource,
        Transform3D anchor,
        bool hasAnchor,
        IK003NativeSignalObservation native,
        ulong physicsTick)
    {
        if (!native.Observed || native.PhysicsTick != physicsTick)
        {
            Vector3 rawError = hasAnchor ? rawSource.Origin - anchor.Origin : Vector3.Zero;
            return IK003SignalErrorObservation.Empty with
            {
                RawSourceAnchor = ToArray(rawError),
                RawSourceAnchorMagnitude = rawError.Length(),
            };
        }

        Vector3 target = ToVector3(native.Target.Origin);
        Vector3 endFoot = ToVector3(native.EndFoot.Origin);
        Vector3 output = native.PostIKOutputObserved ? ToVector3(native.PostIKOutput.Origin) : endFoot;
        Vector3 targetEndFoot = target - endFoot;
        Vector3 endFootOutput = endFoot - output;
        Vector3 outputAnchor = hasAnchor ? output - anchor.Origin : Vector3.Zero;
        Vector3 rawSourceAnchor = hasAnchor ? rawSource.Origin - anchor.Origin : Vector3.Zero;
        return new(
            ToArray(targetEndFoot),
            targetEndFoot.Length(),
            ToArray(endFootOutput),
            endFootOutput.Length(),
            ToArray(outputAnchor),
            outputAnchor.Length(),
            ToArray(rawSourceAnchor),
            rawSourceAnchor.Length());
    }

    private static IK003PerFootCorrectionObservation BuildCorrection(
        ContactLockSnapshot snapshot,
        LimbSide side,
        ulong physicsTick,
        ContactLockMovementConsumptionTrace? consumption)
    {
        ContactLockMovementConsumptionTrace observed = consumption.GetValueOrDefault();
        bool sameTickConsumption = consumption.HasValue
            && observed.Contribution.Side == side
            && observed.Contribution.PhysicsTick == physicsTick;
        ContactLockMotionValue requested = sameTickConsumption ? observed.Contribution.Requested : snapshot.Requested;
        ContactLockMotionValue authorised = sameTickConsumption ? observed.Contribution.Authorised : snapshot.Authorised;
        ContactLockMotionValue realised = sameTickConsumption
            ? new(observed.RealisedTranslation, observed.RealisedYaw)
            : snapshot.Applied;
        ContactLockMotionValue residual = new(
            requested.Translation - realised.Translation,
            requested.YawRadians - realised.YawRadians);
        string disposition = sameTickConsumption
            ? observed.Applied ? "consumed" : "collision_divergence"
            : authorised != default
                ? "available"
                : requested != default
                    || snapshot.Feasibility is not (ContactLockFeasibilityReason.Valid or ContactLockFeasibilityReason.NoSupport)
                    ? "refused"
                    : "unavailable";
        long clockGeneration = sameTickConsumption
            ? observed.Contribution.ClockGeneration
            : snapshot.LiveState.ClockGeneration;
        IK003PerFootCorrectionObservation correction = new(
            side.ToString().ToLowerInvariant(),
            physicsTick,
            clockGeneration,
            sameTickConsumption ? observed.Contribution.ConsumptionIdentity : 0,
            sameTickConsumption ? observed.MovementOperationIdentity : 0,
            ToArray(requested.Translation),
            requested.YawRadians,
            ToArray(authorised.Translation),
            authorised.YawRadians,
            ToArray(realised.Translation),
            realised.YawRadians,
            ToArray(realised.Translation),
            realised.YawRadians,
            ToArray(residual.Translation),
            residual.YawRadians,
            sameTickConsumption && observed.Collided,
            sameTickConsumption && observed.Diverged,
            disposition);
        return correction with
        {
            ActorBefore = sameTickConsumption ? ToWorldTransform(observed.ActorBefore) : ToWorldTransform(snapshot.LiveState.ActorTransform),
            ActorAfter = sameTickConsumption ? ToWorldTransform(observed.ActorAfter) : ToWorldTransform(snapshot.LiveState.ActorTransform),
        };
    }

    private static IK003ReleaseHandoffObservation ToHandoffObservation(ContactLockReleaseHandoff handoff)
        => new(
            handoff.IsObserved ? handoff.IsActive ? "active" : "complete" : "none",
            handoff.Tick,
            handoff.MaximumReachMetres,
            ToWorldTransform(handoff.SampledFallback),
            ToWorldTransform(handoff.ReturnedTarget),
            handoff.ContinuityDeltaMetres,
            ToArray(handoff.ReleaseOffset),
            ToArray(handoff.RemainingOffset),
            handoff.ResidualScale,
            handoff.MinimumTicks,
            handoff.SelectedDurationTicks,
            handoff.MaximumPointDisplacementMetres,
            handoff.MaximumVelocityVectorChangeMetresPerSecond,
            handoff.VelocityVectorChangeMetresPerSecond,
            handoff.CompletionReason);

    private static IK003Vector3 ToArray(Vector3 value) => new(value.X, value.Y, value.Z);
    private static Vector3 ToVector3(IK003Vector3 value) => new(value.X, value.Y, value.Z);
}

public readonly record struct ContactLockMovementConsumptionTrace(
    ulong MovementOperationIdentity,
    ContactLockMotionContribution Contribution,
    Vector3 RealisedTranslation,
    float RealisedYaw,
    bool Applied,
    bool Collided,
    bool Diverged,
    Transform3D ActorBefore,
    Transform3D ActorAfter);
