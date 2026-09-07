using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Core.Logging;
using AlleyCat.Navigation;
using AlleyCat.Rigging;
using Godot;
using Microsoft.Extensions.Logging;

#pragma warning disable CS1591

namespace AlleyCat.IK;

/// <summary>NPC-only Phase 1 wiring, compact overlay, and observational IK-003 trace recorder.</summary>
[GlobalClass]
public partial class IK003NavigationPlaytestDiagnostics : Node
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };
    private CharacterIK? _characterIK;
    private NPCNavigation? _navigation;
    private Skeleton3D? _skeleton;
    private NPCContactLockPlaytestComposition? _composition;
    private ContactLockFootTargetProvider? _leftProvider;
    private ContactLockFootTargetProvider? _rightProvider;
    private Skeleton3D? _observedSkeleton;
    private TwoBoneIK3D? _leftNativeModifier;
    private TwoBoneIK3D? _rightNativeModifier;
    private IK003NativeSignalObservation _leftNativeSignal = IK003NativeSignalObservation.Empty;
    private IK003NativeSignalObservation _rightNativeSignal = IK003NativeSignalObservation.Empty;
    private ulong _lastWrittenPhysicsTick;
    private bool _hasWrittenPhysicsRecord;
    private Label? _lifecyclePanelLabel;
    private PanelContainer? _lifecyclePanel;
    private PanelContainer? _markerLegendPanel;
    private Node3D? _visualMarkerRoot;
    private IK003VisualChannel? _rawSourceVisual;
    private IK003VisualChannel? _anchorVisual;
    private IK003VisualChannel? _nativeTargetVisual;
    private IK003VisualChannel? _nativeEndFootVisual;
    private IK003VisualSnapshot _visualSnapshot = IK003VisualSnapshot.Empty;
    private StreamWriter? _writer;
    private ILogger<IK003NavigationPlaytestDiagnostics>? _logger;

    [Export]
    public Node? NPC
    {
        get; set;
    }
    [Export] public bool EnabledForPlaytest { get; set; } = true;
    /// <summary>Enables lower-body marker rendering only for the focused f2i_l photobooth capture route.</summary>
    public bool RenderVisualMarkers
    {
        get; private set;
    }

    public string RunID { get; } = Guid.NewGuid().ToString("N");
    public string TracePath { get; private set; } = string.Empty;
    public bool IsBound => _composition?.IsInstalled == true;
    public string VisualScenario { get; private set; } = "not_set";

    /// <summary>Flushes all emitted diagnostic records so an external evidence reader can observe a consistent checkpoint.</summary>
    public void FlushTrace() => _writer?.Flush();

    /// <summary>Closes the trace and persists all buffered records when a capture is complete.</summary>
    public void FinaliseTrace()
    {
        if (_writer is not { } writer)
        {
            return;
        }

        _writer = null;
        writer.Dispose();
    }

    /// <summary>Returns the immutable current tick state used by the lower-body diagnostic capture only.</summary>
    public Godot.Collections.Dictionary GetVisualSnapshot() => new()
    {
        ["available"] = _visualSnapshot.IsAvailable,
        ["scenario"] = VisualScenario,
        ["route"] = _navigation?.LastEvaluatedState ?? "waiting",
        ["trace_tick"] = _visualSnapshot.PhysicsTick,
        ["role"] = _visualSnapshot.Role,
        ["clip"] = _visualSnapshot.Clip,
        ["phase"] = _visualSnapshot.Phase,
        ["window_start"] = _visualSnapshot.Window.Start ?? -1.0f,
        ["window_end"] = _visualSnapshot.Window.End ?? -1.0f,
        ["lifecycle"] = _visualSnapshot.Lifecycle,
        ["selected_resolution"] = _visualSnapshot.SelectedResolution,
        ["provider_selection"] = _visualSnapshot.ProviderSelection,
        ["has_anchor"] = _visualSnapshot.HasAnchor,
        ["native_observed"] = _visualSnapshot.NativeObserved,
        ["raw_source"] = _visualSnapshot.RawSource,
        ["anchor"] = _visualSnapshot.Anchor,
        ["native_target"] = _visualSnapshot.NativeTarget,
        ["native_end_foot"] = _visualSnapshot.NativeEndFoot,
        ["foot_lock_status"] = _visualSnapshot.FootLockStatus,
        ["root_correction_status"] = _visualSnapshot.RootCorrectionStatus,
    };

    /// <summary>Returns the actual conservative marker and panel clearances used by the focused f2i_l capture.</summary>
    public Godot.Collections.Dictionary GetVisualLayout()
    {
        EnsureOverlay();
        EnsureVisualMarkers();
        return new Godot.Collections.Dictionary
        {
            ["raw_anchor_clearance_m"] = _rawSourceVisual!.MinimumClearanceTo(_anchorVisual!),
            ["anchor_native_target_clearance_m"] = _anchorVisual!.MinimumClearanceTo(_nativeTargetVisual!),
            ["native_target_end_foot_clearance_m"] = _nativeTargetVisual!.MinimumClearanceTo(_nativeEndFootVisual!),
            ["minimum_marker_foot_clearance_m"] = MathF.Min(
                MathF.Min(_rawSourceVisual!.MinimumFootClearance, _anchorVisual!.MinimumFootClearance),
                MathF.Min(_nativeTargetVisual!.MinimumFootClearance, _nativeEndFootVisual!.MinimumFootClearance)),
            ["raw_marker_elevation_m"] = _rawSourceVisual!.VerticalOffset,
            ["anchor_marker_elevation_m"] = _anchorVisual!.VerticalOffset,
            ["native_target_marker_elevation_m"] = _nativeTargetVisual!.VerticalOffset,
            ["native_end_foot_marker_elevation_m"] = _nativeEndFootVisual!.VerticalOffset,
            ["lifecycle_panel_rect"] = _lifecyclePanel!.GetGlobalRect(),
            ["marker_legend_panel_rect"] = _markerLegendPanel!.GetGlobalRect(),
            ["panel_separation_px"] = RectDistance(_lifecyclePanel!.GetGlobalRect(), _markerLegendPanel!.GetGlobalRect()),
        };
    }

    /// <summary>Shows or hides the test-only 3D channels without changing IK or provider state.</summary>
    public void SetVisualMarkerRendering(bool enabled)
    {
        RenderVisualMarkers = enabled;
        if (enabled)
        {
            EnsureVisualMarkers();
        }

        if (_visualMarkerRoot is { } visualMarkerRoot)
        {
            visualMarkerRoot.Visible = enabled;
        }
    }

    /// <summary>Labels the focused capture route without affecting contact-lock state.</summary>
    public void SetVisualScenario(string scenario) => VisualScenario = scenario;

    public override void _EnterTree() => SetPhysicsProcess(true);

    public override void _Ready()
    {
        if (!EnabledForPlaytest)
        {
            SetPhysicsProcess(false);
            return;
        }

        // Bind after navigation and contact evidence have their physics-tick state. Record emission itself waits for
        // Skeleton3D.SkeletonUpdated, which is the post-modifier boundary after native solver observations.
        ProcessPhysicsPriority = 1;
        EnsureOverlay();
    }

    public override void _PhysicsProcess(double delta)
    {
        _ = delta;
        EnsureOverlay();
        if (!IsBound && !TryBindAfterInstallers())
        {
            UpdateWaitingOverlay();
            return;
        }
    }

    public override void _ExitTree()
    {
        UnsubscribePostModifierSignals();
        FinaliseTrace();
    }

    private bool TryBindAfterInstallers()
    {
        Node? npc = NPC ?? GetParent()?.GetNodeOrNull("NavigationTestNpc");
        CharacterIK? characterIK = npc?.GetNodeOrNull<CharacterIK>("CharacterIK");
        NPCNavigation? navigation = npc?.GetNodeOrNull<NPCNavigation>("Navigation");
        Skeleton3D? skeleton = npc?.GetNodeOrNull<Skeleton3D>("Female/GeneralSkeleton");
        Node3D? leftTarget = characterIK?.LeftFootIKTarget ?? npc?.GetNodeOrNull<Node3D>("IKTargets/LeftFoot");
        Node3D? rightTarget = characterIK?.RightFootIKTarget ?? npc?.GetNodeOrNull<Node3D>("IKTargets/RightFoot");
        if (characterIK is null || leftTarget is null || rightTarget is null || navigation is null || skeleton is null)
        {
            return false;
        }

        _characterIK = characterIK;
        _navigation = navigation;
        _skeleton = skeleton;
        NPCContactLockPlaytestComposition? composition = GetParent()?.GetNodeOrNull<NPCContactLockPlaytestComposition>("NPCContactLockPlaytestComposition");
        if (composition?.IsInstalled != true || composition.LeftProvider is null || composition.RightProvider is null)
        {
            return false;
        }

        _composition = composition;
        _leftProvider = composition.LeftProvider;
        _rightProvider = composition.RightProvider;
        SubscribePostModifierSignals();
        InitialiseTrace();
        _logger ??= GameLoggerResolver.ResolveRequired<IK003NavigationPlaytestDiagnostics>();
        _logger.LogInformation("IK-003 NPC playtest diagnostics bound after installer completion; trace={TracePath}, run={RunID}.", TracePath, RunID);
        return true;
    }

    private void InitialiseTrace()
    {
        string directory = ProjectSettings.GlobalizePath("res://temp");
        _ = Directory.CreateDirectory(directory);
        TracePath = Path.Combine(directory, $"ik003_playtest_{RunID}.jsonl");
        _writer = new StreamWriter(new FileStream(TracePath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.Read));
    }

    private bool WriteRecord()
    {
        ulong physicsTick = Engine.GetPhysicsFrames();
        if (physicsTick == 0 || (_hasWrittenPhysicsRecord && physicsTick <= _lastWrittenPhysicsTick))
        {
            return false;
        }

        if (_writer is not { } writer)
        {
            return false;
        }

        NPCNavigation navigation = _navigation!;
        ContactLockCoordinator coordinator = _composition!.Coordinator!;
        ContactLockResolverTrace resolver = SelectResolverTrace(coordinator, physicsTick);
        IK003PerFootResolverSample leftResolver = BuildPerFootResolverSample(coordinator, LimbSide.Left, physicsTick);
        IK003PerFootResolverSample rightResolver = BuildPerFootResolverSample(coordinator, LimbSide.Right, physicsTick);
        ContactLockCoordinatorSnapshot coordinatorState = coordinator.StateSnapshot;
        ContactLockSnapshot selectedSupport = coordinatorState.ContributionSide == LimbSide.Left
            ? coordinatorState.Left
            : coordinatorState.ContributionSide == LimbSide.Right ? coordinatorState.Right : ContactLockSnapshot.Empty;

        ContactLockSnapshot lockState = resolver.Snapshot;
        ContactLockLiveState live = resolver.LiveState;
        LimbSide? side = resolver.IsAvailable ? resolver.Side : null;
        bool hasReturnedTarget = resolver.IsAvailable && resolver.ResolutionPublished;
        bool contactPublished = resolver.IsAvailable && resolver.ContactPublished;
        ContactLockReleaseHandoff handoff = lockState.Handoff;
        bool isReleaseHandoff = handoff.IsObserved;
        IK003NativeSignalObservation native = GetNativeSignal(side, physicsTick);
        IK003TraceErrors errors = CalculateTraceErrors(
            resolver.RawSynchronisedSource,
            lockState.Anchor,
            native.Target,
            native.EndFoot,
            lockState.HasAnchor,
            native.Observed);
        ContactLockLiveState coordinatorLive = coordinator.LiveState;
        IK003CurrentLiveObservation? currentLive = coordinatorLive.PhysicsTick == physicsTick && !coordinatorLive.Equals(live)
            ? IK003CurrentLiveObservation.From(coordinatorLive)
            : null;
        IK003PlaytestTraceV2 record = new(
            2,
            RunID,
            physicsTick,
            $"state={navigation.LastEvaluatedState};blend={navigation.LastBlendPoint};stage={navigation.ProgrammeStage}",
            IK003AnimSampleObservation.From(resolver.DerivedSample),
            isReleaseHandoff ? "non_contact_handoff" : hasReturnedTarget ? "applicable" : "refused",
            hasReturnedTarget ? null : lockState.Feasibility.ToString(),
            new IK003ActuationObservation(
                hasReturnedTarget ? ToArray(isReleaseHandoff ? handoff.ReturnedTarget.Origin : lockState.Anchor.Origin) : null,
                hasReturnedTarget,
                hasReturnedTarget ? 1.0f : 0.0f),
            new IK003SolverObservation(side.HasValue && IsFootSolveActive(side.Value), native.Target.Origin),
            new IK003PostIKObservation(native.EndFoot.Origin),
            navigation.HasDestination ? "running" : "none",
            NavigationOutcomeLabel(navigation),
            live.Role,
            live.Clip,
            live.Phase,
            side.HasValue ? side.Value.ToString().ToLowerInvariant() : "none",
            lockState.Lifecycle.ToString(),
            lockState.LifecycleTransition,
            lockState.HasAnchor,
            ToArray(lockState.Anchor.Origin),
            lockState.CaptureTick,
            ToArray(lockState.Requested.Translation),
            lockState.Requested.YawRadians,
            ToArray(lockState.Authorised.Translation),
            lockState.Authorised.YawRadians,
            ToArray(lockState.Applied.Translation),
            lockState.Applied.YawRadians,
            ToArray(lockState.Residual.Translation),
            lockState.Residual.YawRadians,
            new IK003ContactLockCaps(coordinator.MaximumTranslation, coordinator.MaximumYawRadians, coordinator.MaximumTranslationRate, coordinator.MaximumTargetDeviation),
            lockState.Feasibility.ToString(),
            lockState.SelectedResolution,
            native.Target.Origin,
            native.EndFoot.Origin,
            ToArray(errors.PostIKAnchorError),
            navigation.LastMovementCollided,
            new IK003LifecycleWindow(resolver.LifecycleWindow.Start, resolver.LifecycleWindow.End),
            ToWorldTransform(resolver.RawSynchronisedSource),
            ToWorldTransform(lockState.Anchor),
            isReleaseHandoff ? "release_handoff" : hasReturnedTarget ? "contact_lock" : "animation_synchronised_fallback",
            native,
            currentLive,
            ToArray(errors.RawSourceAnchorDeviation),
            ToArray(errors.NativeTargetEndFootError),
            contactPublished,
            ToHandoffObservation(handoff),
            leftResolver,
            rightResolver,
            new IK003SelectedSupportContribution(
                coordinatorState.ContributionSide?.ToString().ToLowerInvariant() ?? "none",
                coordinatorState.Contribution.IsAuthorised,
                ToArray(coordinatorState.Contribution.Requested.Translation),
                coordinatorState.Contribution.Requested.YawRadians,
                ToArray(coordinatorState.Contribution.Authorised.Translation),
                coordinatorState.Contribution.Authorised.YawRadians,
                ToArray(selectedSupport.Applied.Translation),
                selectedSupport.Applied.YawRadians,
                coordinatorState.Contribution.Reason.ToString()));
        writer.WriteLine(JsonSerializer.Serialize(record, _jsonOptions));
        _lastWrittenPhysicsTick = physicsTick;
        _hasWrittenPhysicsRecord = true;
        UpdateVisualDiagnostics(resolver, lockState, native, hasReturnedTarget);
        return true;
    }

    private IK003PerFootResolverSample BuildPerFootResolverSample(
        ContactLockCoordinator coordinator,
        LimbSide side,
        ulong physicsTick)
        => ContactLockPerFootTraceBuilder.Build(
            coordinator,
            side,
            physicsTick,
            GetNativeSignal(side, physicsTick),
            _characterIK?.GetFootActuationTrace(side) ?? default);

    private static IK003ReleaseHandoffObservation ToHandoffObservation(ContactLockReleaseHandoff handoff)
        => new(
            handoff.IsObserved ? (handoff.IsActive ? "active" : "complete") : "none",
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

    private ContactLockResolverTrace SelectResolverTrace(ContactLockCoordinator coordinator, ulong physicsTick)
    {
        ContactLockResolverTrace left = GetSameTickResolverTrace(coordinator.GetResolverTrace(LimbSide.Left), physicsTick);
        ContactLockResolverTrace right = GetSameTickResolverTrace(coordinator.GetResolverTrace(LimbSide.Right), physicsTick);
        if (!left.IsAvailable && !right.IsAvailable)
        {
            return CreateUnavailableTrace(coordinator, physicsTick);
        }

        // Resolver records include the full resolver sample. A matching native observation makes a candidate more
        // complete; a published resolution wins the remaining tie. Left is the stable final tie-breaker.
        return GetCompletenessScore(left, physicsTick) >= GetCompletenessScore(right, physicsTick) ? left : right;
    }

    private static ContactLockResolverTrace GetSameTickResolverTrace(ContactLockResolverTrace trace, ulong physicsTick) =>
        trace.IsAvailable && trace.PhysicsTick == physicsTick ? trace : default;

    private static ContactLockResolverTrace CreateUnavailableTrace(ContactLockCoordinator coordinator, ulong physicsTick)
    {
        ContactLockLiveState live = coordinator.LiveState.PhysicsTick == physicsTick ? coordinator.LiveState : default;
        ContactLockSnapshot snapshot = coordinator.Snapshot.LiveState.PhysicsTick == physicsTick
            ? coordinator.Snapshot
            : ContactLockSnapshot.Empty;
        return new ContactLockResolverTrace(
            false,
            physicsTick,
            default,
            live,
            Transform3D.Identity,
            default,
            snapshot,
            new DerivedPerPhysicsContactSampleV1(null, physicsTick, live.Phase, string.Empty, "absent", "unavailable"),
            false,
            false);
    }

    private int GetCompletenessScore(ContactLockResolverTrace trace, ulong physicsTick)
    {
        if (!trace.IsAvailable)
        {
            return 0;
        }

        bool hasNativeObservation = GetNativeSignal(trace.Side, physicsTick).Observed;
        return 1 + (hasNativeObservation ? 4 : 0) + (trace.ResolutionPublished ? 2 : 0);
    }

    private IK003NativeSignalObservation GetNativeSignal(LimbSide? side, ulong resolverTick)
    {
        IK003NativeSignalObservation signal = side switch
        {
            LimbSide.Left => _leftNativeSignal,
            LimbSide.Right => _rightNativeSignal,
            _ => IK003NativeSignalObservation.Empty,
        };
        return signal.Observed && signal.PhysicsTick == resolverTick ? signal : IK003NativeSignalObservation.Empty;
    }

    private bool IsFootSolveActive(LimbSide side)
    {
        string nodeName = side == LimbSide.Left ? "LeftLegTwoBoneIKController" : "RightLegTwoBoneIKController";
        return _skeleton!.GetNodeOrNull<SkeletonModifier3D>(nodeName)?.Active == true;
    }

    private void SubscribePostModifierSignals()
    {
        UnsubscribePostModifierSignals();

        _observedSkeleton = _skeleton!;
        _observedSkeleton.SkeletonUpdated += OnSkeletonUpdated;
        _leftNativeModifier = _observedSkeleton.GetNodeOrNull<TwoBoneIK3D>("LeftLegTwoBoneIKController");
        _rightNativeModifier = _observedSkeleton.GetNodeOrNull<TwoBoneIK3D>("RightLegTwoBoneIKController");
        if (_leftNativeModifier is not null)
        {
            _leftNativeModifier.ModificationProcessed += OnLeftNativeModificationProcessed;
        }

        if (_rightNativeModifier is not null)
        {
            _rightNativeModifier.ModificationProcessed += OnRightNativeModificationProcessed;
        }
    }

    private void UnsubscribePostModifierSignals()
    {
        if (_observedSkeleton is not null)
        {
            _observedSkeleton.SkeletonUpdated -= OnSkeletonUpdated;
        }

        if (_leftNativeModifier is not null)
        {
            _leftNativeModifier.ModificationProcessed -= OnLeftNativeModificationProcessed;
        }

        if (_rightNativeModifier is not null)
        {
            _rightNativeModifier.ModificationProcessed -= OnRightNativeModificationProcessed;
        }

        _observedSkeleton = null;
        _leftNativeModifier = null;
        _rightNativeModifier = null;
    }

    private void OnSkeletonUpdated()
    {
        if (IsBound && WriteRecord())
        {
            UpdateOverlay();
        }
    }

    private void OnLeftNativeModificationProcessed() => ObserveNative(LimbSide.Left);

    private void OnRightNativeModificationProcessed() => ObserveNative(LimbSide.Right);

    private void ObserveNative(LimbSide side)
    {
        Node3D target = side == LimbSide.Left ? _characterIK!.LeftFootIKTarget! : _characterIK!.RightFootIKTarget!;
        IK003WorldTransform endFoot = ToWorldTransform(GetFootTransform(side));
        var observation = new IK003NativeSignalObservation(
            true,
            Engine.GetPhysicsFrames(),
            ToWorldTransform(target.GlobalTransform),
            endFoot)
        {
            PostIKOutputObserved = true,
            PostIKOutput = endFoot,
        };
        if (side == LimbSide.Left)
        {
            _leftNativeSignal = observation;
        }
        else
        {
            _rightNativeSignal = observation;
        }
    }

    internal static IK003TraceErrors CalculateTraceErrors(
        Transform3D rawSynchronisedSource,
        Transform3D anchor,
        IK003WorldTransform nativeTarget,
        IK003WorldTransform nativeEndFoot,
        bool hasAnchor,
        bool hasNativeObservation)
    {
        if (!hasAnchor)
        {
            return default;
        }

        Vector3 rawSourceAnchorDeviation = rawSynchronisedSource.Origin - anchor.Origin;
        return !hasNativeObservation
            ? new IK003TraceErrors(rawSourceAnchorDeviation, Vector3.Zero, Vector3.Zero)
            : new IK003TraceErrors(
                rawSourceAnchorDeviation,
                ToVector3(nativeEndFoot.Origin) - ToVector3(nativeTarget.Origin),
                ToVector3(nativeEndFoot.Origin) - anchor.Origin);
    }

    private Transform3D GetFootTransform(LimbSide side)
    {
        int bone = _skeleton!.FindBone(side == LimbSide.Left ? "LeftFoot" : "RightFoot");
        return bone >= 0 ? _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) : Transform3D.Identity;
    }

    private static string NavigationOutcomeLabel(NPCNavigation navigation)
    {
        if (navigation.CurrentStatus.HasActiveRequest)
        {
            return "incomplete";
        }

        IReadOnlyList<NavigationTerminalOutcomeRecord> outcomes = navigation.TerminalOutcomes;
        return outcomes.Count == 0 ? "none" : outcomes[^1].Outcome.ToString().ToLowerInvariant();
    }

    private void EnsureOverlay()
    {
        if (_lifecyclePanelLabel is not null)
        {
            return;
        }

        var layer = new CanvasLayer { Name = "IK003PlaytestOverlay" };
        _lifecyclePanel = CreateScreenPanel(
            "IK003LifecyclePanel",
            new Vector2(18.0f, 18.0f),
            new Vector2(390.0f, 144.0f),
            out Label lifecycleLabel);
        _markerLegendPanel = CreateScreenPanel(
            "IK003MarkerLegendPanel",
            new Vector2(18.0f, 174.0f),
            new Vector2(315.0f, 112.0f),
            out Label markerLegendLabel);
        lifecycleLabel.Text = "IK-003 waiting for NPC installers…";
        markerLegendLabel.Text = "MARKER KEY (world-space)\n"
            + "red sphere = raw source\ncyan cylinder = retained anchor\n"
            + "green cube = native target\nlime ring = native end foot";
        layer.AddChild(_lifecyclePanel);
        layer.AddChild(_markerLegendPanel);
        AddChild(layer);
        _lifecyclePanelLabel = lifecycleLabel;
    }

    private void UpdateWaitingOverlay()
    {
        if (_lifecyclePanelLabel is { } lifecyclePanelLabel)
        {
            lifecyclePanelLabel.Text = "IK-003 waiting for NPC installers…";
        }
    }

    private void UpdateOverlay()
    {
        IK003VisualSnapshot visual = _visualSnapshot;
        string window = visual.Window.Start.HasValue
            ? visual.Window.End.HasValue
                ? $"[{visual.Window.Start.Value:F3},{visual.Window.End.Value:F3})"
                : $"[{visual.Window.Start.Value:F3},end)"
            : "none";
        _lifecyclePanelLabel!.Text = $"IK-003 {RunID[..8]} · {VisualScenario}\n"
                                      + $"Route: {_navigation!.LastEvaluatedState}  Tick: {visual.PhysicsTick}\n"
                                      + $"Role: {visual.Role}  Clip: {visual.Clip}  Phase: {visual.Phase:F3}\n"
                                      + $"Window: {window}  Lifecycle: {visual.Lifecycle}\n"
                                      + $"Provider: {visual.ProviderSelection} ({visual.SelectedResolution})\n"
                                      + $"Foot Lock: {visual.FootLockStatus}; Root Correction: {visual.RootCorrectionStatus}";
    }

    private static IK003Vector3 ToArray(Vector3 value) => new(value.X, value.Y, value.Z);

    private static Vector3 ToVector3(IK003Vector3 value) => new(value.X, value.Y, value.Z);

    private static IK003WorldTransform ToWorldTransform(Transform3D value)
        => new(ToArray(value.Origin), ToArray(value.Basis.Column0), ToArray(value.Basis.Column1), ToArray(value.Basis.Column2));

    private void UpdateVisualDiagnostics(
        ContactLockResolverTrace resolver,
        ContactLockSnapshot lockState,
        IK003NativeSignalObservation native,
        bool applicable)
    {
        string providerSelection = lockState.Handoff.IsObserved
            ? "release_handoff"
            : applicable ? "contact_lock" : "animation_synchronised_fallback";
        _visualSnapshot = new IK003VisualSnapshot(
            resolver.IsAvailable,
            resolver.PhysicsTick,
            resolver.LiveState.Role,
            resolver.LiveState.Clip,
            resolver.LiveState.Phase,
            resolver.LifecycleWindow,
            lockState.Lifecycle.ToString(),
            lockState.SelectedResolution,
            providerSelection,
            lockState.HasAnchor,
            native.Observed,
            resolver.RawSynchronisedSource.Origin,
            lockState.Anchor.Origin,
            ToVector3(native.Target.Origin),
            ToVector3(native.EndFoot.Origin),
            DescribeFootLock(lockState),
            DescribeRootCorrection(lockState));

        if (!RenderVisualMarkers)
        {
            return;
        }

        EnsureVisualMarkers();
        _rawSourceVisual!.Set(
            resolver.IsAvailable,
            resolver.RawSynchronisedSource.Origin);
        _anchorVisual!.Set(
            lockState.HasAnchor,
            lockState.Anchor.Origin);
        _nativeTargetVisual!.Set(
            native.Observed,
            ToVector3(native.Target.Origin));
        _nativeEndFootVisual!.Set(
            native.Observed,
            ToVector3(native.EndFoot.Origin));
    }

    private void EnsureVisualMarkers()
    {
        if (_visualMarkerRoot is not null)
        {
            return;
        }

        _visualMarkerRoot = new Node3D { Name = "IK003ContactLockVisuals", TopLevel = true };
        AddChild(_visualMarkerRoot);
        _rawSourceVisual = new IK003VisualChannel(
            _visualMarkerRoot, "RawSynchronisedSource", new Color(1.0f, 0.2f, 0.1f), 0.12f, 0.035f, new SphereMesh { Radius = 0.035f, Height = 0.07f });
        _anchorVisual = new IK003VisualChannel(
            _visualMarkerRoot, "RetainedWorldAnchor", new Color(0.1f, 0.95f, 1.0f), 0.28f, 0.035f, new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.035f, Height = 0.07f });
        _nativeTargetVisual = new IK003VisualChannel(
            _visualMarkerRoot, "NativeSignalTimeTarget", new Color(0.25f, 1.0f, 0.2f), 0.44f, 0.035f, new BoxMesh { Size = new Vector3(0.07f, 0.07f, 0.07f) });
        _nativeEndFootVisual = new IK003VisualChannel(
            _visualMarkerRoot, "NativeSignalTimeEndFoot", new Color(0.75f, 1.0f, 0.15f), 0.60f, 0.045f, new TorusMesh { InnerRadius = 0.025f, OuterRadius = 0.045f });
    }

    private static PanelContainer CreateScreenPanel(string name, Vector2 position, Vector2 size, out Label label)
    {
        var panel = new PanelContainer
        {
            Name = name,
            Position = position,
            Size = size,
            MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
        };
        var background = new StyleBoxFlat
        {
            BgColor = new Color(0.015f, 0.025f, 0.045f, 0.84f),
            BorderColor = new Color(0.25f, 0.75f, 0.9f, 0.86f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomRight = 4,
            CornerRadiusBottomLeft = 4,
        };
        panel.AddThemeStyleboxOverride("panel", background);
        label = new Label
        {
            Position = new Vector2(9.0f, 7.0f),
            Size = size - new Vector2(18.0f, 14.0f),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Godot.Control.MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 13);
        label.AddThemeColorOverride("font_color", new Color(0.96f, 0.98f, 1.0f, 1.0f));
        label.AddThemeColorOverride("font_outline_color", new Color(0.0f, 0.01f, 0.02f, 0.98f));
        label.AddThemeConstantOverride("outline_size", 2);
        panel.AddChild(label);
        return panel;
    }

    private static string DescribeFootLock(ContactLockSnapshot snapshot)
        => snapshot.Handoff.IsObserved
            ? snapshot.Handoff.IsActive ? "released; handoff" : "released; handoff complete"
            : snapshot.HasAnchor ? "active" : snapshot.Lifecycle == ContactLockLifecycle.Release ? "cleared; fallback" : "none; fallback";

    private static string DescribeRootCorrection(ContactLockSnapshot snapshot)
        => !snapshot.HasAnchor
            ? snapshot.Lifecycle == ContactLockLifecycle.Release ? "none (released)" : "none"
            : snapshot.Lifecycle == ContactLockLifecycle.Acquire
                ? "none (acquire)"
                : snapshot.Feasibility == ContactLockFeasibilityReason.Valid
                    ? snapshot.Authorised.Translation.IsZeroApprox() && Mathf.IsZeroApprox(snapshot.Authorised.YawRadians)
                        ? "none (not needed)"
                        : "authorised"
                    : $"none ({FormatFeasibility(snapshot.Feasibility)})";

    private static string FormatFeasibility(ContactLockFeasibilityReason feasibility)
        => feasibility is ContactLockFeasibilityReason.TranslationCap
            or ContactLockFeasibilityReason.YawCap
            or ContactLockFeasibilityReason.RateCap
            or ContactLockFeasibilityReason.TargetDeviationCap
                ? "cap"
                : feasibility.ToString().ToLowerInvariant();

    private static float RectDistance(Rect2 first, Rect2 second)
    {
        float horizontal = MathF.Max(MathF.Max(first.Position.X - second.End.X, second.Position.X - first.End.X), 0.0f);
        float vertical = MathF.Max(MathF.Max(first.Position.Y - second.End.Y, second.Position.Y - first.End.Y), 0.0f);
        return new Vector2(horizontal, vertical).Length();
    }

    private sealed class IK003VisualChannel
    {
        private readonly Node3D _root;
        private readonly MeshInstance3D _marker;
        private readonly MeshInstance3D _leader;
        private readonly float _markerRadius;

        public IK003VisualChannel(Node3D parent, string name, Color colour, float verticalOffset, float markerRadius, Mesh markerMesh)
        {
            VerticalOffset = verticalOffset;
            _markerRadius = markerRadius;
            _root = new Node3D { Name = name };
            parent.AddChild(_root);
            StandardMaterial3D material = new()
            {
                AlbedoColor = colour,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                EmissionEnabled = true,
                Emission = colour,
            };
            _marker = new MeshInstance3D
            {
                Mesh = markerMesh,
                MaterialOverride = material,
            };
            _leader = new MeshInstance3D
            {
                Mesh = new CylinderMesh { TopRadius = 0.006f, BottomRadius = 0.006f, Height = verticalOffset },
                MaterialOverride = material,
            };
            _root.AddChild(_leader);
            _root.AddChild(_marker);
        }

        public float MinimumFootClearance => VerticalOffset - _markerRadius - 0.04f;

        public float VerticalOffset
        {
            get;
        }

        public float MinimumClearanceTo(IK003VisualChannel other)
            => MathF.Abs(VerticalOffset - other.VerticalOffset) - _markerRadius - other._markerRadius;

        public void Set(bool visible, Vector3 markerPosition)
        {
            _root.Visible = visible;
            if (!visible)
            {
                return;
            }

            Vector3 displayPosition = markerPosition + (Vector3.Up * VerticalOffset);
            _root.GlobalPosition = displayPosition;
            _leader.Position = Vector3.Down * (VerticalOffset * 0.5f);
            _marker.Position = Vector3.Zero;
        }
    }
}

