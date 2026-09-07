using System.Runtime.CompilerServices;
using AlleyCat.Control.Hands;
using AlleyCat.Core.Logging;
using AlleyCat.Interaction;
using AlleyCat.Interaction.Hands;
using AlleyCat.Rigging;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

/// <summary>
/// Mode-aware optical grab input coordinator (CTRL-002; XR-002 TR46-TR54; INTR-002 R59).
/// </summary>
/// <remarks>
/// <para>
/// <strong>What it does.</strong> While the committed global hand-pose mode is <c>Optical</c>, the coordinator
/// recognises candidate-aware power-grip closure and opening from the raw optical joints of each hand and
/// translates recognised intent into the existing grab lifecycle: a stable grab-threshold crossing begins a
/// grab with optical provenance through <see cref="IHandGrabLifecycle.BeginGrab" />, a stable aggregate
/// opening cancels a pending grab through <see cref="IHandGrabLifecycle.CancelPendingGrab" /> or releases a
/// held object through <see cref="IHand.Release()" />. It implements no grab mechanics and monitors no IK
/// settling (CTRL-002 TR19).
/// </para>
/// <para>
/// <strong>Frame-ordering contract.</strong> The coordinator runs in <see cref="Node._PhysicsProcess" />:
/// OpenXR joint data is produced on the physics tick, so recognition consumes fresh samples on that tick and
/// its <c>Grab()</c>/<c>Release()</c> calls act before the same engine frame's process pass, where
/// <see cref="HandPoseBehaviour._Process" /> performs pending-grab commit processing. Godot executes physics
/// before per-frame process callbacks, so no deferred hopping is required.
/// </para>
/// <para>
/// <strong>Recognition pipeline per side per tick</strong> (ordered):
/// reconcile the state-machine latch with the hand lifecycle → resolve the recognition reference (the active
/// grab's candidate while pending/held, else the observed current-best candidate) → cancel recognition when no
/// eligible candidate exists (candidate-aware) → derive or fetch the cached animation-derived power-grip
/// profile (fail-closed) → fetch the 20 raw joints from the provider → project them through the shared
/// <see cref="OpticalFingerProjectionBinding" /> the finger modifier stages → evaluate the aggregate → run the
/// hysteresis state machine → translate the emitted edge.
/// </para>
/// <para>
/// <strong>Lifecycle policies.</strong> Invalid or insufficient articulation pauses recognition with no
/// synthetic edge: a pending grab is cancelled on loss while a held grab is preserved (INTR-002 R59). The
/// same policy applies while recognition dependencies — the hands holder, the XR runtime, or the finger
/// modifier — cannot resolve: optical-originated pending grabs cancel so <see cref="HandPoseBehaviour._Process" />
/// can never commit one without a functioning loss detector, held grabs are preserved, stale measurements
/// and stability are invalidated, and recovery requires a fresh stability interval. Explicit committed mode
/// transitions — the <see cref="XRManager.HandTrackingModeChanged" /> signal, not ambiguous tracking loss —
/// cancel pending and release held grabs whose provenance is the now-inactive source, reset per-side
/// latches, and never transfer ownership (CTRL-002 TR6, TR8); the authoritative runtime mode is re-checked
/// each physics tick so a transition that arrived during a dependency outage is still retired on recovery.
/// Game-menu pause suppresses all optical edges — physics processing halts while the tree is paused — and
/// the pause notification resets edge accumulation so no burst fires on unpause while held state is
/// preserved (CTRL-002 TR18).
/// </para>
/// <para>
/// All dependency resolution is late-bound and fail-safe: hands through <see cref="IHasHands" /> with deferred
/// retry (mirroring <see cref="PlayerController" />), the XR runtime through the game service provider's
/// <see cref="XRManager" />, and the finger modifier's shared projection binding through a throttled
/// descendant search from this node's parent. Until everything is available recognition is suspended, but
/// dependency-loss lifecycle safety still runs every physics tick.
/// </para>
/// </remarks>
[GlobalClass]
public partial class HandGrabInputCoordinator : Node
{
    private const int TrackedJointCount = 20;

    /// <summary>Delay between repeated modifier searches when none has been found yet, in physics ticks.</summary>
    private const uint ModifierSearchRetryCooldownTicks = 30;

    private static readonly LimbSide[] _sides = [LimbSide.Left, LimbSide.Right];

    private readonly OpticalSideState[] _sideStates = [new(), new()];

    private IHasHands? _hands;
    private XRManager? _xrManager;
    private OpticalFingerTrackingModifier? _modifier;
    private ILogger<HandGrabInputCoordinator>? _logger;
    private bool _handResolutionRetryQueued;
    private bool _modeChangeSubscribed;
    private bool _pipelineReady;
    private bool _pipelineUnavailableLogged;
    private uint _modifierSearchCooldown;
    private ulong _nextAttemptID;
    private ulong _nextEvaluationID;
    private XRHandTrackingMode _lastObservedMode = XRHandTrackingMode.Controller;

