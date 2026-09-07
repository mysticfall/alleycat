using System.Diagnostics.CodeAnalysis;
using AlleyCat.Control;
using AlleyCat.Control.Hands;
using AlleyCat.IK;
using AlleyCat.Rigging;
using AlleyCat.XR;
using AlleyCat.XR.HandTracking;
using AlleyCat.XR.Mock;
using Godot;

namespace AlleyCat.Interaction.Hands;

/// <summary>
/// Test-only driver for the CTRL-002/INTR-002 optical grab interaction photobooth: it reuses the XR-002 mock
/// runtime assembly and joint-injection hooks from <see cref="OpticalHandTrackingScenarioDriver" /> and adds
/// the interaction-scenario surface the visual runner needs — player-rig attachment, projection-calibrated
/// closure ramps, grab-lifecycle and arbitration queries, and the two non-visual pose oracles (authored grab
/// references and live tracked projections).
/// </summary>
/// <remarks>
/// <para>
/// GDScript interop mirrors the XR-002 driver conventions: <see cref="string" /> side and joint names, failures
/// reported through return values plus <see cref="OpticalHandTrackingScenarioDriver.LastError" /> rather than
/// exceptions.
/// </para>
/// <para>
/// Closure calibration follows the coordinator integration fixtures: the injected flex ramp is projected
/// through the live binding staged by the installed player's <see cref="OpticalFingerTrackingModifier" /> and
/// evaluated by the production <see cref="PowerGripRecognitionStrategy" /> against the animation-derived
/// profile until the aggregate crosses the configured threshold bands, so no magic flex constants exist.
/// </para>
/// </remarks>
[GlobalClass]
public partial class OpticalGrabInteractionScenarioDriver : OpticalHandTrackingScenarioDriver
{
    private const int TrackedJointCount = 20;

    private Node? _playerRoot;
    private OpticalFingerTrackingModifier? _modifier;
    private HandPoseBehaviour? _rightHand;
    private HandPoseBehaviour? _leftHand;
    private HandGrabTargetProvider? _rightProvider;
    private HandGrabTargetProvider? _leftProvider;
    private PlayerVRIK? _playerVRIK;
    private readonly OpticalGrabEvaluationTrace?[] _lastEvaluationTraces = new OpticalGrabEvaluationTrace?[2];
    private bool _runtimeModeRelaySubscribed;
    private bool _evaluationTraceSubscribed;

    /// <summary>Whether <see cref="AttachPlayer" /> has resolved the installed rig's interaction nodes.</summary>
    [Export]
    public bool IsPlayerAttached
    {
        get;
        private set;
    }

    /// <summary>
    /// Installs the player rig through the XR-002 base driver and caches the interaction nodes the grab
    /// scenarios observe: the finger modifier, both hand behaviours, and their grab target providers.
    /// </summary>
    /// <param name="playerRoot">Root node of the instanced player character scene.</param>
    /// <returns><see langword="true" /> when the rig is attached.</returns>
    public bool AttachPlayer(Node playerRoot)
    {
        if (!InstallPlayerRig(playerRoot))
        {
            return false;
        }

        try
        {
            _modifier = playerRoot.GetNode<OpticalFingerTrackingModifier>(
                "Female/GeneralSkeleton/OpticalFingerTrackingModifier");
            _rightHand = playerRoot.GetNode<HandPoseBehaviour>("Hands/RightHand");
            _leftHand = playerRoot.GetNode<HandPoseBehaviour>("Hands/LeftHand");
            _rightProvider = playerRoot.GetNode<HandGrabTargetProvider>("VRIK/RightHandGrabProvider");
            _leftProvider = playerRoot.GetNode<HandGrabTargetProvider>("VRIK/LeftHandGrabProvider");
            _playerVRIK = playerRoot.GetNode<PlayerVRIK>("VRIK");

            if (playerRoot.GetNodeOrNull<HandGrabInputCoordinator>("HandGrabInputCoordinator") is not { } coordinator)
            {
                LastError = "The player rig is missing the HandGrabInputCoordinator node.";
                return false;
            }

            if (!_evaluationTraceSubscribed)
            {
                coordinator.OpticalGrabEvaluated += OnOpticalGrabEvaluated;
                _evaluationTraceSubscribed = true;
            }

            // The base driver's fixture assigns the runtime without XRManager.Initialise, so the production
            // runtime-to-manager mode-change relay is absent; wire it exactly like the integration fixtures'
            // TestXRManager.SetRuntime so the coordinator observes explicit mode switches.
            if (!_runtimeModeRelaySubscribed
                && Game.Instance.GetService<XRManager>() is { } xrManager
                && xrManager.Runtime is { } runtime)
            {
                runtime.HandTrackingModeChanged += () => _ = xrManager.EmitSignal(
                    XRManager.SignalName.HandTrackingModeChanged);
                _runtimeModeRelaySubscribed = true;
            }

            _playerRoot = playerRoot;
            IsPlayerAttached = true;

            return true;
        }
        catch (Exception exception)
        {
            LastError = $"Optical grab interaction driver failed to attach the player rig: {exception}";
            return false;
        }
    }