/// <summary>Versioned JSONL record written only by the NPC navigation playtest observer.</summary>
public readonly record struct IK003PlaytestTraceV2(
    int SchemaVersion,
    string RunID,
    ulong PhysicsTick,
    string RouteCommand,
    IK003AnimSampleObservation AnimSample,
    string ProviderDecision,
    string? ProviderRefusalReason,
    IK003ActuationObservation SelectedActuation,
    IK003SolverObservation NativeSolverObservation,
    IK003PostIKObservation PostIKObservation,
    string RouteState,
    string CompletionState,
    string Role,
    string Clip,
    float Phase,
    string Foot,
    string Lifecycle,
    string LifecycleTransition,
    bool AnchorActive,
    IK003Vector3 Anchor,
    ulong CaptureTick,
    IK003Vector3 RequestedTranslation,
    float RequestedYaw,
    IK003Vector3 AuthorisedTranslation,
    float AuthorisedYaw,
    IK003Vector3 AppliedTranslation,
    float AppliedYaw,
    IK003Vector3 ResidualTranslation,
    float ResidualYaw,
    IK003ContactLockCaps Caps,
    string Feasibility,
    string SelectedResolution,
    IK003Vector3 NativeSignalTarget,
    IK003Vector3 NativeSignalEndFoot,
    IK003Vector3 PostIKAnchorError,
    bool RealisedCollision,
    IK003LifecycleWindow LifecycleWindow,
    IK003WorldTransform RawSynchronisedSourceWorldTransform,
    IK003WorldTransform AnchorWorldTransform,
    string ProviderSelection,
    IK003NativeSignalObservation NativeSignalObservation,
    IK003CurrentLiveObservation? CurrentLiveObservation,
    IK003Vector3 RawSourceAnchorDeviation,
    IK003Vector3 NativeTargetEndFootError,
    bool ContactPublished,
    IK003ReleaseHandoffObservation ReleaseHandoff,
    IK003PerFootResolverSample LeftResolver,
    IK003PerFootResolverSample RightResolver,
    IK003SelectedSupportContribution SelectedSupportContribution);

