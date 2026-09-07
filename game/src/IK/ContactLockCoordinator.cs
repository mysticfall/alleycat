using AlleyCat.Rigging;
using Godot;

#pragma warning disable CS1591

namespace AlleyCat.IK;

/// <summary>Owns value-only lifecycle and feasibility state independently for each anatomical foot.</summary>
public sealed class ContactLockCoordinator(AuthoredPerFootContactTrackV1 track)
{
    private const float Epsilon = 0.0001f;
    private readonly FootState _leftState = new(LimbSide.Left);
    private readonly FootState _rightState = new(LimbSide.Right);
    private ContactLockMotionContribution _contribution;
    private LimbSide? _contributionSide;
    private ContactLockMotionContribution _consumedContribution;
    private LimbSide? _consumedContributionSide;
    private long _nextContributionIdentity = 1;

    private readonly AuthoredPerFootContactTrackV1 _track = track ?? throw new ArgumentNullException(nameof(track));

    public float MaximumTranslation { get; set; } = 0.03f;
    public float MaximumYawRadians { get; set; } = 0.06f;
    public float MaximumTranslationRate { get; set; } = 2.0f;
    public float MaximumTargetDeviation { get; set; } = 0.08f;

    /// <summary>Nominal minimum number of physics ticks used to remove a release correction offset.</summary>
    public int ReleaseHandoffMinimumTicks { get; set; } = 8;

    /// <summary>Evidence-derived minimum Left physical-point displacement high band, in metres per tick.</summary>
    public float LeftReleaseHandoffMaximumPointDisplacementMetres { get; set; } = 0.0414086431f;

    /// <summary>Evidence-derived minimum Right physical-point displacement high band, in metres per tick.</summary>
    public float RightReleaseHandoffMaximumPointDisplacementMetres { get; set; } = 0.0439380147f;

    /// <summary>Evidence-derived minimum Left physical-point velocity-vector-change high band, in metres per second.</summary>
    public float LeftReleaseHandoffMaximumVelocityVectorChangeMetresPerSecond { get; set; } = 1.18512666f;

    /// <summary>Evidence-derived minimum Right physical-point velocity-vector-change high band, in metres per second.</summary>
    public float RightReleaseHandoffMaximumVelocityVectorChangeMetresPerSecond { get; set; } = 1.97275591f;

    /// <summary>Maximum reachable sampled fallback position for the configured handoff bounds.</summary>
    public float ReleaseHandoffMaximumReachMetres { get; set; } = 0.80f;

    public ContactLockSnapshot Snapshot { get; private set; } = ContactLockSnapshot.Empty;

    /// <summary>Gets both per-foot snapshots and the singular selected support contribution.</summary>
    public ContactLockCoordinatorSnapshot StateSnapshot
        => new(_leftState.Snapshot, _rightState.Snapshot, _contributionSide, _contribution);

    /// <summary>Gets one foot's current lifecycle snapshot without depending on provider evaluation order.</summary>
    public ContactLockSnapshot GetSnapshot(LimbSide side) => GetState(side).Snapshot;

    /// <summary>Gets the immutable resolver observation for one foot without re-running a provider.</summary>
    public ContactLockResolverTrace GetResolverTrace(LimbSide side) => side == LimbSide.Left ? _leftResolverTrace : _rightResolverTrace;

    /// <summary>Gets the immutable animation evidence last supplied by the post-installer composition.</summary>
    public ContactLockLiveState LiveState
    {
        get; private set;
    }

    /// <summary>Publishes immutable live animation evidence. This does not write a target or actor.</summary>
    public void ObserveLiveState(ContactLockLiveState liveState)
    {
        LiveState = liveState;
        if (!liveState.IsFinite || !liveState.Enabled)
        {
            ClearAll(ContactLockFeasibilityReason.InvalidLiveState);
            return;
        }

        ClearIdentityMismatch(_leftState, liveState);
        ClearIdentityMismatch(_rightState, liveState);
    }

    /// <summary>Clears state before any later result can be published.</summary>
    public void Cancel() => ClearAll(ContactLockFeasibilityReason.Cancelled);

    /// <summary>Clears state before a role replacement or owning node teardown.</summary>
    public void Teardown() => ClearAll(ContactLockFeasibilityReason.Teardown);

    /// <summary>
    /// Resolves a foot intent after synchronisation and before IK actuation. It returns a value only.
    /// </summary>
    public bool TryGetResolution(LimbSide side, Transform3D synchronisedFootTransform, out IKTargetIntentResolution resolution)
        => TryGetResolution(side, synchronisedFootTransform, null, null, synchronisedFootTransform, out resolution);

    /// <summary>Resolves a foot while excluding the contact provider from release fallback lookup.</summary>
    public bool TryGetResolution(
        LimbSide side,
        Transform3D synchronisedFootTransform,
        IKTargetIntentProvider ordinaryFallback,
        out IKTargetIntentResolution resolution)
        => TryGetResolution(side, synchronisedFootTransform, null, ordinaryFallback, null, out resolution);

    /// <summary>
    /// Resolves a foot using the actor transform as the yaw-motion evidence. This prevents animated foot orientation
    /// from being misclassified as an actor correction while preserving the synchronised foot transform as anchor data.
    /// </summary>
    public bool TryGetResolution(
        LimbSide side,
        Transform3D synchronisedFootTransform,
        Transform3D rootMotionTransform,
        IKTargetIntentProvider ordinaryFallback,
        out IKTargetIntentResolution resolution)
        => TryGetResolution(side, synchronisedFootTransform, rootMotionTransform, ordinaryFallback, null, out resolution);