    /// <summary>
    /// Calibrates the closure ramp for one candidate animation by projecting injected flex samples through the
    /// live binding and evaluating the production recognition strategy, mirroring the coordinator integration
    /// fixtures.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="animationPath">Candidate grab animation resource path.</param>
    /// <returns><c>[closedFlex, openFlex]</c>, or an empty array on failure.</returns>
    public float[] CalibrateGrabFlex(string sideName, string animationPath)
    {
        if (!RequireAttached(out OpticalFingerTrackingModifier? modifier)
            || !TryResolveRuntime(out MockXRRuntimeNode? runtime)
            || !TryParseSide(sideName, out LimbSide side)
            || ResourceLoader.Load<Animation>(animationPath) is not { } animation)
        {
            return [];
        }

        if (!AuthoredHandPoseReferenceSampler.TrySample(animationPath, side, out AuthoredHandPoseSideReference reference, out string sampleError))
        {
            LastError = $"Reference sampling failed for '{animationPath}': {sampleError}";
            return [];
        }

        var strategy = new PowerGripRecognitionStrategy();
        Span<Quaternion> neutrals = stackalloc Quaternion[OpticalFingerProjectionBinding.FingerBonesPerSide];
        string deriveError = string.Empty;
        if (!modifier.TryCopyOpticalFingerEffectiveNeutrals(side, neutrals)
            || !strategy.TryDeriveProfile(
                reference,
                neutrals,
                PowerGripRecognitionSettings.Default,
                out IGripRecognitionProfile? profile,
                out deriveError))
        {
            LastError = $"Profile derivation failed for '{animationPath}': {deriveError}";
            return [];
        }

        PowerGripRecognitionSettings settings = PowerGripRecognitionSettings.Default;
        float closedFlex = -1.0f;
        float openFlex = 0.0f;
        for (int step = 0; step <= 80; step++)
        {
            float flex = step * 0.05f;
            _ = InjectTrackedHandPose(sideName, flex, "");
            float? score = TryEvaluateScore(runtime, modifier, strategy, profile!, side);
            if (score is null)
            {
                LastError = $"Score evaluation failed for '{animationPath}' at flex {flex:R}.";
                return [];
            }

            if (closedFlex < 0.0f && score.Value >= MathF.Min(0.95f, settings.GrabThreshold + 0.05f))
            {
                closedFlex = flex;
            }

            if (score.Value <= settings.ReleaseThreshold - 0.15f)
            {
                openFlex = flex;
            }
        }

        if (closedFlex <= 0.0f || openFlex >= closedFlex)
        {
            LastError = $"No usable threshold band found for '{animationPath}': closed {closedFlex:R}, open {openFlex:R}.";
            return [];
        }

        return [closedFlex, openFlex];
    }