public readonly record struct IK003ActuationObservation(IK003Vector3? SelectedTarget, bool TargetGate, float ModifierInfluence);
public readonly record struct IK003SolverObservation(bool Active, IK003Vector3 Target);
public readonly record struct IK003PostIKObservation(IK003Vector3 FootPosition);
public readonly record struct IK003ContactLockCaps(float Translation, float Yaw, float Rate, float TargetDeviation);
public readonly record struct IK003LifecycleWindow(float? Start, float? End);
public readonly record struct IK003WorldTransform(IK003Vector3 Origin, IK003Vector3 BasisX, IK003Vector3 BasisY, IK003Vector3 BasisZ)
{
    public static IK003WorldTransform Identity => new(
        IK003Vector3.Zero,
        IK003Vector3.UnitX,
        IK003Vector3.UnitY,
        IK003Vector3.UnitZ);
}

[JsonConverter(typeof(IK003Vector3JsonConverter))]
public readonly record struct IK003Vector3(float X, float Y, float Z)
{
    public static IK003Vector3 Zero => new(0f, 0f, 0f);
    public static IK003Vector3 UnitX => new(1f, 0f, 0f);
    public static IK003Vector3 UnitY => new(0f, 1f, 0f);
    public static IK003Vector3 UnitZ => new(0f, 0f, 1f);
    public float Magnitude => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));
    public float this[int index] => index switch
    {
        0 => X,
        1 => Y,
        2 => Z,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

public sealed class IK003Vector3JsonConverter : JsonConverter<IK003Vector3>
{
    public override IK003Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        _ = typeToConvert;
        _ = options;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("IK-003 vectors must be encoded as three-element arrays.");
        }

        _ = reader.Read();
        float x = reader.GetSingle();
        _ = reader.Read();
        float y = reader.GetSingle();
        _ = reader.Read();
        float z = reader.GetSingle();
        _ = reader.Read();
        _ = reader.TokenType == JsonTokenType.EndArray
            ? true
            : throw new JsonException("IK-003 vectors must contain exactly three elements.");
        return new(x, y, z);
    }

    public override void Write(Utf8JsonWriter writer, IK003Vector3 value, JsonSerializerOptions options)
    {
        _ = options;
        writer.WriteStartArray();
        writer.WriteNumberValue(value.X);
        writer.WriteNumberValue(value.Y);
        writer.WriteNumberValue(value.Z);
        writer.WriteEndArray();
    }
}