    private bool TryGetResolution(
        LimbSide side,
        Transform3D synchronisedFootTransform,
        Transform3D? rootMotionTransform,
        IKTargetIntentProvider? ordinaryFallback,
        Transform3D? fallbackOverride,
        out IKTargetIntentResolution resolution)
    {
        resolution = default;
        ContactLockLiveState live = LiveState;
        FootState state = GetState(side);
        if (state.IsResolvingFallback)
        {
            return false;
        }

        if (!live.Enabled || !IsFinite(synchronisedFootTransform)
            || (rootMotionTransform.HasValue && !IsFinite(rootMotionTransform.Value)))
        {
            ClearFoot(state, ContactLockFeasibilityReason.InvalidLiveState);
            PublishResolverTrace(
                side,
                synchronisedFootTransform,
                live,
                default,
                CreateAbsentDerivedSample(live, ContactLockFeasibilityReason.InvalidLiveState),
                resolutionPublished: false);
            return false;
        }

        bool lifecycleResolved = _track.TryGetLifecycle(
            live.Role,
            side,
            live.Clip,
            live.Phase,
            out AuthoredContactLifecycleWindow window);

        if (!lifecycleResolved)
        {
            ResetObservedAcquire(state);
            ClearFoot(state, ContactLockFeasibilityReason.Released);
            PublishResolverTrace(
                side,
                synchronisedFootTransform,
                live,
                default,
                CreateAbsentDerivedSample(live, ContactLockFeasibilityReason.Released),
                resolutionPublished: false);
            return false;
        }

        var traceWindow = ContactLockLifecycleWindow.From(window);
        if (window.Lifecycle != ContactLockLifecycle.Acquire)
        {
            ResetObservedAcquire(state);
        }

        AuthoredContactRefusalReason refusal = AuthoredPerFootContactProvider.Validate(
            window.Sample,
            live.Role,
            side,
            live.Variant,
            live.Identity,
            live.Generation);
        DerivedPerPhysicsContactSampleV1 derivedSample = CreateDerivedSample(window.Sample, live, refusal);
        if (refusal != AuthoredContactRefusalReason.None)
        {
            ClearFoot(state, ContactLockFeasibilityReason.Refused);
            PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
            return false;
        }

        if (window.Lifecycle == ContactLockLifecycle.Release)
        {
            return TryGetReleaseHandoff(
                state,
                synchronisedFootTransform,
                live,
                traceWindow,
                derivedSample,
                ordinaryFallback,
                fallbackOverride,
                out resolution);
        }

        int supportedFootCount = _track.CountSupportingFeet(live.Role, live.Clip, live.Phase);
        if (supportedFootCount != 1)
        {
            ClearFoot(state, supportedFootCount == 0 ? ContactLockFeasibilityReason.NoSupport : ContactLockFeasibilityReason.DoubleSupport);
            PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
            return false;
        }

        ContactLockAnchorKey key = new(side, live.Role, live.Clip, live.Identity, live.Generation);
        if (window.Lifecycle == ContactLockLifecycle.Acquire)
        {
            // CharacterIK is the authoritative caller, but a second observer can inspect the same provider during
            // the same physics tick. That observation is the already-admitted lifecycle event, not a second acquire.
            if (state.HasObservedAcquire && state.ObservedAcquireKey.Equals(key))
            {
                if (state.HasAnchor)
                {
                    resolution = new IKTargetIntentResolution(new IKTargetIntent(state.Anchor, 1.0f), true);
                    PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: true, contactPublished: true);
                    return true;
                }

                ClearFoot(state, ContactLockFeasibilityReason.DuplicateAcquire);
                PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            if (state.HasAnchor || state.HasHandoff)
            {
                ClearFoot(state, ContactLockFeasibilityReason.DuplicateAcquire);
                PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            state.Anchor = synchronisedFootTransform;
            state.PreviousRootMotionTransform = rootMotionTransform;
            state.AnchorKey = key;
            state.ObservedAcquireKey = key;
            state.HasAnchor = true;
            state.HasObservedAcquire = true;
            state.CaptureTick = live.PhysicsTick;
            ClearContribution(side);
            SetSnapshot(state, new ContactLockSnapshot(
                live,
                ContactLockLifecycle.Acquire,
                "acquire",
                true,
                state.Anchor,
                side,
                state.CaptureTick,
                default,
                default,
                default,
                ContactLockFeasibilityReason.Valid,
                "contact_lock"));
            resolution = new IKTargetIntentResolution(new IKTargetIntent(state.Anchor, 1.0f), true);
            PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: true, contactPublished: true);
            return true;
        }

        if (!state.HasAnchor || !state.AnchorKey.Equals(key))
        {
            ClearFoot(state, ContactLockFeasibilityReason.LateOrMismatchedSupport);
            PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
            return false;
        }

        EvaluateContribution(state, live, synchronisedFootTransform, rootMotionTransform);
        if (!state.Contribution.IsAuthorised)
        {
            ContactLockMotionContribution refused = state.Contribution;
            SetSnapshot(state, new ContactLockSnapshot(
                live,
                ContactLockLifecycle.Support,
                refused.Reason.ToString(),
                false,
                Transform3D.Identity,
                null,
                0,
                refused.Requested,
                default,
                default,
                refused.Reason,
                "fallback"));
            PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: false);
            return false;
        }