    /// <summary>Gets the committed grab lifecycle state name: <c>None</c>, <c>Pending</c>, or <c>Held</c>.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Lifecycle state name, or <c>Unavailable</c> before the rig is attached.</returns>
    public string GetHandLifecycle(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand) ? hand!.GrabLifecycle.ToString() : "Unavailable";

    /// <summary>Gets the grab provenance name: <c>None</c>, <c>Controller</c>, or <c>Optical</c>.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Grab input source name, or <c>Unavailable</c> before the rig is attached.</returns>
    public string GetHandGrabInputSource(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand) ? hand!.GrabInputSource.ToString() : "Unavailable";

    /// <summary>Gets the name of the currently held grabbable node, or an empty string while nothing is held.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Held node name or <see langword="null" /> before the rig is attached.</returns>
    public string? GetHeldGrabbableName(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand) && hand!.CurrentGrabbed is Node held
            ? held.Name
            : string.Empty;

    /// <summary>Gets the resource name of the active grab animation, or an empty string while inactive.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Animation resource name or <see langword="null" /> before the rig is attached.</returns>
    public string? GetActiveGrabAnimationName(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand) ? hand!.ActiveGrabAnimation?.ResourceName ?? string.Empty : null;

    /// <summary>Gets the name of the animation carried by the currently observed best grab candidate.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Candidate animation resource name, or an empty string when no candidate exists.</returns>
    public string? GetCurrentCandidateAnimationName(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand)
            && hand!.TryGetCurrentGrabCandidate(out GrabCandidateObservation? candidate)
            && candidate?.Animation is { } animation
                ? animation.ResourceName
                : string.Empty;

    /// <summary>
    /// Gets the direct hand-attachment transform required by the currently selected candidate. This is the
    /// Movable commit gate's actual comparison target, not the VRIK target body.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>The expected direct attachment transform, or identity when no candidate is available.</returns>
    public Transform3D GetCurrentCandidateExpectedAttachmentTransform(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand)
            && hand!.TryGetPendingExpectedAttachmentTransform(out Transform3D pendingExpectedAttachment)
                ? pendingExpectedAttachment
                : TryGetHand(sideName, out hand)
            && hand!.TryGetCurrentGrabCandidate(out GrabCandidateObservation? observation)
            && observation?.Grabbable.GetGrabPoint(hand.Side, ResolveHandTransform(hand)) is GrabPointCandidate candidate
                ? candidate.GrabPointTransform * candidate.GrabPointOffsetFromHand.AffineInverse()
                : Transform3D.Identity;

    /// <summary>Begins a controller-provenance grab through the installed production hand lifecycle.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns><see langword="true"/> when the controller grab entered Pending.</returns>
    public bool BeginControllerGrab(string sideName)
        => TryGetHand(sideName, out HandPoseBehaviour? hand)
            && hand!.BeginGrab(HandGrabInputSource.Controller) is null
            && hand.GrabLifecycle == HandGrabLifecycleState.Pending;

    /// <summary>Gets the per-hand optical-grab pose-arbitration phase name.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns><c>Tracking</c>, <c>CommitBlend</c>, <c>HeldSuppressed</c>, or <c>ReleaseBlend</c>.</returns>
    public string GetGrabPoseBlendPhase(string sideName)
        => RequireAttached(out OpticalFingerTrackingModifier? modifier) && TryParseSide(sideName, out LimbSide side)
            ? modifier!.GetGrabPoseBlendPhase(side).ToString()
            : "Unavailable";

    /// <summary>Gets whether arbitration currently reports an optical grab held on this hand.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Arbitration held flag, or <see langword="false" /> when unresolved.</returns>
    public bool IsOpticalGrabHeld(string sideName)
        => TryResolveXRManager(out XRManager? xrManager)
            && TryParseSide(sideName, out LimbSide side)
            && xrManager!.OpticalGrabArbiter.GetPresentation(side).IsOpticalGrabHeld;