[JsonConverter(typeof(IK003ContactTimingJsonConverter))]
public readonly record struct IK003ContactTiming(int Count, double First, double Second)
{
    public static IK003ContactTiming From(IReadOnlyList<double> timing) => timing.Count switch
    {
        0 => default,
        1 => new(1, timing[0], 0d),
        2 => new(2, timing[0], timing[1]),
        _ => throw new InvalidOperationException("IK-003 authored contact timing supports at most two values."),
    };

    public double this[int index] => index switch
    {
        0 when Count > 0 => First,
        1 when Count > 1 => Second,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
}

public sealed class IK003ContactTimingJsonConverter : JsonConverter<IK003ContactTiming>
{
    public override IK003ContactTiming Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        _ = typeToConvert;
        _ = options;
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("IK-003 contact timing must be encoded as an array.");
        }

        double first = 0d;
        double second = 0d;
        int count = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (count >= 2)
            {
                throw new JsonException("IK-003 contact timing supports at most two values.");
            }
            if (count == 0)
            {
                first = reader.GetDouble();
            }
            else
            {
                second = reader.GetDouble();
            }
            count++;
        }
        return new(count, first, second);
    }

    public override void Write(Utf8JsonWriter writer, IK003ContactTiming value, JsonSerializerOptions options)
    {
        _ = options;
        writer.WriteStartArray();
        if (value.Count > 0)
        {
            writer.WriteNumberValue(value.First);
        }
        if (value.Count > 1)
        {
            writer.WriteNumberValue(value.Second);
        }
        writer.WriteEndArray();
    }
}