    /// <summary>
    /// Reports one completed optical grip evaluation without changing recognition or grab lifecycle state.
    /// Intended for focused runtime verification and tooling that needs evaluation-by-evaluation evidence.
    /// </summary>
    public event Action<OpticalGrabEvaluationTrace>? OpticalGrabEvaluated;

    /// <summary>
    /// Candidate-keyed production strategy resolver. The default contains only power grip; tests and future content
    /// may supply a resolver with another strategy without changing coordinator control flow.
    /// </summary>
    public IGripRecognitionStrategyResolver RecognitionStrategyResolver { get; set; } = GripRecognitionStrategies.Default;

    /// <summary>
    /// Returns the most recent per-side optical recognition measurement. It is a read-only snapshot for focused
    /// runtime diagnosis; it neither evaluates recognition nor changes lifecycle state.
    /// </summary>
    public bool TryGetOpticalGripMeasurement(LimbSide side, out OpticalGripMeasurement measurement)
    {
        OpticalGripMeasurement? current = _sideStates[(int)side].LastMeasurement;
        if (current is null)
        {
            measurement = default;
            return false;
        }

        measurement = current.Value;
        return true;
    }

    /// <summary>
    /// Optional node implementing <see cref="IHasHands" /> for hand resolution; falls back to a sibling
    /// <c>Hands</c> node of this coordinator's parent, mirroring <see cref="PlayerController.HandHolderNode" />.
    /// </summary>
    [Export]
    public Node? HandHolderNode
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        _hands = ResolveHands();
        QueueHandsResolutionRetryIfNeeded();
        _ = ResolveXrManager();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_modeChangeSubscribed && _xrManager is XRManager manager)
        {
            manager.HandTrackingModeChanged -= OnHandTrackingModeChanged;
            _modeChangeSubscribed = false;
        }
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (!TryResolvePipeline(out CoordinatorPipeline pipeline))
        {
            // Recognition cannot run, but lifecycle safety still must (CTRL-002 TR15; XR-002 TR51): an optical
            // pending grab must not survive — and later commit — without a functioning recognition/loss
            // detector, while a held grab is preserved with no synthetic release.
            ProcessDependencyLoss();
            return;
        }

        // Reconcile against the authoritative committed mode before the mode gate: a transition the signal
        // path could not fully process while dependencies were unresolved is retired here (CTRL-002 TR8).
        ReconcileCommittedMode(pipeline.Runtime.HandTrackingMode);

        if (pipeline.Runtime.HandTrackingMode != XRHandTrackingMode.Optical)
        {
            return;
        }

        foreach (LimbSide side in _sides)
        {
            ProcessSide(side, pipeline, (float)delta);
        }
    }

    /// <inheritdoc />
    public override void _Notification(int what)
    {
        base._Notification(what);

        if (what != NotificationPaused)
        {
            return;
        }

        // Pause suppression (CTRL-002 TR18): physics processing halts while the tree is paused, so no edges
        // are processed; clearing the edge accumulation here guarantees no burst fires on unpause while held
        // state — the grabbed latch — is preserved.
        foreach (OpticalSideState state in _sideStates)
        {
            state.Machine.Reset(state.Machine.IsGrabRecognised);
        }

        ResolveLogger()?.LogDebug("Game paused; optical grab edge accumulation reset for both hands.");
    }

    private void ProcessSide(LimbSide side, in CoordinatorPipeline pipeline, float deltaSeconds)
    {
        if (!pipeline.Hands.TryGetHand(side, out IHand? hand)
            || hand is not IHandGrabLifecycle lifecycle)
        {
            // No hand component for this side, or it lacks the lifecycle seam: the input is ignored (CTRL-002
            // TR20) — never an error.
            return;
        }

        OpticalSideState state = _sideStates[(int)side];
        HandGrabLifecycleState lifecycleBefore = lifecycle.GrabLifecycle;

        // External abandonment observation: when the authoritative lifecycle ended a grab the recognition
        // machine still holds, surface a bounded non-convergence or persistently-moving-candidate
        // abandonment through the neutral trace while the attempt identity is still live (IK-005 TR22).
        // Other externally driven endings keep their existing silent latch reconciliation.
        if (lifecycleBefore == HandGrabLifecycleState.None
            && state.Machine.IsGrabRecognised
            && state.ActiveAttemptID is not null
            && lifecycle.LastPendingGrabAbandonmentReason
                is HandGrabAbandonmentReason.NonConvergence
                or HandGrabAbandonmentReason.MovingCandidate)
        {
            PublishEvaluationTrace(
                side,
                state,
                GripEdge.None,
                HandGrabLifecycleState.Pending,
                HandGrabLifecycleState.None,
                lifecycle.LastPendingGrabAbandonmentReason == HandGrabAbandonmentReason.MovingCandidate
                    ? OpticalGrabEvaluationReason.PendingAbandonedMovingCandidate
                    : OpticalGrabEvaluationReason.PendingAbandonedNonConvergence);
        }

        SynchroniseAttemptIdentity(state, lifecycleBefore);

        // Reconcile the machine's grabbed latch with the authoritative hand lifecycle so externally driven
        // transitions (forced cancellation or release, commit abandonment) can never leave stale recognition
        // state, and a fresh stability interval always follows.
        bool grabIntentActive = lifecycle.GrabLifecycle
            is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held;
        if (state.Machine.IsGrabRecognised != grabIntentActive)
        {
            state.Machine.Reset(grabIntentActive);
        }

        GrabPoseReference? reference;
        string? strategyName;
        object? candidateIdentity;
        if (grabIntentActive)
        {
            // Pending and held recognition keep the active grab's candidate as their fixed reference
            // (INTR-002 R59-60); the live candidate observation is not consulted.
            reference = lifecycle.ActiveGrabReference;
            strategyName = lifecycle.ActiveGrabRecognitionStrategyName;
            candidateIdentity = reference;
        }
        else if (lifecycle.TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate)
            && candidate is not null)
        {
            reference = candidate.Reference;
            strategyName = candidate.GripRecognitionStrategyName;
            candidateIdentity = (
                GetIdentity(candidate.Grabbable),
                GetIdentity(candidate.GrabPointSource),
                candidate.Reference.Animation.GetInstanceId());
        }
        else
        {
            // Candidate-aware recognition: with no eligible candidate there is no recognition regardless of
            // hand closure (CTRL-002 TR10; XR-002 TR47).
            state.Machine.Reset(isGrabRecognised: false);
            state.LastMeasurement = OpticalGripMeasurement.NoCandidate(lifecycle.GrabLifecycle);
            return;
        }

        if (reference is null
            || string.IsNullOrWhiteSpace(strategyName)
            || !TryGetProfile(side, state, pipeline.Modifier, reference, strategyName, out IGripRecognitionProfile? profile,
                out IGripRecognitionStrategy? strategy)
            || profile is null)
        {
            // Fail closed: an underivable profile disables recognition for this tick (CTRL-002 TR12).
            state.Machine.Reset(grabIntentActive);
            state.LastMeasurement = OpticalGripMeasurement.ProfileUnavailable(lifecycle.GrabLifecycle, reference?.Animation);
            return;
        }

        // The resolved strategy, not this coordinator, owns the compatible recognition policy. Reconfiguration is
        // identity-keyed, so a resolved strategy or policy change starts a fresh stability interval while unchanged
        // valid observations remain on the hot path without resetting every physics tick.
        state.Machine.Configure(strategy!, profile.Settings);

        if (!TryEvaluate(
                side,
                pipeline,
                state,
                strategy!,
                profile,
                out GripRecognitionEvaluation evaluation,
                out OpticalGrabEvaluationReason rejectionReason))
        {
            // A failed projection/evaluation is itself the current measurement. Never leave the previous
            // successful score, pending edge, or validity visible to evidence consumers.
            state.LastMeasurement = OpticalGripMeasurement.Invalid(
                lifecycle.GrabLifecycle,
                reference.Animation,
                rejectionReason);
            HandleInvalidArticulation(side, lifecycle, state, rejectionReason.ToString());
            PublishEvaluationTrace(
                side,
                state,
                GripEdge.None,
                lifecycleBefore,
                lifecycle.GrabLifecycle,
                rejectionReason);
            return;
        }

        if (!evaluation.SufficientValidity)
        {
            state.LastMeasurement = OpticalGripMeasurement.FromEvaluation(
                lifecycle.GrabLifecycle,
                reference.Animation,
                evaluation,
                state.Machine,
                GripEdge.None);
            HandleInvalidArticulation(side, lifecycle, state, reason: "insufficient live-valid articulation");
            PublishEvaluationTrace(
                side,
                state,
                GripEdge.None,
                lifecycleBefore,
                lifecycle.GrabLifecycle,
                OpticalGrabEvaluationReason.InsufficientValidity);
            return;
        }

        if (state.RecognitionInvalidLogged)
        {
            state.RecognitionInvalidLogged = false;
            ResolveLogger()?.LogDebug("{Side} hand optical articulation recovered; recognition resumed.", side);
        }

        GripEdge edge = state.Machine.Evaluate(evaluation, deltaSeconds, candidateIdentity);
        OpticalGrabEvaluationReason reason = TranslateEdge(edge, side, hand, lifecycle);
        HandGrabLifecycleState lifecycleAfter = lifecycle.GrabLifecycle;
        if (state.ActiveAttemptID is null
            && lifecycleAfter is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held)
        {
            state.ActiveAttemptID = ++_nextAttemptID;
        }

        state.LastMeasurement = OpticalGripMeasurement.FromEvaluation(
            lifecycleAfter,
            reference.Animation,
            evaluation,
            state.Machine,
            edge);
        PublishEvaluationTrace(side, state, edge, lifecycleBefore, lifecycleAfter, reason);
    }

    private OpticalGrabEvaluationReason TranslateEdge(
        GripEdge edge,
        LimbSide side,
        IHand hand,
        IHandGrabLifecycle lifecycle)
    {
        ILogger<HandGrabInputCoordinator>? logger = ResolveLogger();

        switch (edge)
        {
            case GripEdge.None:
                return OpticalGrabEvaluationReason.NoEdge;

            case GripEdge.Grab:
                if (lifecycle.GrabLifecycle != HandGrabLifecycleState.None)
                {
                    // The lifecycle moved between the evaluation and the edge; reconciliation handles it next tick.
                    return OpticalGrabEvaluationReason.GrabRejectedLifecycleActive;
                }

                _ = lifecycle.BeginGrab(HandGrabInputSource.Optical);
                if (lifecycle.GrabLifecycle == HandGrabLifecycleState.None)
                {
                    return OpticalGrabEvaluationReason.GrabRejectedNoCandidate;
                }

                logger?.LogInformation("Optical grab recognised on the {Side} hand; approach phase begun.", side);
                return OpticalGrabEvaluationReason.GrabStarted;

            case GripEdge.Release:
                switch (lifecycle.GrabLifecycle)
                {
                    case HandGrabLifecycleState.Pending:
                        _ = lifecycle.CancelPendingGrab();
                        logger?.LogInformation(
                            "Stable optical opening on the {Side} hand; pending grab cancelled through abandonment.",
                            side);
                        return OpticalGrabEvaluationReason.PendingCancelled;

                    case HandGrabLifecycleState.Held:
                        hand.Release();
                        logger?.LogInformation(
                            "Stable optical opening on the {Side} hand; held object released through Release().",
                            side);
                        return OpticalGrabEvaluationReason.HeldReleased;

                    case HandGrabLifecycleState.None:
                    default:
                        // The grab already ended externally; the release edge is consumed (reconciliation follows).
                        return OpticalGrabEvaluationReason.ReleaseIgnoredNoActiveGrab;
                }

            default:
                return OpticalGrabEvaluationReason.UnsupportedEdge;
        }
    }

    private static bool TryEvaluate(
        LimbSide side,
        in CoordinatorPipeline pipeline,
        OpticalSideState state,
        IGripRecognitionStrategy strategy,
        IGripRecognitionProfile profile,
        out GripRecognitionEvaluation evaluation,
        out OpticalGrabEvaluationReason rejectionReason)
    {
        evaluation = default;
        rejectionReason = OpticalGrabEvaluationReason.NoEdge;

        for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
        {
            _ = pipeline.JointProvider.TryGetJoint(side, (XRHandJoint)jointIndex, out state.JointSamples[jointIndex]);
        }

        if (!pipeline.Modifier.TryProjectOpticalFingers(side, state.JointSamples, state.ProjectedPoses))
        {
            rejectionReason = OpticalGrabEvaluationReason.ProjectionUnavailable;
            return false;
        }

        if (!strategy.TryEvaluate(profile, state.ProjectedPoses, out evaluation))
        {
            rejectionReason = OpticalGrabEvaluationReason.EvaluationFailed;
            return false;
        }

        return true;
    }

    private void SynchroniseAttemptIdentity(OpticalSideState state, HandGrabLifecycleState lifecycle)
    {
        if (lifecycle == HandGrabLifecycleState.None)
        {
            state.ActiveAttemptID = null;
        }
        else
        {
            state.ActiveAttemptID ??= ++_nextAttemptID;
        }
    }

    private void PublishEvaluationTrace(
        LimbSide side,
        OpticalSideState state,
        GripEdge edge,
        HandGrabLifecycleState lifecycleBefore,
        HandGrabLifecycleState lifecycleAfter,
        OpticalGrabEvaluationReason reason)
    {
        ulong? attemptID = state.ActiveAttemptID;
        OpticalGrabEvaluated?.Invoke(new OpticalGrabEvaluationTrace(
            ++_nextEvaluationID,
            Engine.GetPhysicsFrames(),
            side,
            edge,
            lifecycleBefore,
            lifecycleAfter,
            attemptID,
            reason));

        if (lifecycleAfter == HandGrabLifecycleState.None)
        {
            state.ActiveAttemptID = null;
        }
    }

    /// <summary>
    /// Recognition pauses per side with no synthetic edge: a pending grab is cancelled on loss while a held
    /// grab is preserved (INTR-002 R59; CTRL-002 TR15). A recovered stable open must hold its full stability
    /// interval because the accumulation resets here.
    /// </summary>
    private void HandleInvalidArticulation(
        LimbSide side,
        IHandGrabLifecycle? lifecycle,
        OpticalSideState state,
        string reason)
    {
        if (!state.RecognitionInvalidLogged)
        {
            state.RecognitionInvalidLogged = true;
            ResolveLogger()?.LogInformation(
                "Optical recognition for the {Side} hand paused: {Reason}.",
                side,
                reason);
        }

        if (lifecycle?.GrabLifecycle == HandGrabLifecycleState.Pending)
        {
            _ = lifecycle.CancelPendingGrab();
            ResolveLogger()?.LogInformation(
                "Tracking lost while the {Side} hand grab was pending; pending grab cancelled.",
                side);
        }

        bool stillActive = lifecycle?.GrabLifecycle
            is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held;
        state.Machine.Reset(stillActive);
    }

    /// <summary>
    /// Lifecycle safety and measurement invalidation while recognition dependencies — the hands holder, the XR
    /// runtime, or the finger modifier — cannot resolve (CTRL-002 TR15; XR-002 TR51; INTR-002 R59). An
    /// optical-originated pending grab is cancelled before <see cref="HandPoseBehaviour._Process" /> can commit
    /// it with no functioning recognition or loss detector; a held grab is preserved with no synthetic
    /// release. Stale measurements, scores, and pending edges are invalidated and recognition accumulation is
    /// reset, so recovery always requires a fresh stability interval. Provenance and mode are reconciled
    /// against the authoritative committed mode once the runtime returns.
    /// </summary>
    private void ProcessDependencyLoss()
    {
        IHasHands? hands = GetResolvedHandsHolder();

        foreach (LimbSide side in _sides)
        {
            OpticalSideState state = _sideStates[(int)side];

            IHandGrabLifecycle? lifecycle = null;
            if (hands is not null
                && hands.TryGetHand(side, out IHand? hand)
                && hand is IHandGrabLifecycle handLifecycle)
            {
                lifecycle = handLifecycle;
            }

            if (lifecycle?.GrabLifecycle == HandGrabLifecycleState.Pending
                && lifecycle.GrabInputSource == HandGrabInputSource.Optical)
            {
                _ = lifecycle.CancelPendingGrab();
                ResolveLogger()?.LogInformation(
                    "Recognition dependencies lost while the {Side} hand grab was pending; pending grab cancelled.",
                    side);
            }

            HandGrabLifecycleState lifecycleAfter = lifecycle?.GrabLifecycle ?? HandGrabLifecycleState.None;
            if (lifecycleAfter == HandGrabLifecycleState.None)
            {
                state.ActiveAttemptID = null;
            }

            bool stillActive = lifecycleAfter is HandGrabLifecycleState.Pending or HandGrabLifecycleState.Held;
            state.Machine.Reset(stillActive);

            if (state.LastMeasurement is not null || stillActive)
            {
                state.LastMeasurement = OpticalGripMeasurement.DependenciesUnavailable(lifecycleAfter);
            }

            if (!state.RecognitionInvalidLogged)
            {
                state.RecognitionInvalidLogged = true;
                ResolveLogger()?.LogInformation(
                    "Optical recognition for the {Side} hand paused: recognition dependencies are unavailable.",
                    side);
            }
        }
    }

    private IHasHands? GetResolvedHandsHolder()
        => _hands is Node handsNode && !IsInstanceValid(handsNode) ? null : _hands;

    private bool TryGetProfile(
        LimbSide side,
        OpticalSideState state,
        OpticalFingerTrackingModifier modifier,
        GrabPoseReference reference,
        string strategyName,
        out IGripRecognitionProfile? profile,
        out IGripRecognitionStrategy? strategy)
    {
        strategy = null;
        IGripRecognitionStrategyResolver resolver = RecognitionStrategyResolver;
        if (!resolver.TryResolve(strategyName, out IGripRecognitionStrategy resolvedStrategy, out string resolveError))
        {
            profile = null;
            LogProfileFailureOnce(state, side, reference.Animation, resolveError);
            return false;
        }

        ProfileCacheKey cacheKey = new(
            reference,
            resolvedStrategy,
            modifier.OpticalFingerProjectionBindingIdentity,
            modifier.OpticalFingerProjectionBindingGeneration,
            resolvedStrategy.RecognitionSettings);
        if (state.ProfileCacheKey is ProfileCacheKey current && current.Equals(cacheKey) && state.ProfileDerivationAttempted)
        {
            profile = state.Profile;
            strategy = profile is null ? null : resolvedStrategy;
            return profile is not null;
        }

        state.Profile = null;
        state.ProfileCacheKey = cacheKey;
        state.ProfileDerivationAttempted = true;
        profile = null;

        if (TryDeriveProfile(
                side,
                modifier,
                reference,
                resolvedStrategy,
                resolvedStrategy.RecognitionSettings,
                out IGripRecognitionProfile? derived,
                out string error))
        {
            state.Profile = derived;
            state.ProfileFailureLogged = false;
            profile = derived;
            strategy = resolvedStrategy;
            return true;
        }

        LogProfileFailureOnce(state, side, reference.Animation, error);

        return false;
    }

    private bool TryDeriveProfile(
        LimbSide side,
        OpticalFingerTrackingModifier modifier,
        GrabPoseReference reference,
        IGripRecognitionStrategy strategy,
        PowerGripRecognitionSettings settings,
        out IGripRecognitionProfile? profile,
        out string error)
    {
        profile = null;

        Span<Quaternion> sideEffectiveNeutrals =
            stackalloc Quaternion[OpticalFingerProjectionBinding.FingerBonesPerSide];
        if (!modifier.TryCopyOpticalFingerEffectiveNeutrals(side, sideEffectiveNeutrals))
        {
            error = "the shared projection binding has no staged effective neutrals";
            return false;
        }

        return strategy.TryDeriveProfile(
            reference.SampledReference,
            sideEffectiveNeutrals,
            settings,
            out profile!,
            out error);
    }

    private void LogProfileFailureOnce(OpticalSideState state, LimbSide side, Animation animation, string error)
    {
        if (state.ProfileFailureLogged)
        {
            return;
        }

        state.ProfileFailureLogged = true;
        ResolveLogger()?.LogWarning(
            "Failed to derive the {Side}-hand candidate profile from Animation instance {AnimationInstanceId} " +
            "('{AnimationPath}'); optical recognition fails closed until its descriptor, binding, strategy, or settings change: {Error}.",
            side,
            animation.GetInstanceId(),
            animation.ResourcePath,
            error);
    }

    private void OnHandTrackingModeChanged()
    {
        XRManager? manager = _xrManager;
        if (manager is null)
        {
            return;
        }

        ReconcileCommittedMode(manager.HandTrackingMode);
    }

    /// <summary>
    /// Retires grabs originated by the previously committed source on an explicit committed mode transition:
    /// pending grabs cancel, held grabs release, per-side latches reset, and ownership never transfers
    /// (CTRL-002 TR6, TR8). Called from the XR-001 mode-change signal and re-checked against the
    /// authoritative runtime mode each physics tick, so a transition that arrived while dependencies were
    /// unresolved is still retired once the pipeline returns.
    /// </summary>
    private void ReconcileCommittedMode(XRHandTrackingMode authoritativeMode)
    {
        XRHandTrackingMode previousMode = _lastObservedMode;
        if (previousMode == authoritativeMode)
        {
            // Ambiguous tracking loss is not a mode transition (XR-002 TR5).
            return;
        }

        _lastObservedMode = authoritativeMode;

        ILogger<HandGrabInputCoordinator>? logger = ResolveLogger();
        logger?.LogInformation(
            "Committed hand-pose mode changed {PreviousMode} to {NewMode}; cancelling or releasing grabs " +
            "originated by {RetiredSource} and resetting per-hand recognition latches.",
            previousMode,
            authoritativeMode,
            previousMode);

        HandGrabInputSource retiredSource = ToInputSource(previousMode);
        foreach (LimbSide side in _sides)
        {
            _sideStates[(int)side].Machine.Reset(isGrabRecognised: false);

            if (_hands is not { } hands
                || !hands.TryGetHand(side, out IHand? hand)
                || hand is not IHandGrabLifecycle lifecycle
                || lifecycle.GrabLifecycle == HandGrabLifecycleState.None
                || lifecycle.GrabInputSource != retiredSource)
            {
                continue;
            }

            if (lifecycle.GrabLifecycle == HandGrabLifecycleState.Pending)
            {
                _ = lifecycle.CancelPendingGrab();
                logger?.LogInformation(
                    "Mode switch cancelled the {Side} hand's pending {Source} grab.",
                    side,
                    retiredSource);
            }
            else
            {
                hand.Release();
                logger?.LogInformation(
                    "Mode switch released the {Side} hand's held {Source} grab.",
                    side,
                    retiredSource);
            }
        }
    }

    private static HandGrabInputSource ToInputSource(XRHandTrackingMode mode)
        => mode switch
        {
            XRHandTrackingMode.Controller => HandGrabInputSource.Controller,
            XRHandTrackingMode.Optical => HandGrabInputSource.Optical,
            _ => HandGrabInputSource.None,
        };

    private bool TryResolvePipeline(out CoordinatorPipeline pipeline)
    {
        pipeline = default;

        if (!EnsureHandsResolved() || _hands is null)
        {
            LogPipelineUnavailable("the hands holder is not resolved yet");
            return false;
        }

        XRManager? manager = ResolveXrManager();
        if (manager?.Runtime is not { } runtime || !IsRuntimeValid(runtime))
        {
            LogPipelineUnavailable("the XR runtime is not initialised yet");
            return false;
        }

        if (!TryResolveModifier(out OpticalFingerTrackingModifier? modifier) || modifier is null)
        {
            LogPipelineUnavailable("the optical finger tracking modifier is not found yet");
            return false;
        }

        if (!_pipelineReady)
        {
            _pipelineReady = true;
            _pipelineUnavailableLogged = false;
            ResolveLogger()?.LogInformation(
                "Optical grab coordination pipeline ready; recognition runs per physics tick while the " +
                "committed mode is Optical.");
        }

        pipeline = new CoordinatorPipeline(_hands, runtime, runtime.OpticalHandJoints, modifier);
        return true;
    }

    private void LogPipelineUnavailable(string reason)
    {
        if (_pipelineReady || !_pipelineUnavailableLogged)
        {
            _pipelineReady = false;
            _pipelineUnavailableLogged = true;
            ResolveLogger()?.LogDebug(
                "Optical grab coordination inactive this tick: {Reason}.", reason);
        }
    }

    private IHasHands? ResolveHands()
        => HandHolderNode is null || !IsInstanceValid(HandHolderNode)
            ? GetParent()?.GetNodeOrNull<Node>("Hands") as IHasHands
            : HandHolderNode as IHasHands;

    private bool TryResolveHands(out IHasHands? hands)
    {
        hands = ResolveHands();
        return hands is not null;
    }

    private bool EnsureHandsResolved()
    {
        if (_hands is not null && (_hands is not Node handsNode || IsInstanceValid(handsNode)))
        {
            return true;
        }

        if (TryResolveHands(out _hands))
        {
            return true;
        }

        QueueHandsResolutionRetryIfNeeded();
        return false;
    }

    private void QueueHandsResolutionRetryIfNeeded()
    {
        if (_hands is not null || _handResolutionRetryQueued || !IsInsideTree())
        {
            return;
        }

        _handResolutionRetryQueued = true;
        _ = CallDeferred(MethodName.ResolveHandsAfterTreeSettled);
    }

    private void ResolveHandsAfterTreeSettled()
    {
        _handResolutionRetryQueued = false;
        if (!IsInstanceValid(this) || !IsInsideTree() || _hands is not null)
        {
            return;
        }

        // Deferred calls all run in the same idle-message flush. Calling EnsureHandsResolved here would queue
        // this method again when the holder is still unavailable, recursively filling that flush's message queue.
        // The next physics tick performs the normal retry instead.
        _ = TryResolveHands(out _hands);
    }

    private XRManager? ResolveXrManager()
    {
        if (_xrManager is not null && IsInstanceValid(_xrManager))
        {
            return _xrManager;
        }

        _xrManager = TryResolveXrManager();
        if (_xrManager is null)
        {
            return null;
        }

        if (!_modeChangeSubscribed)
        {
            _xrManager.HandTrackingModeChanged += OnHandTrackingModeChanged;
            _modeChangeSubscribed = true;
            _lastObservedMode = _xrManager.HandTrackingMode;
        }

        return _xrManager;
    }

    private static XRManager? TryResolveXrManager()
    {
        try
        {
            return Game.Instance.GetService<XRManager>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private bool TryResolveModifier(out OpticalFingerTrackingModifier? modifier)
    {
        modifier = _modifier;
        if (modifier is not null && IsInstanceValid(modifier))
        {
            return true;
        }

        if (_modifierSearchCooldown > 0)
        {
            _modifierSearchCooldown--;
            return false;
        }

        _modifier = modifier = FindFirstDescendantModifier(GetParent());
        _modifierSearchCooldown = ModifierSearchRetryCooldownTicks;
        if (modifier is not null)
        {
            ResolveLogger()?.LogDebug(
                "Optical finger tracking modifier resolved at '{ModifierPath}'; sharing its projection binding.",
                modifier.GetPath());
        }

        return modifier is not null;
    }

    private static OpticalFingerTrackingModifier? FindFirstDescendantModifier(Node? root)
    {
        if (root is null)
        {
            return null;
        }

        foreach (Node child in root.GetChildren())
        {
            if (child is OpticalFingerTrackingModifier found)
            {
                return found;
            }

            if (FindFirstDescendantModifier(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool IsRuntimeValid(IXRRuntime runtime)
        => runtime is not GodotObject godotObject || IsInstanceValid(godotObject);

    private static ulong GetIdentity(object value)
        => value is GodotObject godotObject ? godotObject.GetInstanceId() : (ulong)RuntimeHelpers.GetHashCode(value);

    private ILogger<HandGrabInputCoordinator>? ResolveLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<HandGrabInputCoordinator>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    private readonly record struct CoordinatorPipeline(
        IHasHands Hands,
        IXRRuntime Runtime,
        IXRHandJointProvider JointProvider,
        OpticalFingerTrackingModifier Modifier);

    private sealed class OpticalSideState
    {
        public readonly GripRecognitionStateMachine Machine = new(PowerGripRecognitionSettings.Default);

        public readonly XRHandJointSourceSample[] JointSamples = new XRHandJointSourceSample[TrackedJointCount];

        public readonly OpticalFingerProjectedPose[] ProjectedPoses =
            new OpticalFingerProjectedPose[OpticalFingerProjectionBinding.FingerBonesPerSide];

        public IGripRecognitionProfile? Profile
        {
            get;
            set;
        }

        public bool ProfileDerivationAttempted
        {
            get;
            set;
        }

        public ProfileCacheKey? ProfileCacheKey
        {
            get;
            set;
        }

        public bool ProfileFailureLogged
        {
            get;
            set;
        }

        public bool RecognitionInvalidLogged
        {
            get;
            set;
        }

        public OpticalGripMeasurement? LastMeasurement
        {
            get;
            set;
        }

        public ulong? ActiveAttemptID
        {
            get;
            set;
        }

    }

    private readonly record struct ProfileCacheKey(
        GrabPoseReference Reference,
        IGripRecognitionStrategy Strategy,
        ulong BindingIdentity,
        long BindingGeneration,
        PowerGripRecognitionSettings Settings);

}

/// <summary>Neutral reason associated with a completed optical grip evaluation trace.</summary>
public enum OpticalGrabEvaluationReason
{
    /// <summary>The evaluation emitted no edge and made no lifecycle transition.</summary>
    NoEdge = 0,

    /// <summary>A grab edge began a lifecycle attempt.</summary>
    GrabStarted = 1,

    /// <summary>A grab edge was rejected because a lifecycle attempt was already active.</summary>
    GrabRejectedLifecycleActive = 2,

    /// <summary>A grab edge was rejected because no candidate remained available to begin.</summary>
    GrabRejectedNoCandidate = 3,

    /// <summary>A release edge cancelled a pending attempt.</summary>
    PendingCancelled = 4,

    /// <summary>A release edge released a held attempt.</summary>
    HeldReleased = 5,

    /// <summary>A release edge found no active attempt.</summary>
    ReleaseIgnoredNoActiveGrab = 6,

    /// <summary>The projected sample did not have sufficient live validity.</summary>
    InsufficientValidity = 7,

    /// <summary>The shared projection was unavailable for this evaluation.</summary>
    ProjectionUnavailable = 8,

    /// <summary>The recognition strategy rejected the supplied profile or projected poses.</summary>
    EvaluationFailed = 9,

    /// <summary>An unrecognised edge value was ignored.</summary>
    UnsupportedEdge = 10,

    /// <summary>No eligible grab candidate was available for this tick.</summary>
    NoCandidate = 11,

    /// <summary>The selected candidate's authored recognition profile was unavailable.</summary>
    ProfileUnavailable = 12,

    /// <summary>Recognition dependencies — hands holder, XR runtime, or finger modifier — could not resolve.</summary>
    DependenciesUnavailable = 13,

    /// <summary>
    /// The hand abandoned its pending grab because the commanded approach could not converge within its bounded
    /// interval — an unreachable or collision-limited destination (IK-005 TR22).
    /// </summary>
    PendingAbandonedNonConvergence = 14,

    /// <summary>
    /// The hand abandoned its pending grab because the candidate's physics body moved persistently above the
    /// moving-candidate threshold, so the approach chased a live destination instead of converging.
    /// </summary>
    PendingAbandonedMovingCandidate = 15,
}

/// <summary>
/// Read-only evidence for one completed optical grip evaluation. Attempt IDs are coordinator-local and remain
/// stable while one lifecycle attempt moves from pending to held.
/// </summary>
public readonly record struct OpticalGrabEvaluationTrace(
    ulong EvaluationID,
    ulong PhysicsTick,
    LimbSide Side,
    GripEdge Edge,
    HandGrabLifecycleState LifecycleBefore,
    HandGrabLifecycleState LifecycleAfter,
    ulong? AttemptID,
    OpticalGrabEvaluationReason Reason);

/// <summary>
/// Read-only snapshot of one side's latest optical grip evaluation. It exposes measurement data only and is not a
/// second recognition path.
/// </summary>
public readonly record struct OpticalGripMeasurement(
    HandGrabLifecycleState Lifecycle,
    Animation? ReferenceAnimation,
    float? Score,
    bool SufficientValidity,
    float StabilitySeconds,
    GripEdge PendingEdge,
    GripEdge EmittedEdge,
    bool IsGrabRecognised,
    OpticalGrabEvaluationReason RejectionCategory)
{
    /// <summary>Builds an idle measurement for a tick with no eligible candidate.</summary>
    public static OpticalGripMeasurement NoCandidate(HandGrabLifecycleState lifecycle)
        => new(lifecycle, null, null, false, 0.0f, GripEdge.None, GripEdge.None, false,
            OpticalGrabEvaluationReason.NoCandidate);

    /// <summary>Builds an unavailable measurement when the candidate profile cannot be derived.</summary>
    public static OpticalGripMeasurement ProfileUnavailable(HandGrabLifecycleState lifecycle, Animation? referenceAnimation)
        => new(lifecycle, referenceAnimation, null, false, 0.0f, GripEdge.None, GripEdge.None, false,
            OpticalGrabEvaluationReason.ProfileUnavailable);

    /// <summary>Builds the current fail-closed measurement for a projection or evaluation rejection.</summary>
    public static OpticalGripMeasurement Invalid(
        HandGrabLifecycleState lifecycle,
        Animation? referenceAnimation,
        OpticalGrabEvaluationReason rejectionCategory)
        => new(lifecycle, referenceAnimation, null, false, 0.0f, GripEdge.None, GripEdge.None, false,
            rejectionCategory);

    /// <summary>
    /// Builds the fail-closed measurement while recognition dependencies cannot resolve, replacing any stale
    /// score, validity, or pending edge from before the loss (CTRL-002 TR15; XR-002 TR51).
    /// </summary>
    public static OpticalGripMeasurement DependenciesUnavailable(HandGrabLifecycleState lifecycle)
        => new(lifecycle, null, null, false, 0.0f, GripEdge.None, GripEdge.None, false,
            OpticalGrabEvaluationReason.DependenciesUnavailable);

    /// <summary>Builds a snapshot from the production evaluation and state-machine result.</summary>
    public static OpticalGripMeasurement FromEvaluation(
        HandGrabLifecycleState lifecycle,
        Animation referenceAnimation,
        in GripRecognitionEvaluation evaluation,
        GripRecognitionStateMachine machine,
        GripEdge emittedEdge)
        => new(
            lifecycle,
            referenceAnimation,
            evaluation.Score,
            evaluation.SufficientValidity,
            machine.StabilitySeconds,
            machine.PendingEdge,
            emittedEdge,
            machine.IsGrabRecognised,
            evaluation.SufficientValidity
                ? OpticalGrabEvaluationReason.NoEdge
                : OpticalGrabEvaluationReason.InsufficientValidity);
}