    /// <summary>Gets whether this hand's grab target provider override is active (a pending approach).</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Grab override flag, or <see langword="false" /> when unresolved.</returns>
    public bool IsGrabOverrideActive(string sideName)
        => TryGetProvider(sideName, out HandGrabTargetProvider? provider) && provider!.IsGrabOverrideActive;

    /// <summary>Gets the world-space position of the hand IK target node the VRIK rig moves.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Hand target world position, or <see cref="Vector3.Zero" /> when unresolved.</returns>
    public Vector3 GetHandTargetWorldPosition(string sideName)
        => _playerRoot is { } player
            && TryParseSide(sideName, out LimbSide side)
            && player.GetNodeOrNull<Node3D>(side == LimbSide.Right ? "IKTargets/RightHand" : "IKTargets/LeftHand")
                is { } target
                ? target.GlobalPosition
                : Vector3.Zero;

    /// <summary>Gets the full world transform of the live hand IK target for fixture frame conversion.</summary>
    public Transform3D GetHandTargetWorldTransform(string sideName)
        => _playerRoot is { } player
            && TryParseSide(sideName, out LimbSide side)
            && player.GetNodeOrNull<Node3D>(side == LimbSide.Right ? "IKTargets/RightHand" : "IKTargets/LeftHand")
                is { } target
                ? target.GlobalTransform
                : Transform3D.Identity;

    /// <summary>Gets the world-space position of the hand bone attachment (the solved wrist).</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Hand bone attachment world position, or <see cref="Vector3.Zero" /> when unresolved.</returns>
    public Vector3 GetHandAttachmentWorldPosition(string sideName)
        => _playerRoot is { } player
            && TryParseSide(sideName, out LimbSide side)
            && player.GetNodeOrNull<Node3D>(side == LimbSide.Right ? "Female/GeneralSkeleton/RightHand" : "Female/GeneralSkeleton/LeftHand")
                is { } attachment
                ? attachment.GlobalPosition
                : Vector3.Zero;

    /// <summary>Gets the full world transform of the solved hand bone attachment.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Hand attachment world transform, or identity when unresolved.</returns>
    public Transform3D GetHandAttachmentWorldTransform(string sideName)
        => _playerRoot is { } player
            && TryParseSide(sideName, out LimbSide side)
            && player.GetNodeOrNull<Node3D>(side == LimbSide.Right ? "Female/GeneralSkeleton/RightHand" : "Female/GeneralSkeleton/LeftHand")
                is { } attachment
                ? attachment.GlobalTransform
                : Transform3D.Identity;