public readonly record struct IK003AuthoredContactObservation(
    int SchemaVersion,
    string Role,
    LimbSide Foot,
    string Lifecycle,
    IK003ContactTiming Timing,
    string Validity,
    float Confidence,
    string Provenance,
    string Clip,
    string Variant,
    string Reference,
    string Identity,
    string Generation)
{
    public static IK003AuthoredContactObservation From(AuthoredPerFootContactSampleV1 value)
        => new(
            value.SchemaVersion,
            value.Role,
            value.Foot,
            value.Lifecycle,
            IK003ContactTiming.From(value.Timing),
            value.Validity,
            value.Confidence,
            value.Provenance,
            value.Clip,
            value.Variant,
            value.Reference,
            value.Identity,
            value.Generation);
}

public readonly record struct IK003AnimSampleObservation(
    IK003AuthoredContactObservation? Authored,
    ulong PhysicsTick,
    double ClipTime,
    string ObservedLifecycle,
    string Freshness,
    string RuntimeValidation)
{
    public static IK003AnimSampleObservation From(DerivedPerPhysicsContactSampleV1 value)
        => new(
            value.Authored is { } authored ? IK003AuthoredContactObservation.From(authored) : null,
            value.PhysicsTick,
            value.ClipTime,
            value.ObservedLifecycle,
            value.Freshness,
            value.RuntimeValidation);
}