        SetSnapshot(state, state.Snapshot with
        {
            LiveState = live,
            Lifecycle = ContactLockLifecycle.Support,
            LifecycleTransition = "support",
            HasAnchor = true,
            Anchor = state.Anchor,
            CaptureTick = state.CaptureTick,
            Requested = state.Contribution.Requested,
            Authorised = state.Contribution.Authorised,
            Feasibility = state.Contribution.Reason,
            SelectedResolution = "contact_lock",
        });
        resolution = new IKTargetIntentResolution(new IKTargetIntent(state.Anchor, 1.0f), true);
        PublishResolverTrace(side, synchronisedFootTransform, live, traceWindow, derivedSample, resolutionPublished: true, contactPublished: true);
        return true;
    }

    private bool TryGetReleaseHandoff(
        FootState state,
        Transform3D rawSynchronisedSource,
        ContactLockLiveState live,
        ContactLockLifecycleWindow lifecycleWindow,
        DerivedPerPhysicsContactSampleV1 derivedSample,
        IKTargetIntentProvider? ordinaryFallback,
        Transform3D? fallbackOverride,
        out IKTargetIntentResolution resolution)
    {
        resolution = default;
        LimbSide side = state.Side;
        ContactLockAnchorKey key = new(side, live.Role, live.Clip, live.Identity, live.Generation);
        if (state.HasHandoff)
        {
            if (!state.AnchorKey.Equals(key))
            {
                ClearFoot(state, ContactLockFeasibilityReason.IdentityChanged);
                PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
                return false;
            }
        }
        else
        {
            if (!state.HasAnchor || !state.AnchorKey.Equals(key))
            {
                ClearFoot(state, ContactLockFeasibilityReason.Released);
                SetSnapshot(state, state.Snapshot with
                {
                    LiveState = live,
                    Lifecycle = ContactLockLifecycle.Release,
                    LifecycleTransition = "release",
                });
                PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            if (!TryGetLiveFallback(state, ordinaryFallback, fallbackOverride, out Transform3D releaseFallback)
                || !CanStartReleaseHandoff(state, releaseFallback, live.Delta, out int derivedDuration))
            {
                FailReleaseHandoff(state, live, ContactLockFeasibilityReason.ReleaseHandoffRefused, "invalid_release_fallback");
                PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            state.HasHandoff = true;
            state.HandoffFallback = releaseFallback;
            state.HandoffReleaseOffset = state.Anchor.Origin - releaseFallback.Origin;
            state.HandoffRemainingOffset = state.HandoffReleaseOffset;
            state.HandoffResidualScale = 1.0f;
            state.HandoffReturnedTarget = new Transform3D(releaseFallback.Basis, state.Anchor.Origin);
            state.HandoffPreviousOutputStep = Vector3.Zero;
            state.HandoffSelectedDuration = derivedDuration;
            state.LastHandoffPhysicsTick = live.PhysicsTick;
            state.HandoffTick = 1;
            ClearContribution(side);
        }
        if (live.PhysicsTick != state.LastHandoffPhysicsTick)
        {
            if (!TryGetLiveFallback(state, ordinaryFallback, fallbackOverride, out Transform3D liveFallback)
                || !CanUseLiveFallback(state, liveFallback))
            {
                FailReleaseHandoff(state, live, ContactLockFeasibilityReason.ReleaseHandoffRefused, "invalid_live_fallback");
                PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            state.LastHandoffPhysicsTick = live.PhysicsTick;
            state.HandoffTick++;
            if (!TryAdvanceReleaseOffset(state, liveFallback, live.Delta, out Transform3D returnedTarget, out float residualScale, out float candidateContinuityDelta, out float velocityChange))
            {
                FailReleaseHandoff(state, live, ContactLockFeasibilityReason.ReleaseHandoffRefused, "bounded_trajectory_infeasible");
                PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
                return false;
            }

            state.HandoffFallback = liveFallback;
            state.HandoffPreviousOutputStep = returnedTarget.Origin - state.HandoffReturnedTarget.Origin;
            state.HandoffReturnedTarget = returnedTarget;
            state.HandoffResidualScale = residualScale;
            state.HandoffRemainingOffset = returnedTarget.Origin - liveFallback.Origin;
            state.HandoffContinuityDelta = candidateContinuityDelta;
            state.HandoffVelocityVectorChange = velocityChange;
        }

        float continuityDelta = state.HandoffContinuityDelta;
        if (!IsFinite(state.HandoffReturnedTarget)
            || !float.IsFinite(continuityDelta))
        {
            ClearFoot(state, ContactLockFeasibilityReason.ReleaseHandoffRefused);
            PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: false);
            return false;
        }

        ContactLockReleaseHandoff handoff = new(
            true,
            true,
            state.HandoffTick,
            ReleaseHandoffMaximumReachMetres,
            state.HandoffFallback,
            state.HandoffReturnedTarget,
            continuityDelta,
            state.HandoffReleaseOffset,
            state.HandoffRemainingOffset,
            state.HandoffResidualScale,
            ReleaseHandoffMinimumTicks,
            state.HandoffSelectedDuration,
            MaximumPointDisplacementFor(side),
            MaximumVelocityVectorChangeFor(side),
            state.HandoffVelocityVectorChange,
            "active");
        SetSnapshot(state, new ContactLockSnapshot(
            live,
            ContactLockLifecycle.Release,
            "release_handoff",
            true,
            state.Anchor,
            side,
            state.CaptureTick,
            default,
            default,
            default,
            ContactLockFeasibilityReason.Valid,
            "release_handoff",
            handoff));
        resolution = new IKTargetIntentResolution(new IKTargetIntent(state.HandoffReturnedTarget, 1.0f), true);
        PublishResolverTrace(side, rawSynchronisedSource, live, lifecycleWindow, derivedSample, resolutionPublished: true, contactPublished: false);

        if (state.HandoffResidualScale == 0.0f
            && state.HandoffReturnedTarget == state.HandoffFallback)
        {
            CompleteReleaseHandoff(state, live, handoff);
            RefreshResolverTrace(side);
        }

        return true;
    }

    /// <summary>Returns the already-authorised value. It never calculates or mutates it.</summary>
    public ContactLockMotionContribution GetAuthorisedContribution() => _contribution;

    /// <summary>Atomically consumes one fresh, same-clock support contribution for a movement owner.</summary>
    public bool TryConsumeAuthorisedContribution(
        ulong physicsTick,
        long clockGeneration,
        out ContactLockMotionContribution contribution)
    {
        contribution = default;
        if (!_contribution.IsAuthorised
            || _contribution.PhysicsTick != physicsTick
            || _contribution.ClockGeneration != clockGeneration
            || !_contributionSide.HasValue
            || _consumedContribution.IsAuthorised)
        {
            return false;
        }

        contribution = _contribution;
        _consumedContribution = _contribution;
        _consumedContributionSide = _contributionSide;
        _contribution = default;
        _contributionSide = null;
        return true;
    }

    /// <summary>Records the collision-realised movement without compensating for it.</summary>
    public bool RecordMovementRealisation(ContactLockMotionContribution consumed, Vector3 realisedTranslation, float realisedYaw, bool collided)
    {
        if (!consumed.IsAuthorised
            || !_consumedContribution.IsAuthorised
            || consumed != _consumedContribution
            || !_consumedContributionSide.HasValue)
        {
            return false;
        }

        FootState state = GetState(_consumedContributionSide.Value);
        _consumedContribution = default;
        _consumedContributionSide = null;

        Vector3 delta = new(realisedTranslation.X - consumed.Authorised.Translation.X, 0.0f, realisedTranslation.Z - consumed.Authorised.Translation.Z);
        float yawDelta = Mathf.Abs(WrapAngle(realisedYaw - consumed.Authorised.YawRadians));
        bool diverged = collided || delta.Length() > Epsilon || yawDelta > Epsilon;
        if (diverged)
        {
            LimbSide resolvedSide = state.Side;
            ContactLockSnapshot decision = state.Snapshot with
            {
                LifecycleTransition = ContactLockFeasibilityReason.CollisionDivergence.ToString(),
                HasAnchor = false,
                Anchor = Transform3D.Identity,
                AnchorSide = null,
                Requested = consumed.Requested,
                Authorised = consumed.Authorised,
                Applied = new ContactLockMotionValue(realisedTranslation, realisedYaw),
                Feasibility = ContactLockFeasibilityReason.CollisionDivergence,
                SelectedResolution = "fallback",
            };
            ClearFoot(state, ContactLockFeasibilityReason.CollisionDivergence);
            SetSnapshot(state, decision);
            RefreshResolverTrace(resolvedSide, clearPublication: true);
            return false;
        }

        SetSnapshot(state, state.Snapshot with
        {
            Applied = new ContactLockMotionValue(realisedTranslation, realisedYaw)
        });
        RefreshResolverTrace(state.Side);
        return true;
    }

    private void EvaluateContribution(
        FootState state,
        ContactLockLiveState live,
        Transform3D current,
        Transform3D? rootMotionTransform)
    {
        Vector3 requestedTranslation = state.Anchor.Origin - current.Origin;
        requestedTranslation.Y = 0.0f;
        float requestedYaw = rootMotionTransform.HasValue && state.PreviousRootMotionTransform.HasValue
            ? WrapAngle(Yaw(state.PreviousRootMotionTransform.Value.Basis) - Yaw(rootMotionTransform.Value.Basis))
            : WrapAngle(Yaw(state.Anchor.Basis) - Yaw(current.Basis));
        state.PreviousRootMotionTransform = rootMotionTransform;
        ContactLockMotionValue requested = new(requestedTranslation, requestedYaw);
        float deviation = requestedTranslation.Length();
        bool finite = requested.IsFinite && float.IsFinite(MaximumTranslation) && float.IsFinite(MaximumYawRadians)
            && float.IsFinite(MaximumTranslationRate) && float.IsFinite(MaximumTargetDeviation);
        ContactLockFeasibilityReason reason = !finite ? ContactLockFeasibilityReason.NonFinite
            : deviation > MaximumTargetDeviation ? ContactLockFeasibilityReason.TargetDeviationCap
            : deviation > MaximumTranslation ? ContactLockFeasibilityReason.TranslationCap
            : Mathf.Abs(requestedYaw) > MaximumYawRadians ? ContactLockFeasibilityReason.YawCap
            : deviation / Math.Max(live.Delta, Epsilon) > MaximumTranslationRate ? ContactLockFeasibilityReason.RateCap
            : ContactLockFeasibilityReason.Valid;
        state.Contribution = reason == ContactLockFeasibilityReason.Valid
            ? new ContactLockMotionContribution(
                requested,
                requested,
                reason,
                true,
                state.Side,
                live.PhysicsTick,
                live.ClockGeneration,
                _nextContributionIdentity++)
            : new ContactLockMotionContribution(
                requested,
                default,
                reason,
                false,
                state.Side,
                live.PhysicsTick,
                live.ClockGeneration,
                _nextContributionIdentity++);
        _contribution = state.Contribution;
        _contributionSide = state.Contribution.IsAuthorised ? state.Side : null;
    }

    private void ClearAll(ContactLockFeasibilityReason reason)
    {
        ClearFoot(_leftState, reason);
        ClearFoot(_rightState, reason);
        _contribution = default;
        _contributionSide = null;
        _consumedContribution = default;
        _consumedContributionSide = null;
    }

    private void ClearFoot(FootState state, ContactLockFeasibilityReason reason)
    {
        state.HasAnchor = false;
        state.Anchor = Transform3D.Identity;
        state.PreviousRootMotionTransform = null;
        state.AnchorKey = default;
        state.CaptureTick = 0;
        state.Contribution = default;
        state.HasHandoff = false;
        state.HandoffFallback = Transform3D.Identity;
        state.HandoffReleaseOffset = Vector3.Zero;
        state.HandoffReturnedTarget = Transform3D.Identity;
        state.HandoffPreviousOutputStep = Vector3.Zero;
        state.HandoffResidualScale = 0.0f;
        state.HandoffContinuityDelta = 0.0f;
        state.HandoffVelocityVectorChange = 0.0f;
        state.HandoffSelectedDuration = 0;
        state.LastHandoffPhysicsTick = 0;
        state.HandoffTick = 0;
        if (_contributionSide == state.Side)
        {
            _contribution = default;
            _contributionSide = null;
        }
        if (_consumedContributionSide == state.Side)
        {
            _consumedContribution = default;
            _consumedContributionSide = null;
        }

        SetSnapshot(state, new ContactLockSnapshot(
            LiveState,
            ContactLockLifecycle.None,
            reason.ToString(),
            false,
            Transform3D.Identity,
            null,
            0,
            default,
            default,
            default,
            reason,
            "fallback"));
    }

    private bool CanStartReleaseHandoff(FootState state, Transform3D fallback, float delta, out int derivedDuration)
    {
        derivedDuration = 0;
        if (ReleaseHandoffMinimumTicks < 2
            || !HasFiniteReleaseBounds(state.Side)
            || !float.IsFinite(ReleaseHandoffMaximumReachMetres)
            || ReleaseHandoffMaximumReachMetres <= 0.0f
            || !float.IsFinite(delta)
            || delta <= 0.0f
            || !IsFinite(fallback))
        {
            return false;
        }

        float residualDistance = fallback.Origin.DistanceTo(state.Anchor.Origin);
        return float.IsFinite(residualDistance)
            && residualDistance <= ReleaseHandoffMaximumReachMetres + Epsilon
            && TryDeriveReleaseDuration(state.Side, residualDistance, delta, out derivedDuration);
    }

    private bool CanUseLiveFallback(FootState state, Transform3D fallback)
        => IsFinite(fallback)
           && fallback.Origin.DistanceTo(state.Anchor.Origin) <= ReleaseHandoffMaximumReachMetres + Epsilon;

    private bool TryAdvanceReleaseOffset(
        FootState state,
        Transform3D liveFallback,
        float delta,
        out Transform3D returnedTarget,
        out float residualScale,
        out float continuityDelta,
        out float velocityVectorChange)
    {
        returnedTarget = default;
        residualScale = state.HandoffResidualScale;
        continuityDelta = 0.0f;
        velocityVectorChange = 0.0f;
        return float.IsFinite(delta)
            && delta > 0.0f
            && HasFiniteReleaseBounds(state.Side)
            && TryEvaluateReleaseCandidate(
            state,
            liveFallback,
            delta,
            state.HandoffSelectedDuration,
            out returnedTarget,
            out residualScale,
            out continuityDelta,
            out velocityVectorChange);
    }

    private bool TryEvaluateReleaseCandidate(
        FootState state,
        Transform3D liveFallback,
        float delta,
        int duration,
        out Transform3D candidate,
        out float candidateScale,
        out float candidateContinuity,
        out float candidateVelocityChange)
    {
        float progress = state.HandoffTick >= duration
            ? 1.0f
            : (float)(state.HandoffTick - 1) / (duration - 1);
        float smoothProgress = progress * progress * (3.0f - (2.0f * progress));
        float scheduledScale = state.HandoffTick >= duration ? 0.0f : 1.0f - smoothProgress;
        Vector3 desiredRemainingOffset = state.HandoffReleaseOffset * scheduledScale;
        Vector3 desiredOrigin = liveFallback.Origin + desiredRemainingOffset;
        float maximumPointDisplacement = MaximumPointDisplacementFor(state.Side);
        Vector3 desiredStep = (desiredOrigin - state.HandoffReturnedTarget.Origin)
            .LimitLength(maximumPointDisplacement);

        float maximumStepChange = MaximumVelocityVectorChangeFor(state.Side) * delta;
        Vector3 candidateStep = state.HandoffPreviousOutputStep.MoveToward(desiredStep, maximumStepChange);
        Vector3 candidateOrigin = state.HandoffReturnedTarget.Origin + candidateStep;
        candidate = new Transform3D(liveFallback.Basis, candidateOrigin);
        Vector3 candidateRemainingOffset = candidateOrigin - liveFallback.Origin;
        float releaseDistance = state.HandoffReleaseOffset.Length();
        candidateScale = releaseDistance > Epsilon
            ? candidateRemainingOffset.Length() / releaseDistance
            : 0.0f;
        candidateContinuity = candidateStep.Length();
        candidateVelocityChange = (candidateStep - state.HandoffPreviousOutputStep).Length() / delta;
        return IsFinite(candidate)
            && float.IsFinite(candidateContinuity)
            && float.IsFinite(candidateVelocityChange)
            && candidateContinuity <= maximumPointDisplacement + Epsilon
            && candidateVelocityChange <= MaximumVelocityVectorChangeFor(state.Side) + Epsilon;
    }

    private bool TryDeriveReleaseDuration(LimbSide side, float residualDistance, float delta, out int duration)
    {
        // Smoothstep has maximum first derivative 1.5 and a bounded second derivative magnitude of 6. These bounds
        // provide a finite interval count for every finite residual when both configured motion bands are non-zero.
        double displacementIntervals = Math.Ceiling(1.5d * residualDistance / MaximumPointDisplacementFor(side));
        double velocityIntervals = Math.Ceiling(Math.Sqrt(
            6.0d * residualDistance / (MaximumVelocityVectorChangeFor(side) * delta)));
        double requiredIntervals = Math.Max(ReleaseHandoffMinimumTicks - 1.0d, Math.Max(displacementIntervals, velocityIntervals));
        if (!double.IsFinite(requiredIntervals) || requiredIntervals > int.MaxValue - 1.0d)
        {
            duration = 0;
            return false;
        }

        duration = Math.Max(ReleaseHandoffMinimumTicks, checked((int)requiredIntervals + 1));
        return duration >= 2;
    }

    private bool HasFiniteReleaseBounds(LimbSide side)
        => float.IsFinite(MaximumPointDisplacementFor(side))
           && MaximumPointDisplacementFor(side) > 0.0f
           && float.IsFinite(MaximumVelocityVectorChangeFor(side))
           && MaximumVelocityVectorChangeFor(side) > 0.0f;

    private float MaximumPointDisplacementFor(LimbSide side)
        => side == LimbSide.Left
            ? LeftReleaseHandoffMaximumPointDisplacementMetres
            : RightReleaseHandoffMaximumPointDisplacementMetres;

    private float MaximumVelocityVectorChangeFor(LimbSide side)
        => side == LimbSide.Left
            ? LeftReleaseHandoffMaximumVelocityVectorChangeMetresPerSecond
            : RightReleaseHandoffMaximumVelocityVectorChangeMetresPerSecond;

    private static bool TryGetLiveFallback(
        FootState state,
        IKTargetIntentProvider? ordinaryFallback,
        Transform3D? fallbackOverride,
        out Transform3D fallback)
    {
        if (fallbackOverride is Transform3D supplied)
        {
            fallback = supplied;
            return IsFinite(fallback);
        }

        if (ordinaryFallback is not (null or ContactLockFootTargetProvider))
        {
            state.IsResolvingFallback = true;
            try
            {
                if (ordinaryFallback.TryGetTargetIntent(out IKTargetIntentResolution resolution)
                    && resolution.IsFinite
                    && resolution.Intent.DesiredInfluence > 0.0f)
                {
                    fallback = resolution.Intent.WorldTransform;
                    return true;
                }
            }
            finally
            {
                state.IsResolvingFallback = false;
            }
        }

        fallback = Transform3D.Identity;
        return false;
    }

    private void FailReleaseHandoff(
        FootState state,
        ContactLockLiveState live,
        ContactLockFeasibilityReason reason,
        string disposition)
    {
        ContactLockReleaseHandoff failed = CreateHandoffObservation(state, false, disposition);
        ResetHandoffState(state);
        SetSnapshot(state, new ContactLockSnapshot(
            live,
            ContactLockLifecycle.Release,
            disposition,
            false,
            Transform3D.Identity,
            state.Side,
            0,
            default,
            default,
            default,
            reason,
            "release_handoff_incomplete",
            failed));
    }

    private ContactLockReleaseHandoff CreateHandoffObservation(FootState state, bool active, string completionReason)
        => new(
            true,
            active,
            state.HandoffTick,
            ReleaseHandoffMaximumReachMetres,
            state.HandoffFallback,
            state.HandoffReturnedTarget,
            state.HandoffContinuityDelta,
            state.HandoffReleaseOffset,
            state.HandoffRemainingOffset,
            state.HandoffResidualScale,
            ReleaseHandoffMinimumTicks,
            state.HandoffSelectedDuration,
            MaximumPointDisplacementFor(state.Side),
            MaximumVelocityVectorChangeFor(state.Side),
            state.HandoffVelocityVectorChange,
            completionReason);

    private static void ResetHandoffState(FootState state)
    {
        state.HasAnchor = false;
        state.Anchor = Transform3D.Identity;
        state.AnchorKey = default;
        state.CaptureTick = 0;
        state.Contribution = default;
        state.HasHandoff = false;
        state.HandoffFallback = Transform3D.Identity;
        state.HandoffReleaseOffset = Vector3.Zero;
        state.HandoffRemainingOffset = Vector3.Zero;
        state.HandoffReturnedTarget = Transform3D.Identity;
        state.HandoffPreviousOutputStep = Vector3.Zero;
        state.HandoffResidualScale = 0.0f;
        state.HandoffContinuityDelta = 0.0f;
        state.HandoffVelocityVectorChange = 0.0f;
        state.HandoffSelectedDuration = 0;
        state.LastHandoffPhysicsTick = 0;
        state.HandoffTick = 0;
    }

    private void CompleteReleaseHandoff(FootState state, ContactLockLiveState live, ContactLockReleaseHandoff handoff)
    {
        ContactLockReleaseHandoff completedHandoff = handoff with
        {
            IsActive = false,
            CompletionReason = "completed_zero_residual",
        };
        ResetHandoffState(state);
        SetSnapshot(state, new ContactLockSnapshot(
            live,
            ContactLockLifecycle.Release,
            "release_handoff_complete",
            false,
            Transform3D.Identity,
            state.Side,
            0,
            default,
            default,
            default,
            ContactLockFeasibilityReason.Valid,
            "release_handoff_complete",
            completedHandoff));
    }

    private ContactLockResolverTrace _leftResolverTrace;
    private ContactLockResolverTrace _rightResolverTrace;

    private void PublishResolverTrace(
        LimbSide side,
        Transform3D rawSynchronisedSource,
        ContactLockLiveState liveState,
        ContactLockLifecycleWindow lifecycleWindow,
        DerivedPerPhysicsContactSampleV1 derivedSample,
        bool resolutionPublished,
        bool contactPublished = false)
    {
        var trace = new ContactLockResolverTrace(
            true,
            liveState.PhysicsTick,
            side,
            liveState,
            rawSynchronisedSource,
            lifecycleWindow,
            GetState(side).Snapshot,
            derivedSample,
            resolutionPublished,
            contactPublished);
        if (side == LimbSide.Left)
        {
            _leftResolverTrace = trace;
        }
        else
        {
            _rightResolverTrace = trace;
        }
    }

    private static DerivedPerPhysicsContactSampleV1 CreateAbsentDerivedSample(
        ContactLockLiveState live,
        ContactLockFeasibilityReason reason)
        => new(null, live.PhysicsTick, live.Phase, string.Empty, "absent", reason.ToString());

    private static DerivedPerPhysicsContactSampleV1 CreateDerivedSample(
        AuthoredPerFootContactSampleV1 authored,
        ContactLockLiveState live,
        AuthoredContactRefusalReason refusal)
        => new(
            authored,
            live.PhysicsTick,
            live.Phase,
            authored.Lifecycle,
            refusal == AuthoredContactRefusalReason.None ? "fresh" : "refused",
            refusal.ToString());

    private void RefreshResolverTrace(LimbSide? side, bool clearPublication = false)
    {
        if (!side.HasValue)
        {
            return;
        }

        ContactLockResolverTrace trace = GetResolverTrace(side.Value);
        if (!trace.IsAvailable)
        {
            return;
        }

        trace = trace with
        {
            Snapshot = GetState(side.Value).Snapshot,
            ResolutionPublished = !clearPublication && trace.ResolutionPublished,
            ContactPublished = !clearPublication && trace.ContactPublished,
        };
        if (side == LimbSide.Left)
        {
            _leftResolverTrace = trace;
        }
        else
        {
            _rightResolverTrace = trace;
        }
    }

    private static float Yaw(Basis basis) => Mathf.Atan2(-basis.Z.X, -basis.Z.Z);
    private static float WrapAngle(float angle) => Mathf.PosMod(angle + Mathf.Pi, Mathf.Tau) - Mathf.Pi;
    private static bool IsFinite(Transform3D value) => value.Origin.IsFinite() && value.Basis.Column0.IsFinite() && value.Basis.Column1.IsFinite() && value.Basis.Column2.IsFinite();
    private FootState GetState(LimbSide side) => side == LimbSide.Left ? _leftState : _rightState;

    private void SetSnapshot(FootState state, ContactLockSnapshot snapshot)
    {
        state.Snapshot = snapshot;
        Snapshot = snapshot;
    }

    private void ClearContribution(LimbSide side)
    {
        FootState state = GetState(side);
        state.Contribution = default;
        if (_contributionSide == side)
        {
            _contribution = default;
            _contributionSide = null;
        }
    }

    private static void ResetObservedAcquire(FootState state)
    {
        state.HasObservedAcquire = false;
        state.ObservedAcquireKey = default;
    }

    private void ClearIdentityMismatch(FootState state, ContactLockLiveState liveState)
    {
        if ((state.HasAnchor || state.HasHandoff) && !state.AnchorKey.Matches(liveState))
        {
            ClearFoot(state, ContactLockFeasibilityReason.IdentityChanged);
        }
    }

    private sealed class FootState(LimbSide side)
    {
        public LimbSide Side { get; } = side;
        public Transform3D Anchor { get; set; } = Transform3D.Identity;
        public Transform3D? PreviousRootMotionTransform
        {
            get; set;
        }
        public ContactLockAnchorKey AnchorKey
        {
            get; set;
        }
        public ContactLockAnchorKey ObservedAcquireKey
        {
            get; set;
        }
        public bool HasAnchor
        {
            get; set;
        }
        public bool HasObservedAcquire
        {
            get; set;
        }
        public ulong CaptureTick
        {
            get; set;
        }
        public ContactLockMotionContribution Contribution
        {
            get; set;
        }
        public Transform3D HandoffFallback { get; set; } = Transform3D.Identity;
        public Vector3 HandoffReleaseOffset { get; set; } = Vector3.Zero;
        public Vector3 HandoffRemainingOffset { get; set; } = Vector3.Zero;
        public Transform3D HandoffReturnedTarget { get; set; } = Transform3D.Identity;
        public Vector3 HandoffPreviousOutputStep { get; set; } = Vector3.Zero;
        public float HandoffResidualScale
        {
            get; set;
        }
        public float HandoffContinuityDelta
        {
            get; set;
        }
        public float HandoffVelocityVectorChange
        {
            get; set;
        }
        public int HandoffSelectedDuration
        {
            get; set;
        }
        public bool HasHandoff
        {
            get; set;
        }
        public ulong LastHandoffPhysicsTick
        {
            get; set;
        }
        public int HandoffTick
        {
            get; set;
        }
        public bool IsResolvingFallback
        {
            get; set;
        }
        public ContactLockSnapshot Snapshot { get; set; } = ContactLockSnapshot.Empty;
    }

    private readonly record struct ContactLockAnchorKey(LimbSide Side, string Role, string Clip, string Identity, string Generation)
    {
        public bool Matches(ContactLockLiveState state) => Role == state.Role && Clip == state.Clip && Identity == state.Identity && Generation == state.Generation;
    }
}

public enum ContactLockLifecycle
{
    None, Acquire, Support, Release
}
public enum ContactLockFeasibilityReason
{
    Valid, InvalidLiveState, IdentityChanged, Refused, Released, ReleaseHandoffRefused, NoSupport, DoubleSupport, DuplicateAcquire, LateOrMismatchedSupport, NonFinite, TranslationCap, YawCap, RateCap, TargetDeviationCap, CollisionDivergence, Cancelled, Teardown
}
public readonly record struct ContactLockMotionValue(Vector3 Translation, float YawRadians)
{
    public bool IsFinite => Translation.IsFinite() && float.IsFinite(YawRadians);
}
public readonly record struct ContactLockMotionContribution(
    ContactLockMotionValue Requested,
    ContactLockMotionValue Authorised,
    ContactLockFeasibilityReason Reason,
    bool IsGranted,
    LimbSide Side = LimbSide.Left,
    ulong PhysicsTick = 0,
    long ClockGeneration = 0,
    long ConsumptionIdentity = 0)
{
    public bool IsAuthorised => IsGranted && Reason == ContactLockFeasibilityReason.Valid && Authorised.IsFinite;
}
public readonly record struct ContactLockLiveState(
    bool Enabled,
    string Role,
    string Clip,
    float Phase,
    string Variant,
    string Identity,
    string Generation,
    ulong PhysicsTick,
    float Delta)
{
    /// <summary>Evaluated animation-clock generation, distinct from authored evidence generation.</summary>
    public long ClockGeneration
    {
        get; init;
    }

    /// <summary>Actor transform sampled with this evaluated animation state.</summary>
    public Transform3D ActorTransform { get; init; } = Transform3D.Identity;

    /// <summary>Concrete finite animation-clock position sampled with this state.</summary>
    public float FiniteClockPositionSeconds
    {
        get; init;
    }

    /// <summary>Concrete finite animation duration sampled with this state.</summary>
    public float FiniteClockDurationSeconds
    {
        get; init;
    }

    public bool IsFinite => !string.IsNullOrWhiteSpace(Role) && !string.IsNullOrWhiteSpace(Clip) && float.IsFinite(Phase) && Phase >= 0.0f && Phase <= 1.0f && !string.IsNullOrWhiteSpace(Variant) && !string.IsNullOrWhiteSpace(Identity) && !string.IsNullOrWhiteSpace(Generation) && float.IsFinite(Delta) && Delta > 0.0f;
}
public readonly record struct ContactLockSnapshot(ContactLockLiveState LiveState, ContactLockLifecycle Lifecycle, string LifecycleTransition, bool HasAnchor, Transform3D Anchor, LimbSide? AnchorSide, ulong CaptureTick, ContactLockMotionValue Requested, ContactLockMotionValue Authorised, ContactLockMotionValue Applied, ContactLockFeasibilityReason Feasibility, string SelectedResolution, ContactLockReleaseHandoff Handoff = default)
{
    public ContactLockMotionValue Residual => new(Requested.Translation - Applied.Translation, Requested.YawRadians - Applied.YawRadians);
    public static ContactLockSnapshot Empty => new(default, ContactLockLifecycle.None, "none", false, Transform3D.Identity, null, 0, default, default, default, ContactLockFeasibilityReason.NoSupport, "fallback");
}
public readonly record struct ContactLockCoordinatorSnapshot(
    ContactLockSnapshot Left,
    ContactLockSnapshot Right,
    LimbSide? ContributionSide,
    ContactLockMotionContribution Contribution);
public readonly record struct ContactLockLifecycleWindow(float? Start, float? End)
{
    public static ContactLockLifecycleWindow From(AuthoredContactLifecycleWindow window) => window.Lifecycle switch
    {
        ContactLockLifecycle.Acquire => new((float)window.Sample.Timing[0], (float)window.Sample.Timing[0]),
        ContactLockLifecycle.Support => new((float)window.Sample.Timing[0], (float)window.Sample.Timing[1]),
        ContactLockLifecycle.Release => new((float)window.Sample.Timing[0], null),
        ContactLockLifecycle.None => default,
        _ => default,
    };
}
public readonly record struct ContactLockResolverTrace(
    bool IsAvailable,
    ulong PhysicsTick,
    LimbSide Side,
    ContactLockLiveState LiveState,
    Transform3D RawSynchronisedSource,
    ContactLockLifecycleWindow LifecycleWindow,
    ContactLockSnapshot Snapshot,
    DerivedPerPhysicsContactSampleV1 DerivedSample,
    bool ResolutionPublished,
    bool ContactPublished);
public readonly record struct ContactLockReleaseHandoff(
    bool IsObserved,
    bool IsActive,
    int Tick,
    float MaximumReachMetres,
    Transform3D SampledFallback,
    Transform3D ReturnedTarget,
    float ContinuityDeltaMetres,
    Vector3 ReleaseOffset = default,
    Vector3 RemainingOffset = default,
    float ResidualScale = 0.0f,
    int MinimumTicks = 0,
    int SelectedDurationTicks = 0,
    float MaximumPointDisplacementMetres = 0.0f,
    float MaximumVelocityVectorChangeMetresPerSecond = 0.0f,
    float VelocityVectorChangeMetresPerSecond = 0.0f,
    string CompletionReason = "none");