    /// <summary>
    /// Captures one read-only temporal observation of the optical grab pipeline. Expected attachment is reported
    /// only as an observation and is never used to derive or alter mock input.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="item">Scenario item whose transform and parent are observed.</param>
    /// <returns>A neutral trace sample, or an empty dictionary before fixture attachment.</returns>
    public Godot.Collections.Dictionary CaptureTemporalTraceSample(string sideName, Node3D item)
    {
        if (!TryParseSide(sideName, out LimbSide side)
            || !TryGetHand(sideName, out HandPoseBehaviour? hand)
            || !TryGetProvider(sideName, out HandGrabTargetProvider? provider)
            || !TryResolveXRManager(out XRManager? xrManager)
            || _modifier is null
            || _playerVRIK is null)
        {
            return [];
        }

        OpticalGrabEvaluationTrace? evaluation = _lastEvaluationTraces[(int)side];
        _ = xrManager!.Runtime.GetHandPoseSource(side).TryGetCalibratedWristTransform(out Transform3D calibratedWrist);
        bool hasExpectedAttachment = hand!.TryGetPendingExpectedAttachmentTransform(out Transform3D expectedAttachment);
        IKTargetPipelineResult pipeline = side == LimbSide.Right
            ? _playerVRIK.RightHandTargetPipelineDebugState
            : _playerVRIK.LeftHandTargetPipelineDebugState;
        IKTargetIntent providerOutput = provider!.LastOutputIntent;
        string candidate = hand.ActiveGrabAnimation?.ResourceName ?? GetCurrentCandidateAnimationName(sideName) ?? string.Empty;
        float fingerBlend = _playerRoot?.GetNodeOrNull<AnimationTree>("AnimationTree")?.Get(
            HandPoseAnimationTreePaths.GetHandBlendParameter(side)).AsSingle() ?? 0.0f;

        return new Godot.Collections.Dictionary
        {
            ["process_frame"] = Engine.GetProcessFrames(),
            ["physics_tick"] = Engine.GetPhysicsFrames(),
            ["evaluation_id"] = evaluation?.EvaluationID ?? 0UL,
            ["attempt_id"] = evaluation?.AttemptID ?? 0UL,
            ["edge"] = evaluation?.Edge.ToString() ?? GripEdge.None.ToString(),
            ["lifecycle_before"] = evaluation?.LifecycleBefore.ToString() ?? hand.GrabLifecycle.ToString(),
            ["lifecycle"] = hand.GrabLifecycle.ToString(),
            ["candidate"] = candidate,
            ["calibrated_wrist"] = calibratedWrist,
            ["provider_command"] = provider.GrabTarget,
            ["provider_output"] = providerOutput.WorldTransform,
            ["provider_influence"] = providerOutput.DesiredInfluence,
            ["actuator_requested"] = pipeline.RequestedTarget,
            ["actuator_realised"] = pipeline.RealisedTarget,
            ["actuator_feedback_reason"] = pipeline.Feedback.Reason,
            ["actuator_feedback_delta"] = pipeline.Feedback.RequestedToRealisedDelta,
            ["actuator_feedback_error"] = pipeline.Feedback.ErrorDistance,
            ["has_expected_attachment"] = hasExpectedAttachment,
            ["expected_attachment"] = expectedAttachment,
            ["actual_attachment"] = hand.HandBoneAttachment?.GlobalTransform ?? Transform3D.Identity,
            ["item_transform"] = item.GlobalTransform,
            ["finger_blend"] = fingerBlend,
            ["presentation_phase"] = _modifier.GetGrabPoseBlendPhase(side).ToString(),
            ["item_parent"] = item.GetParent()?.GetPath().ToString() ?? string.Empty,
            ["override_active"] = provider.IsGrabOverrideActive,
        };
    }