public readonly record struct IK003NativeSignalObservation(bool Observed, ulong PhysicsTick, IK003WorldTransform Target, IK003WorldTransform EndFoot)
{
    public static IK003NativeSignalObservation Empty => new(
        false,
        0,
        IK003WorldTransform.Identity,
        IK003WorldTransform.Identity);

    /// <summary>Whether the end-foot output was sampled in this modifier's ModificationProcessed signal.</summary>
    public bool PostIKOutputObserved
    {
        get; init;
    }

    /// <summary>Post-TwoBoneIK output sampled at the same signal as Target and EndFoot.</summary>
    public IK003WorldTransform PostIKOutput { get; init; } = EmptyTransform;

    private static IK003WorldTransform EmptyTransform
        => IK003WorldTransform.Identity;
}
public readonly record struct IK003CurrentLiveObservation(
    bool Enabled,
    string Role,
    string Clip,
    float Phase,
    string Variant,
    string Identity,
    string Generation,
    ulong PhysicsTick,
    float Delta,
    long ClockGeneration)
{
    public static IK003CurrentLiveObservation From(ContactLockLiveState value)
        => new(value.Enabled, value.Role, value.Clip, value.Phase, value.Variant, value.Identity, value.Generation, value.PhysicsTick, value.Delta, value.ClockGeneration)
        {
            ActorTransform = ContactLockPerFootTraceBuilder.ToWorldTransform(value.ActorTransform),
            FiniteClockPositionSeconds = value.FiniteClockPositionSeconds,
            FiniteClockDurationSeconds = value.FiniteClockDurationSeconds,
        };

    /// <summary>Actor transform sampled with this exact live evaluation.</summary>
    public IK003WorldTransform ActorTransform
    {
        get; init;
    }
        = IK003WorldTransform.Identity;
    public float FiniteClockPositionSeconds
    {
        get; init;
    }
    public float FiniteClockDurationSeconds
    {
        get; init;
    }
}
public readonly record struct IK003ReleaseHandoffObservation(
    string State,
    int Tick,
    float MaximumReachMetres,
    IK003WorldTransform SampledFallback,
    IK003WorldTransform ReturnedTarget,
    float ContinuityDeltaMetres,
    IK003Vector3 ReleaseOffset,
    IK003Vector3 RemainingOffset,
    float ResidualScale,
    int MinimumTicks,
    int SelectedDurationTicks,
    float MaximumPointDisplacementMetres,
    float MaximumVelocityVectorChangeMetresPerSecond,
    float VelocityVectorChangeMetresPerSecond,
    string CompletionReason);
public readonly record struct IK003PerFootResolverSample(
    bool IsAvailable,
    ulong PhysicsTick,
    string Foot,
    IK003CurrentLiveObservation LiveState,
    IK003LifecycleWindow LifecycleWindow,
    IK003AnimSampleObservation AnimSample,
    string Lifecycle,
    string LifecycleTransition,
    IK003WorldTransform RawSynchronisedSource,
    bool AnchorActive,
    IK003WorldTransform Anchor,
    ulong CaptureTick,
    string ProviderSelection,
    string ProviderDecision,
    string? ProviderRefusalReason,
    IK003Vector3 RequestedTranslation,
    float RequestedYaw,
    IK003Vector3 AuthorisedTranslation,
    float AuthorisedYaw,
    IK003Vector3 AppliedTranslation,
    float AppliedYaw,
    IK003Vector3 ResidualTranslation,
    float ResidualYaw,
    string LiveFallbackProviderSelection,
    IK003WorldTransform LiveFallback,
    bool LiveFallbackBasisUnchanged,
    IK003ReleaseHandoffObservation ReleaseHandoff,
    IK003NativeSignalObservation NativeSignalObservation,
    IK003Vector3 NativeTargetEndFootError,
    IK003Vector3 PostIKAnchorError,
    IK003Vector3 RawSourceAnchorDeviation,
    bool ContactPublished)
{
    public IK003FootActuationObservation Actuation { get; init; } = IK003FootActuationObservation.Empty;
    public IK003SignalErrorObservation SignalErrors { get; init; } = IK003SignalErrorObservation.Empty;
    public IK003PerFootCorrectionObservation Correction { get; init; } = IK003PerFootCorrectionObservation.Empty;
}
public readonly record struct IK003SelectedSupportContribution(
    string Foot,
    bool IsAuthorised,
    IK003Vector3 RequestedTranslation,
    float RequestedYaw,
    IK003Vector3 AuthorisedTranslation,
    float AuthorisedYaw,
    IK003Vector3 AppliedTranslation,
    float AppliedYaw,
    string Feasibility)
{
    public IK003Vector3 RealisedTranslation { get; init; } = IK003Vector3.Zero;
    public float RealisedYaw
    {
        get; init;
    }
    public IK003Vector3 ResidualTranslation { get; init; } = IK003Vector3.Zero;
    public float ResidualYaw
    {
        get; init;
    }
    public bool Collided
    {
        get; init;
    }
    public bool Diverged
    {
        get; init;
    }
    public string ExactOnceDisposition { get; init; } = "unavailable";
}
public readonly record struct IK003FootActuationObservation(
    bool Observed,
    ulong PhysicsTick,
    string Foot,
    string ProviderSelection,
    string ProviderIdentity,
    IK003WorldTransform SelectedTarget,
    bool TargetGate,
    float ModifierInfluence,
    bool TargetTransformApplied)
{
    public static IK003FootActuationObservation Empty => new(
        false,
        0,
        "none",
        "none",
        "none",
        IK003WorldTransform.Identity,
        false,
        0f,
        false);
}
public readonly record struct IK003SignalErrorObservation(
    IK003Vector3 TargetEndFoot,
    float TargetEndFootMagnitude,
    IK003Vector3 EndFootOutput,
    float EndFootOutputMagnitude,
    IK003Vector3 OutputAnchor,
    float OutputAnchorMagnitude,
    IK003Vector3 RawSourceAnchor,
    float RawSourceAnchorMagnitude)
{
    public static IK003SignalErrorObservation Empty => new(
        IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f);
}
public readonly record struct IK003PerFootCorrectionObservation(
    string Foot,
    ulong PhysicsTick,
    long ClockGeneration,
    long ConsumptionIdentity,
    ulong MovementOperationIdentity,
    IK003Vector3 RequestedTranslation,
    float RequestedYaw,
    IK003Vector3 AuthorisedTranslation,
    float AuthorisedYaw,
    IK003Vector3 RealisedTranslation,
    float RealisedYaw,
    IK003Vector3 AppliedTranslation,
    float AppliedYaw,
    IK003Vector3 ResidualTranslation,
    float ResidualYaw,
    bool Collided,
    bool Diverged,
    string ExactOnceDisposition)
{
    public IK003WorldTransform ActorBefore
    {
        get; init;
    }
        = IK003WorldTransform.Identity;
    public IK003WorldTransform ActorAfter
    {
        get; init;
    }
        = IK003WorldTransform.Identity;

    public static IK003PerFootCorrectionObservation Empty => new(
        "none", 0, 0, 0, 0, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f,
        IK003Vector3.Zero, 0f, IK003Vector3.Zero, 0f, false, false, "unavailable");
}
public readonly record struct IK003TraceErrors(
    Vector3 RawSourceAnchorDeviation,
    Vector3 NativeTargetEndFootError,
    Vector3 PostIKAnchorError);
public readonly record struct IK003VisualSnapshot(
    bool IsAvailable,
    ulong PhysicsTick,
    string Role,
    string Clip,
    float Phase,
    ContactLockLifecycleWindow Window,
    string Lifecycle,
    string SelectedResolution,
    string ProviderSelection,
    bool HasAnchor,
    bool NativeObserved,
    Vector3 RawSource,
    Vector3 Anchor,
    Vector3 NativeTarget,
    Vector3 NativeEndFoot,
    string FootLockStatus,
    string RootCorrectionStatus)
{
    public static IK003VisualSnapshot Empty => new(false, 0, "none", "none", -1.0f, default, "None", "fallback", "animation_synchronised_fallback", false, false, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, "none; fallback", "none");
}