    /// <summary>
    /// Injects an optical wrist sample that lands on the exact requested world transform, compensating the XR
    /// origin transform, the calibrated world scale, and the per-side calibration anchor at injection time —
    /// the composition the mock applies is <c>origin.Scaled(worldScale) * raw * anchor</c>.
    /// </summary>
    /// <remarks>
    /// Grab scenarios need centimetre-exact wrist placement (the ball grab reach is 0.12 m), so unlike the
    /// framing-grade <see cref="OpticalHandTrackingScenarioDriver.SetOpticalWristWorldSample" /> this variant
    /// accounts for the world scale the mock composes into every origin-space sample.
    /// </remarks>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="worldWrist">Desired world-space wrist transform.</param>
    /// <returns><see langword="true" /> when injected.</returns>
    public bool SetExactOpticalWristWorldSample(string sideName, Transform3D worldWrist)
    {
        if (!TryResolveRuntime(out MockXRRuntimeNode? runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        Transform3D anchor = Transform3D.Identity;
        if (runtime!.GetNodeOrNull<Node3D>($"{(side == LimbSide.Right ? "Right" : "Left")}OpticalHand/WristAnchor/OpticalHandAnchor")
            is { } seededAnchor)
        {
            anchor = seededAnchor.Transform;
        }

        float worldScale = runtime.WorldScale;
        Transform3D scaledOrigin = runtime.OriginNode.GlobalTransform
            .Scaled(new Vector3(worldScale, worldScale, worldScale));
        Transform3D originSpaceSample = scaledOrigin.AffineInverse() * (worldWrist * anchor.AffineInverse());
        runtime.SetOpticalWristSample(side, originSpaceSample);

        return true;
    }

    /// <summary>Gets the world-space position of the calibrated wrist the hand-pose source exposes.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Calibrated wrist world position, or <see cref="Vector3.Zero" /> when unavailable.</returns>
    public Vector3 GetCalibratedWristWorldPosition(string sideName)
        => TryResolveXRManager(out XRManager? xrManager)
            && TryParseSide(sideName, out LimbSide side)
            && xrManager!.Runtime.GetHandPoseSource(side).TryGetCalibratedWristTransform(out Transform3D wrist)
                ? wrist.Origin
                : Vector3.Zero;

    /// <summary>
    /// Samples the authored grab reference poses from a candidate animation, in the canonical
    /// <see cref="XRHandJoints.DestinationJoints" /> order — the expected held finger-pose oracle.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <param name="animationPath">Grab animation resource path.</param>
    /// <returns>Fifteen reference rotations, or an empty array on failure.</returns>
    public Godot.Collections.Array<Quaternion> SampleAuthoredGrabReference(string sideName, string animationPath)
    {
        string sampleError = string.Empty;
        if (!TryParseSide(sideName, out LimbSide side)
            || !AuthoredHandPoseReferenceSampler.TrySample(animationPath, side, out AuthoredHandPoseSideReference reference, out sampleError))
        {
            LastError = $"Authored reference sampling failed for '{animationPath}': {sampleError}";
            return [];
        }

        Godot.Collections.Array<Quaternion> rotations = [];
        foreach (Quaternion pose in reference.Poses)
        {
            rotations.Add(pose);
        }

        return rotations;
    }

    /// <summary>
    /// Projects the currently injected raw joint samples through the live shared binding — the independent
    /// oracle for where normal optical tracking writes should land.
    /// </summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns>Fifteen projected rotations, or an empty array on failure.</returns>
    public Godot.Collections.Array<Quaternion> ProjectTrackedFingerRotations(string sideName)
    {
        if (!RequireAttached(out OpticalFingerTrackingModifier? modifier)
            || !TryResolveRuntime(out MockXRRuntimeNode? runtime)
            || !TryParseSide(sideName, out LimbSide side))
        {
            return [];
        }

        var samples = new XRHandJointSourceSample[TrackedJointCount];
        for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
        {
            _ = runtime!.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
        }

        var poses = new OpticalFingerProjectedPose[OpticalFingerProjectionBinding.FingerBonesPerSide];
        if (!modifier!.TryProjectOpticalFingers(side, samples, poses))
        {
            LastError = "The shared projection binding could not project the injected samples.";
            return [];
        }

        Godot.Collections.Array<Quaternion> rotations = [];
        for (int fingerIndex = 0; fingerIndex < poses.Length; fingerIndex++)
        {
            if (!poses[fingerIndex].IsValid)
            {
                LastError = $"The projection for finger {fingerIndex} of {side} is invalid.";
                return [];
            }

            rotations.Add(poses[fingerIndex].Rotation);
        }

        return rotations;
    }

    /// <summary>Clears every tracked joint sample on one hand, invalidating that hand's optical data.</summary>
    /// <param name="sideName">Hand side: <c>Left</c> or <c>Right</c>.</param>
    /// <returns><see langword="true" /> when cleared.</returns>
    public bool ClearHandJointSamples(string sideName)
    {
        if (!TryResolveRuntime(out MockXRRuntimeNode? runtime) || !TryParseSide(sideName, out LimbSide side))
        {
            return false;
        }

        foreach (XRHandJoint joint in XRHandJoints.TrackedJoints)
        {
            runtime!.ClearHandJointSample(side, joint);
        }

        return true;
    }

    private static float? TryEvaluateScore(
        MockXRRuntimeNode runtime,
        OpticalFingerTrackingModifier modifier,
        PowerGripRecognitionStrategy strategy,
        IGripRecognitionProfile profile,
        LimbSide side)
    {
        var samples = new XRHandJointSourceSample[TrackedJointCount];
        for (int jointIndex = 0; jointIndex < TrackedJointCount; jointIndex++)
        {
            _ = runtime.TryGetJoint(side, (XRHandJoint)jointIndex, out samples[jointIndex]);
        }

        var poses = new OpticalFingerProjectedPose[OpticalFingerProjectionBinding.FingerBonesPerSide];
        return modifier.TryProjectOpticalFingers(side, samples, poses)
            && strategy.TryEvaluate(profile, poses, out GripRecognitionEvaluation evaluation)
            ? evaluation.Score
            : null;
    }

    private bool RequireAttached([NotNullWhen(true)] out OpticalFingerTrackingModifier? modifier)
    {
        if (IsPlayerAttached && _modifier is not null && IsInstanceValid(_modifier))
        {
            modifier = _modifier;
            return true;
        }

        LastError = "The optical grab interaction driver has no attached player rig; call AttachPlayer() first.";
        modifier = null;

        return false;
    }

    private bool TryGetHand(string sideName, [NotNullWhen(true)] out HandPoseBehaviour? hand)
    {
        hand = null;
        if (!TryParseSide(sideName, out LimbSide side) || !IsPlayerAttached)
        {
            return false;
        }

        HandPoseBehaviour? candidate = side == LimbSide.Right ? _rightHand : _leftHand;
        if (candidate is null || !IsInstanceValid(candidate))
        {
            return false;
        }

        hand = candidate;

        return true;
    }

    private bool TryGetProvider(string sideName, [NotNullWhen(true)] out HandGrabTargetProvider? provider)
    {
        provider = null;
        if (!TryParseSide(sideName, out LimbSide side) || !IsPlayerAttached)
        {
            return false;
        }

        HandGrabTargetProvider? candidate = side == LimbSide.Right ? _rightProvider : _leftProvider;
        if (candidate is null || !IsInstanceValid(candidate))
        {
            return false;
        }

        provider = candidate;

        return true;
    }

    private static Transform3D ResolveHandTransform(HandPoseBehaviour hand)
        => hand.HandTargetNode is { } target && IsInstanceValid(target)
            ? target.GlobalTransform
            : hand.HandBoneAttachment is { } attachment && IsInstanceValid(attachment)
            ? attachment.GlobalTransform
            : Transform3D.Identity;

    private bool TryResolveRuntime([NotNullWhen(true)] out MockXRRuntimeNode? runtime)
    {
        if (TryResolveXRManager(out XRManager? xrManager)
            && xrManager!.Runtime is MockXRRuntimeNode mock
            && IsInstanceValid(mock))
        {
            runtime = mock;
            return true;
        }

        LastError = "The mock XR runtime is not available; call Setup() first.";
        runtime = null;

        return false;
    }

    private bool TryResolveXRManager([NotNullWhen(true)] out XRManager? xrManager)
    {
        try
        {
            xrManager = Game.Instance.GetService<XRManager>();
            return xrManager is not null && IsInstanceValid(xrManager);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            LastError = $"The XR manager service is unavailable: {exception}";
            xrManager = null;

            return false;
        }
    }

    private void OnOpticalGrabEvaluated(OpticalGrabEvaluationTrace trace)
        => _lastEvaluationTraces[(int)trace.Side] = trace;

    private bool TryParseSide(string sideName, out LimbSide side)
    {
        if (string.Equals(sideName, nameof(LimbSide.Left), StringComparison.OrdinalIgnoreCase))
        {
            side = LimbSide.Left;
            return true;
        }

        if (string.Equals(sideName, nameof(LimbSide.Right), StringComparison.OrdinalIgnoreCase))
        {
            side = LimbSide.Right;
            return true;
        }

        LastError = $"Unknown hand side '{sideName}'; expected 'Left' or 'Right'.";
        side = LimbSide.Left;

        return false;
    }
}
