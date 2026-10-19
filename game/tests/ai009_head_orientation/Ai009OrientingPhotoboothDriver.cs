using System.Reflection;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Common;
using AlleyCat.IK;
using AlleyCat.Mind.Attention;
using AlleyCat.Navigation;
using AlleyCat.Vision;
using Godot;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.Testing;

/// <summary>
/// Test-only GDScript bridge for AI-009 orienting photobooth scenario control and non-visual runtime probes.
/// </summary>
/// <remarks>
/// <para>
/// The driver simulates the AI-007 gaze-selector role: it assigns and clears the gaze anchor through the installed
/// character's <see cref="IVision"/> exactly as the production selector would, while the template-composed AI-007
/// selector is disabled so it cannot override the authored scenario timeline.
/// </para>
/// <para>
/// Head probes read the solved skeleton viewpoint rather than controller internals so photobooth assertions stay
/// independent of the runtime implementation under verification. The commanded influence is exposed separately as a
/// supporting probe only.
/// </para>
/// </remarks>
[GlobalClass]
public sealed partial class Ai009OrientingPhotoboothDriver : Node
{
    private const float DisabledIntervalSeconds = 99f;
    private const string HorizontalSeekParameter = "parameters/EyesHorizontalLookSeek/seek_request";
    private const string VerticalSeekParameter = "parameters/EyesVerticalLookSeek/seek_request";
    private const string StatesPlaybackParameter = "parameters/States/playback";
    private const string CrouchClipPositionParameter = "parameters/States/StandingCrouching/CrouchAnimation/current_position";
    private const string IdleClipPositionParameter = "parameters/States/NPCIdle/Clip/current_position";

    private static readonly string[] _chainBoneNames = ["Head", "Neck", "UpperChest", "Chest", "Spine"];

    private static readonly FieldInfo _generationField = RequireObservationField("_bindingGeneration");
    private static readonly FieldInfo _sequenceField = RequireObservationField("_evaluationSequence");
    private static readonly FieldInfo _anchorField = RequireObservationField("_lastAnchor");

    private readonly Dictionary<string, CharacterRig> _characters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ChainBaseline> _chainBaselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Basis> _authoredEyeNeutrals = new(StringComparer.Ordinal);
    private bool _presentationConfigured;

    /// <summary>
    /// Commits character component projection for both photobooth NPCs, disables the template-composed AI-007
    /// selector, and configures deterministic eye presentation (no blinks, saccades, or visual surveys).
    /// </summary>
    public void Activate()
    {
        EnsureRigsCreated();
        if (_presentationConfigured)
        {
            return;
        }

        _presentationConfigured = true;

        foreach (CharacterRig rig in _characters.Values)
        {
            rig.Character.RefreshComponents();
        }

        // Disable the production selector immediately after projection (same call stack, before any process frame)
        // so its initial evaluation cannot override the driver-authored gaze timeline. The orienting controller
        // stays active: it only consumes the assignments.
        foreach (CharacterRig rig in _characters.Values)
        {
            rig.Selector.ProcessMode = ProcessModeEnum.Disabled;
        }

        foreach (CharacterRig rig in _characters.Values)
        {
            EyesBehaviour eyes = ResolveEyes(rig);
            eyes.MinimumBlinkInterval = DisabledIntervalSeconds;
            eyes.MaximumBlinkInterval = DisabledIntervalSeconds;
            eyes.SaccadeInterval = DisabledIntervalSeconds;
            eyes.SaccadeAmplitude = 0f;
            eyes.VisualSurveyIntervalSeconds = DisabledIntervalSeconds;
            eyes.AnimationTree!.Active = true;
            rig.Eyes = eyes;
            rig.RightEye = eyes.RightEye;
            CreateChainAttachments(rig);
            CaptureEyeGazeNeutral(rig);
        }
    }

    /// <summary>
    /// Deactivates both photobooth animation trees before the first engine frame can evaluate them. The
    /// production installer's deferred activation wins the startup race in a live scene tree; this runner's
    /// pre-loop context cannot, so the trees are held inert until <see cref="ActivateProductionStartup"/> switches
    /// them to the production manual processing and starts the NPC idle programme — preserving the
    /// production-validated startup outcome (StandingCrouching clip frozen at zero) without ever evaluating the
    /// unassigned crouch advance. Call immediately after tree entry, before waiting any frame.
    /// </summary>
    public void HoldAnimationTreesInert()
    {
        EnsureRigsCreated();

        foreach (CharacterRig rig in _characters.Values)
        {
            RequireAnimationTree(rig, RequireNavigation(rig)).Active = false;
        }
    }

    /// <summary>
    /// Mirrors the production-validated NPC startup semantics on both photobooth rigs. The installer-composed
    /// production NPC enters the tree with the navigation runtime binding active — the AnimationTree switched to
    /// manual processing, the NPC idle programme started, and the StandingCrouching clip frozen at zero — while an
    /// unactivated role template drifts into the unassigned StandingCrouching advance (the cold-start contamination
    /// attributed in the B0/B1 startup parity investigation). This reproduces exactly the installer's
    /// post-projection step, character component refresh followed by
    /// <see cref="NPCNavigation.ActivateRuntimeBinding"/> — the same production method
    /// <c>CharacterRuntimeSubsystemInstaller</c> invokes on installer-composed NPCs — without instantiating the
    /// role installer or any other production system. Call once after tree entry once the scene tree is
    /// iterating (the installer's deferred timing), so the crouch clip never meaningfully advances.
    /// Returns whether both rigs activated the binding successfully.
    /// </summary>
    public bool ActivateProductionStartup()
    {
        EnsureRigsCreated();

        foreach (CharacterRig rig in _characters.Values)
        {
            rig.Character.RefreshComponents();

            NPCNavigation navigation = RequireNavigation(rig);
            AnimationTree tree = RequireAnimationTree(rig, navigation);
            GD.Print(
                $"AI009_STARTUP_PRE rig={rig.Name} active={tree.Active} processing={tree.CallbackModeProcess} " +
                $"crouch_clip={tree.Get(CrouchClipPositionParameter).AsDouble():F4} physics_frames={Engine.GetPhysicsFrames()}");
            navigation.ActivateRuntimeBinding();
            GD.Print(
                $"AI009_STARTUP_POST rig={rig.Name} binding={navigation.IsRuntimeBindingActive} " +
                $"crouch_clip={tree.Get(CrouchClipPositionParameter).AsDouble():F4} physics_frames={Engine.GetPhysicsFrames()}");
            if (!navigation.IsRuntimeBindingActive)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' failed to activate the production navigation runtime "
                    + "binding; the startup state is not production-equivalent (see the engine log for the "
                    + "binding failure).");
            }
        }

        return true;
    }

    /// <summary>
    /// Verifies the production startup state held through the driver activation settle: the navigation runtime
    /// binding stays active, the AnimationTree processes manually, the state machine stays in the NPC idle state,
    /// the StandingCrouching clip stays frozen at zero, and the idle clip has advanced (production idle animates
    /// rather than holding a frozen pose). Call after the startup settle and before anchors or scenarios begin.
    /// </summary>
    public bool VerifyProductionIdleStartup()
    {
        EnsureRigsCreated();

        foreach (CharacterRig rig in _characters.Values)
        {
            NPCNavigation navigation = RequireNavigation(rig);
            AnimationTree tree = RequireAnimationTree(rig, navigation);
            if (!navigation.IsRuntimeBindingActive)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' lost the production navigation runtime binding.");
            }

            if (tree.CallbackModeProcess != AnimationMixer.AnimationCallbackModeProcess.Manual)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' AnimationTree must process manually under the production "
                    + $"startup binding (found {tree.CallbackModeProcess}).");
            }

            AnimationNodeStateMachinePlayback playback =
                tree.Get(StatesPlaybackParameter).As<AnimationNodeStateMachinePlayback>()
                    ?? throw new InvalidOperationException(
                        $"AI-009 photobooth rig '{rig.Name}' has no States playback for the startup verification.");
            using StringName currentState = playback.GetCurrentNode();
            if (currentState != NPCAnimationProgramme.IdleState)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' must start in the production idle state "
                    + $"'{NPCAnimationProgramme.IdleState}' (found '{currentState}').");
            }

            double crouchClipPosition = tree.Get(CrouchClipPositionParameter).AsDouble();
            if (Mathf.Abs(crouchClipPosition) > 0d)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' must hold the StandingCrouching clip frozen at zero under "
                    + $"the production startup binding (found {crouchClipPosition:F4} s).");
            }

            double idleClipPosition = tree.Get(IdleClipPositionParameter).AsDouble();
            if (idleClipPosition <= 0d)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth rig '{rig.Name}' must run the animating NPC idle programme (idle clip at "
                    + $"{idleClipPosition:F4} s).");
            }

            GD.Print(
                $"AI009_STARTUP_STATE rig={rig.Name} binding=true state={currentState} crouch_clip=" +
                $"{crouchClipPosition:F4} idle_clip={idleClipPosition:F4} processing={tree.CallbackModeProcess}");
        }

        return true;
    }

    private void EnsureRigsCreated()
    {
        if (_characters.Count > 0)
        {
            return;
        }

        CharacterRig tallRig = CreateRig("TallNpc", "Male");
        CharacterRig shortRig = CreateRig("ShortNpc", "Female");
        _characters[tallRig.Name] = tallRig;
        _characters[shortRig.Name] = shortRig;
    }

    private static NPCNavigation RequireNavigation(CharacterRig rig)
        => rig.Character.Navigation as NPCNavigation
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth character '{rig.Name}' must expose {nameof(NPCNavigation)} as its navigation "
                + "component for the production startup mirror.");

    private static AnimationTree RequireAnimationTree(CharacterRig rig, NPCNavigation navigation)
        => navigation.AnimationTree
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth navigation of '{rig.Name}' must bind an AnimationTree.");

    /// <summary>
    /// Attaches one <see cref="BoneAttachment3D"/> per neck-spine chain bone so the chain probes read the
    /// modifier-solved bone orientations. The skeleton's raw bone-pose query does not reflect skeleton-modifier
    /// output between updates, while bone attachments track the solved pose the renderer sees.
    /// </summary>
    private static void CreateChainAttachments(CharacterRig rig)
    {
        foreach (string boneName in _chainBoneNames)
        {
            if (rig.Skeleton.FindBone(boneName) < 0)
            {
                throw new InvalidOperationException(
                    $"AI-009 photobooth skeleton '{rig.Skeleton.Name}' has no '{boneName}' bone required by the "
                    + "chain-distribution probes.");
            }

            var attachment = new BoneAttachment3D
            {
                Name = $"Ai009ChainProbe{boneName}",
                BoneName = boneName,
            };
            rig.Skeleton.AddChild(attachment);
            rig.ChainAttachments[boneName] = attachment;
        }
    }

    /// <summary>
    /// Returns the current NPCIdle clip position in seconds for the named character — the
    /// deterministic idle-sway phase source used by phase-controlled assertion sampling.
    /// </summary>
    public float GetIdleClipPositionSeconds(string characterId)
        => RequireAnimationTree(Rig(characterId), RequireNavigation(Rig(characterId)))
            .Get(IdleClipPositionParameter)
            .AsSingle();

    /// <summary>
    /// Recaptures the neutral relative rotation between the solved viewpoint and the transform-driven right
    /// eye, used as the gaze-neutral reference by the eye-gaze probes. Call only while the eyes are known
    /// neutral (for example settled on a centred anchor); the reference is head-pose invariant because both
    /// nodes are rigid children of the same head bone.
    /// </summary>
    public void CaptureEyeGazeNeutral(string characterId) => CaptureEyeGazeNeutral(Rig(characterId));

    private static void CaptureEyeGazeNeutral(CharacterRig rig)
    {
        if (rig.RightEye is not Node3D rightEye || !IsInstanceValid(rightEye) || !rightEye.IsInsideTree())
        {
            return;
        }

        Quaternion eyeOriginRotation = rig.Viewpoint.GlobalBasis.Orthonormalized().GetRotationQuaternion();
        rig.EyeGazeNeutralRelativeRotation =
            eyeOriginRotation.Inverse() * rightEye.GlobalBasis.Orthonormalized().GetRotationQuaternion();
    }

    /// <summary>Assigns the gaze anchor for the named character, simulating the AI-007 selector role.</summary>
    public void AssignLookTarget(string characterId, VisualCue anchor)
        => Eyes(characterId).SetLookTarget(anchor);

    /// <summary>Clears the gaze anchor for the named character, simulating the AI-007 selector role.</summary>
    public void ClearLookTarget(string characterId)
        => Eyes(characterId).ClearLookTarget();

    /// <summary>Reports whether the named character currently retains an explicit gaze assignment.</summary>
    public bool HasAssignedLookTarget(string characterId)
        => Eyes(characterId).HasRuntimeLookTarget();

    /// <summary>
    /// Returns the angle in degrees between the solved head forward axis and the direction to the current gaze
    /// anchor, or -1 when no anchor is assigned. This is the gaze eccentricity the eyes must carry.
    /// </summary>
    public float GetHeadToAnchorAngleDegrees(string characterId)
    {
        CharacterRig rig = Rig(characterId);
        VisualCue? anchor = rig.Eyes?.LookTarget;
        if (anchor is null || !IsInstanceValid(anchor))
        {
            return -1f;
        }

        Vector3 headForward = -rig.Viewpoint.GlobalBasis.Orthonormalized().Z;
        Vector3 anchorDirection = (anchor.GlobalPosition - rig.Viewpoint.GlobalPosition).Normalized();
        return Mathf.RadToDeg(headForward.AngleTo(anchorDirection));
    }

    /// <summary>Returns the solved head yaw in degrees relative to the character rest frame; positive is left.</summary>
    public float GetHeadYawDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).Y);

    /// <summary>Returns the solved head pitch in degrees relative to the character rest frame; positive is up.</summary>
    public float GetHeadPitchDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).X);

    /// <summary>
    /// Returns the current head-intent influence commanded by the orienting controller, as a supporting probe that
    /// is deliberately not the primary photobooth oracle.
    /// </summary>
    public float GetInfluence(string characterId)
        => Rig(characterId).Controller.GetTargetIntent().DesiredInfluence;

    /// <summary>
    /// Returns the yaw of the orienting controller's currently published head-intent basis in degrees, relative to
    /// the character rest frame (positive to the character's left) — a supporting probe for diagnosing policy-aim
    /// versus solved-head divergence.
    /// </summary>
    public float GetIntentYawDegrees(string characterId)
        => Mathf.RadToDeg(HeadIntentBasis(characterId).GetEuler(EulerOrder.Yxz).Y);

    /// <summary>
    /// Returns the pitch of the orienting controller's currently published head-intent basis in degrees, relative to
    /// the character rest frame (positive up) — a supporting probe for diagnosing policy-aim versus solved-head
    /// divergence.
    /// </summary>
    public float GetIntentPitchDegrees(string characterId)
        => Mathf.RadToDeg(HeadIntentBasis(characterId).GetEuler(EulerOrder.Yxz).X);

    /// <summary>
    /// Passive photobooth evidence: reads the already captured Begin-stage upstream packet and published
    /// command, never resamples upstream from solved bones or advances any controller/solver. Positions are
    /// distinct diagnostics, not attainment gates. Reflection reads existing validity stamps without adding
    /// a production diagnostic API. The caller records its post-draw time alongside this returned packet.
    /// </summary>
    public string GetRestAtLimitContext(string characterId)
    {
        CharacterRig rig = Rig(characterId);
        OrientingController controller = rig.Controller;
        OrientingUpstreamSnapshot? snapshot = controller.LastUpstreamSnapshot;
        VisualCue? anchor = Eyes(characterId).LookTarget;
        var evaluatedAnchor = _anchorField.GetValue(controller) as Node3D;
        CharacterIK ik = rig.Character.RequireNode<CharacterIK>("CharacterIK");
        IKTargetIntent intent = controller.GetTargetIntent();
        IKTargetPipelineResult pipeline = ik.HeadTargetPipelineDebugState;
        ulong generation = (ulong)_generationField.GetValue(controller)!;
        ulong sequence = (ulong)_sequenceField.GetValue(controller)!;
        bool anchorValid = anchor is not null && IsInstanceValid(anchor) && anchor.IsInsideTree();
        Transform3D viewpointLocal = rig.Viewpoint.Transform;
        return JsonSerializer.Serialize(new
        {
            time_usec = Time.GetTicksUsec(),
            npc = characterId,
            valid = snapshot is not null && anchorValid && ReferenceEquals(anchor, evaluatedAnchor)
                && snapshot.Generation == generation && snapshot.EvaluationSequence == sequence
                && snapshot.SkeletonIdentity == rig.Skeleton.GetInstanceId()
                && snapshot.CharacterGlobalTransform == rig.Character.GlobalTransform
                && snapshot.SkeletonGlobalTransform == rig.Skeleton.GlobalTransform,
            generation,
            sequence,
            skeleton_id = rig.Skeleton.GetInstanceId(),
            anchor_id = anchorValid ? anchor!.GetInstanceId() : 0UL,
            evaluated_anchor_id = evaluatedAnchor is not null && IsInstanceValid(evaluatedAnchor)
                ? evaluatedAnchor.GetInstanceId() : 0UL,
            target = anchorValid ? ObservationVector(anchor!.GlobalPosition) : null,
            root = ObservationTransform(rig.Character.GlobalTransform),
            skeleton = ObservationTransform(rig.Skeleton.GlobalTransform),
            upstream = snapshot is null ? null : new
            {
                snapshot.Generation,
                snapshot.EvaluationSequence,
                snapshot.DeltaSeconds,
                snapshot.EvaluationMode,
                snapshot.SkeletonIdentity,
                snapshot.HasPivot,
                root = ObservationTransform(snapshot.CharacterGlobalTransform),
                skeleton = ObservationTransform(snapshot.SkeletonGlobalTransform),
                h = ObservationVector(snapshot.HeadOriginWorld),
                p = ObservationVector(snapshot.PivotPositionWorld),
                U = ObservationBasis(snapshot.UpstreamFaceBasisWorld)
            },
            viewpoint_local = ObservationTransform(viewpointLocal),
            viewpoint_face_offset = ObservationVector(viewpointLocal.Basis.Orthonormalized().Inverse() * viewpointLocal.Origin),
            command_face = ObservationBasis(intent.WorldTransform.Basis * viewpointLocal.Basis.Orthonormalized()),
            command_yaw_rad = controller.LastPublishedAim.HorizontalRadians,
            command_pitch_rad = controller.LastPublishedAim.VerticalRadians,
            influence = intent.DesiredInfluence,
            envelope = new[] { controller.EnvelopeHorizontalDegrees, controller.EnvelopeUpDegrees, controller.EnvelopeDownDegrees },
            requested_head_position = ObservationVector(intent.WorldTransform.Origin),
            pipeline_source_position = ObservationVector(pipeline.SourceTarget.Origin),
            mediated_requested_position = ObservationVector(pipeline.RequestedTarget.Origin),
            mediated_realised_position = ObservationVector(pipeline.RealisedTarget.Origin),
            solved_head_attachment_position = ObservationVector(rig.ChainAttachments["Head"].GlobalPosition),
            solved_viewpoint_position = ObservationVector(rig.Viewpoint.GlobalPosition),
            solved_face = ObservationBasis(rig.Viewpoint.GlobalBasis)
        });
    }

    private static FieldInfo RequireObservationField(string name)
        => typeof(OrientingController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"Missing read-only photobooth observation field '{name}'.");

    private static float[] ObservationVector(Vector3 value) => [value.X, value.Y, value.Z];

    private static float[][] ObservationBasis(Basis value)
        => [ObservationVector(value.X), ObservationVector(value.Y), ObservationVector(value.Z)];

    private static object ObservationTransform(Transform3D value)
        => new
        {
            origin = ObservationVector(value.Origin),
            basis = ObservationBasis(value.Basis)
        };

    private Basis HeadIntentBasis(string characterId)
        => Rig(characterId).Controller.GetTargetIntent().WorldTransform.Basis.Orthonormalized();

    /// <summary>
    /// Returns the horizontal eye-presentation seek value: 0.5 is eye-neutral, 1.0 is full left, 0.0 is full right.
    /// </summary>
    public float GetEyeSeekHorizontal(string characterId)
        => ReadEyeSeek(characterId, HorizontalSeekParameter);

    /// <summary>Returns the vertical eye-presentation seek value: 0.5 is eye-neutral, 1.0 is full down, 0.0 is full up.</summary>
    public float GetEyeSeekVertical(string characterId)
        => ReadEyeSeek(characterId, VerticalSeekParameter);

    /// <summary>Returns the solved viewpoint world height in metres, for geometry sanity probes.</summary>
    public float GetViewpointHeight(string characterId)
        => Rig(characterId).Viewpoint.GlobalPosition.Y;

    /// <summary>
    /// Returns the solved head roll in degrees using the same rest-frame convention as the yaw and pitch probes
    /// (YXZ euler Z of the solved viewpoint basis; the neutral head reads approximately 0). Positive tilts the
    /// crown towards the character's left (left ear dipping down).
    /// </summary>
    public float GetHeadRollDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).Z);

    /// <summary>
    /// Captures the current solved chain state as the no-gaze neutral baseline for the chain-distribution probes.
    /// Call only after the unengaged character has settled so bone deltas measure orienting distribution, not
    /// release transients. Repeated calls replace the stored baseline.
    /// </summary>
    public void CaptureChainBaseline(string characterId)
    {
        CharacterRig rig = Rig(characterId);

        Dictionary<string, float> bonePitches = new(StringComparer.Ordinal);
        foreach (string boneName in _chainBoneNames)
        {
            bonePitches[boneName] = ReadBonePitchDegrees(rig, boneName);
        }

        Vector3 faceEuler = HeadEuler(characterId);
        _chainBaselines[rig.Name] = new ChainBaseline(
            bonePitches,
            Mathf.RadToDeg(faceEuler.X),
            Mathf.RadToDeg(faceEuler.Z));
    }

    /// <summary>
    /// Returns the solved pitch delta in degrees (YXZ euler X of the chain bone attachment's world basis) of the
    /// named chain bone relative to the captured no-gaze baseline; positive pitches the bone up.
    /// </summary>
    public float GetChainBonePitchDeltaDegrees(string characterId, string boneName)
        => ReadBonePitchDegrees(Rig(characterId), boneName)
            - (Baseline(characterId).BonePitchDegrees.TryGetValue(boneName, out float baselinePitch)
                ? baselinePitch
                : throw new InvalidOperationException(
                    $"AI-009 photobooth chain baseline has no '{boneName}' bone entry."));

    /// <summary>
    /// Returns the solved viewpoint pitch delta in degrees relative to the captured no-gaze baseline; this is the
    /// total face pitch the neck-spine chain distributed.
    /// </summary>
    public float GetFacePitchDeltaDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).X) - Baseline(characterId).FacePitchDegrees;

    /// <summary>Returns the solved viewpoint roll delta in degrees relative to the captured no-gaze baseline.</summary>
    public float GetFaceRollDeltaDegrees(string characterId)
        => Mathf.RadToDeg(HeadEuler(characterId).Z) - Baseline(characterId).FaceRollDegrees;

    /// <summary>
    /// Returns the yaw of the assigned gaze anchor in the current solved head frame (positive to the character's
    /// left), or NaN when no anchor is assigned.
    /// </summary>
    public float GetAnchorYawInHeadDegrees(string characterId)
    {
        CharacterRig rig = Rig(characterId);
        VisualCue? anchor = rig.Eyes?.LookTarget;
        if (anchor is null || !IsInstanceValid(anchor))
        {
            return float.NaN;
        }

        Vector3 local = rig.Viewpoint.GlobalTransform.AffineInverse() * anchor.GlobalPosition;
        return Mathf.RadToDeg(Mathf.Atan2(-local.X, -local.Z));
    }

    /// <summary>Returns the horizontal eye clamp in degrees — the seek-value to angle mapping denominator.</summary>
    public float GetEyeMaxHorizontalAngleDegrees(string characterId)
        => Eyes(characterId).MaxHorizontalAngleDegrees;

    /// <summary>Returns the vertical eye clamp in degrees — the seek-value to angle mapping denominator.</summary>
    public float GetEyeMaxVerticalAngleDegrees(string characterId)
        => Eyes(characterId).MaxVerticalAngleDegrees;

    /// <summary>
    /// Returns the rendered right-eye gaze yaw in the head frame in degrees, measured directly from the solved
    /// eye-node transform relative to the captured gaze-neutral reference (positive looks to the character's
    /// left); NaN when the character has no transform-driven eye nodes.
    /// </summary>
    public float GetEyeGazeYawDegrees(string characterId)
        => EyeGazeDelta(characterId) is { } delta
            ? Mathf.RadToDeg(new Basis(delta).GetEuler(EulerOrder.Yxz).Y)
            : float.NaN;

    /// <summary>
    /// Both presented optical contributions relative to immutable scene-authored neutrals, in the current
    /// solved Viewpoint frame. X is signed leftward yaw, Y is signed upward pitch; live calibration is unused.
    /// </summary>
    public Godot.Collections.Dictionary GetAuthoredEyeContributions(string characterId)
    {
        if (characterId != "ShortNpc")
        {
            throw new InvalidOperationException("The authored yaw-contribution acceptance fixture is reference female only.");
        }
        CharacterRig rig = Rig(characterId);
        Godot.Collections.Dictionary result = [];
        foreach (string eyeName in new[] { "LeftEye", "RightEye" })
        {
            string key = characterId + "/" + eyeName;
            if (!_authoredEyeNeutrals.TryGetValue(key, out Basis neutral))
            {
                const string path = "res://assets/characters/templates/reference_female/reference_female_base.tscn";
                PackedScene packed = ResourceLoader.Load<PackedScene>(path)
                    ?? throw new InvalidOperationException($"Missing authored eye-reference scene '{path}'.");
                SceneState state = packed.GetState();
                bool found = false;
                for (int node = 0; node < state.GetNodeCount() && !found; node++)
                {
                    if (!state.GetNodePath(node).ToString().EndsWith("/Head/Eyes/" + eyeName, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    for (int property = 0; property < state.GetNodePropertyCount(node); property++)
                    {
                        if (state.GetNodePropertyName(node, property) != "transform")
                        {
                            continue;
                        }
                        Variant value = state.GetNodePropertyValue(node, property);
                        if (value.VariantType != Variant.Type.Transform3D)
                        {
                            throw new InvalidOperationException($"Authored optical reference '{key}' is not a transform.");
                        }
                        neutral = RequireOpticalBasis(value.AsTransform3D().Basis);
                        found = true;
                        break;
                    }
                }
                if (!found)
                {
                    throw new InvalidOperationException($"Missing immutable authored optical reference '{key}'.");
                }
                _authoredEyeNeutrals.Add(key, neutral);
            }
            Node3D eye = rig.Viewpoint.GetParent().RequireNode<Node3D>("Eyes/" + eyeName);
            result[eyeName] = MeasureAuthoredEyeContribution(rig.Viewpoint.GlobalBasis,
                ((Node3D)eye.GetParent()).GlobalBasis, neutral, eye.GlobalBasis);
        }
        return result;
    }

    /// <summary>Independent optical-ray measurement; no production gaze helper or seek-channel inference.</summary>
    public static Vector2 MeasureAuthoredEyeContribution(Basis face, Basis parent, Basis neutral, Basis actual)
    {
        Basis inverseFace = RequireOpticalBasis(face).Inverse();
        Vector3 referenceRay = inverseFace * RequireOpticalBasis(parent) * RequireOpticalBasis(neutral) * Vector3.Forward;
        Vector3 actualRay = inverseFace * RequireOpticalBasis(actual) * Vector3.Forward;
        float yaw = Mathf.Wrap(Mathf.Atan2(-actualRay.X, -actualRay.Z)
            - Mathf.Atan2(-referenceRay.X, -referenceRay.Z), -Mathf.Pi, Mathf.Pi);
        float pitch = Mathf.Atan2(actualRay.Y, Mathf.Sqrt((actualRay.X * actualRay.X) + (actualRay.Z * actualRay.Z)))
            - Mathf.Atan2(referenceRay.Y, Mathf.Sqrt((referenceRay.X * referenceRay.X) + (referenceRay.Z * referenceRay.Z)));
        return new Vector2(Mathf.RadToDeg(yaw), Mathf.RadToDeg(pitch));
    }

    private static Basis RequireOpticalBasis(Basis basis)
        => basis.X.IsFinite() && basis.Y.IsFinite() && basis.Z.IsFinite() && basis.Determinant() > 1e-6f
            ? basis.Orthonormalized()
            : throw new InvalidOperationException("Optical contribution requires finite, nondegenerate authored/current bases.");

    /// <summary>
    /// Returns the rendered right-eye gaze pitch in the head frame in degrees, measured directly from the
    /// solved eye-node transform relative to the captured gaze-neutral reference (positive looks up); NaN
    /// when the character has no transform-driven eye nodes.
    /// </summary>
    public float GetEyeGazePitchDegrees(string characterId)
        => EyeGazeDelta(characterId) is { } delta
            ? Mathf.RadToDeg(new Basis(delta).GetEuler(EulerOrder.Yxz).X)
            : float.NaN;

    /// <summary>
    /// Returns one per-frame transit sample for head-turn eye-lag analysis: solved head yaw/pitch/roll, the
    /// rendered eye gaze measured from the solved eye-node transform, the AnimationTree seek values (stale by
    /// construction under the transform-driven backend, kept to document that), the controller-reported
    /// smoothed seek state, the anchor direction in the head frame, and the derived world-frame gaze angles.
    /// </summary>
    /// <remarks>
    /// World gaze composes the solved head orientation with the measured eye-in-head rotation:
    /// <c>world_gaze_yaw = head_yaw + eye_yaw</c> and <c>world_gaze_pitch = head_pitch + eye_pitch</c>, both
    /// positive to the character's left and up. The controller state mapping, for cross-checks, is
    /// <c>eye_yaw = (seek_state - 0.5) * 2 * max_horizontal</c> and
    /// <c>eye_pitch = (0.5 - seek_state) * 2 * max_vertical</c> degrees.
    /// </remarks>
    public Godot.Collections.Dictionary GetTransitSample(string characterId)
    {
        EyesBehaviour eyes = Eyes(characterId);
        Vector3 headEuler = HeadEuler(characterId);

        float yaw = Mathf.RadToDeg(headEuler.Y);
        float pitch = Mathf.RadToDeg(headEuler.X);
        float eyeYaw = GetEyeGazeYawDegrees(characterId);
        float eyePitch = GetEyeGazePitchDegrees(characterId);

        return new Godot.Collections.Dictionary
        {
            ["frame"] = Engine.GetProcessFrames(),
            ["yaw"] = yaw,
            ["pitch"] = pitch,
            ["roll"] = Mathf.RadToDeg(headEuler.Z),
            ["eye_yaw"] = eyeYaw,
            ["eye_pitch"] = eyePitch,
            ["eye_h"] = ReadEyeSeek(characterId, HorizontalSeekParameter),
            ["eye_v"] = ReadEyeSeek(characterId, VerticalSeekParameter),
            ["eye_h_smoothed"] = eyes.GetHorizontalLookSeekTime(),
            ["eye_v_smoothed"] = eyes.GetVerticalLookSeekTime(),
            ["head_to_anchor"] = GetHeadToAnchorAngleDegrees(characterId),
            ["anchor_yaw"] = GetAnchorYawInHeadDegrees(characterId),
            ["world_gaze_yaw"] = yaw + eyeYaw,
            ["world_gaze_pitch"] = pitch + eyePitch,
        };
    }

    /// <summary>
    /// Returns the world-frame yaw in degrees of the rendered right-eye forward direction, positive to the
    /// character's left, measured directly from the solved eye-node world basis. Unlike the head+eye euler
    /// composition in <see cref="GetTransitSample"/>, this probe carries no cross-axis decomposition
    /// artefacts, so per-frame deltas are the honest oracle for world-gaze rotation direction; NaN when the
    /// character has no transform-driven eye nodes.
    /// </summary>
    public float GetEyeWorldGazeYawDegrees(string characterId)
        => Rig(characterId).RightEye is Node3D rightEye && IsInstanceValid(rightEye) && rightEye.IsInsideTree()
            ? WorldYawDegrees(-rightEye.GlobalBasis.Orthonormalized().Z)
            : float.NaN;

    /// <summary>
    /// Returns the world-frame yaw in degrees of the solved viewpoint forward direction, positive to the
    /// character's left — the same axis <see cref="GetEyeWorldGazeYawDegrees"/> reports, for measuring head
    /// motion direction against world-gaze motion.
    /// </summary>
    public float GetHeadWorldYawDegrees(string characterId)
        => WorldYawDegrees(-Rig(characterId).Viewpoint.GlobalBasis.Orthonormalized().Z);

    private static float WorldYawDegrees(Vector3 worldDirection)
        => worldDirection.LengthSquared() < 1e-10f
            ? 0f
            : Mathf.RadToDeg(Mathf.Atan2(-worldDirection.X, -worldDirection.Z));

    private CharacterRig CreateRig(string nodeName, string bodyChildName)
    {
        CharacterHub character = this.RequireNode<CharacterHub>($"../Subject/{nodeName}");
        CharacterIK characterIk = this.RequireNode<CharacterIK>($"../Subject/{nodeName}/CharacterIK");
        if (characterIk.HeadTargetIntentProvider is not OrientingController controller)
        {
            throw new InvalidOperationException(
                $"AI-009 photobooth character '{nodeName}' must wire an {nameof(OrientingController)} into its "
                + $"{nameof(CharacterIK.HeadTargetIntentProvider)} slot.");
        }

        AttentionGazeTargetSelector selector =
            this.RequireNode<AttentionGazeTargetSelector>($"../Subject/{nodeName}/Mind/AttentionGazeTargetSelector");
        Skeleton3D skeleton =
            this.RequireNode<Skeleton3D>($"../Subject/{nodeName}/{bodyChildName}/GeneralSkeleton");
        Marker3D viewpoint =
            this.RequireNode<Marker3D>($"../Subject/{nodeName}/{bodyChildName}/GeneralSkeleton/Head/Viewpoint");

        // Vision resolution must wait for component projection, so the eyes capability is bound after
        // RefreshComponents through ResolveEyes.
        return new CharacterRig(nodeName, character, controller, selector, viewpoint, skeleton);
    }

    private static EyesBehaviour ResolveEyes(CharacterRig rig)
    {
        return ((ICharacter)rig.Character).RequireVision() as EyesBehaviour
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth character '{rig.Name}' must expose {nameof(EyesBehaviour)} as its IVision.");
    }

    private Vector3 HeadEuler(string characterId)
        => Rig(characterId).Viewpoint.GlobalBasis.Orthonormalized().GetEuler(EulerOrder.Yxz);

    private Quaternion? EyeGazeDelta(string characterId)
    {
        CharacterRig rig = Rig(characterId);
        if (rig.RightEye is not Node3D rightEye || !IsInstanceValid(rightEye) || !rightEye.IsInsideTree())
        {
            return null;
        }

        Quaternion eyeOriginRotation = rig.Viewpoint.GlobalBasis.Orthonormalized().GetRotationQuaternion();
        Quaternion relative =
            eyeOriginRotation.Inverse() * rightEye.GlobalBasis.Orthonormalized().GetRotationQuaternion();
        return relative * rig.EyeGazeNeutralRelativeRotation.Inverse();
    }

    private static float ReadBonePitchDegrees(CharacterRig rig, string boneName)
        => rig.ChainAttachments.TryGetValue(boneName, out BoneAttachment3D? attachment)
            ? Mathf.RadToDeg(attachment.GlobalBasis.Orthonormalized().GetEuler(EulerOrder.Yxz).X)
            : throw new InvalidOperationException(
                $"AI-009 photobooth chain probe has no '{boneName}' bone attachment.");

    private ChainBaseline Baseline(string characterId)
        => _chainBaselines.GetValueOrDefault(Rig(characterId).Name)
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth driver has no captured chain baseline for '{characterId}'. "
                + $"Call {nameof(CaptureChainBaseline)} first after neutral settle.");

    private float ReadEyeSeek(string characterId, string parameter)
        => Eyes(characterId).AnimationTree?.Get(parameter).AsSingle() ?? float.NaN;

    private CharacterRig Rig(string characterId)
        => _characters.GetValueOrDefault(characterId)
            ?? throw new InvalidOperationException(
                $"AI-009 photobooth driver has no registered character '{characterId}'. Call Activate() first.");

    private EyesBehaviour Eyes(string characterId)
        => Rig(characterId).Eyes ?? throw new InvalidOperationException(
            $"AI-009 photobooth character '{characterId}' has no resolved eyes capability. Call Activate() first.");

    private sealed class CharacterRig(
        string name,
        CharacterHub character,
        OrientingController controller,
        AttentionGazeTargetSelector selector,
        Marker3D viewpoint,
        Skeleton3D skeleton)
    {
        public string Name => name;

        public CharacterHub Character => character;

        public OrientingController Controller => controller;

        public AttentionGazeTargetSelector Selector => selector;

        public Marker3D Viewpoint => viewpoint;

        public Skeleton3D Skeleton => skeleton;

        public Dictionary<string, BoneAttachment3D> ChainAttachments { get; } = new(StringComparer.Ordinal);

        public Node3D? RightEye
        {
            get; set;
        }

        public Quaternion EyeGazeNeutralRelativeRotation
        {
            get; set;
        } = Quaternion.Identity;

        public EyesBehaviour? Eyes
        {
            get; set;
        }
    }

    private sealed class ChainBaseline(
        IReadOnlyDictionary<string, float> bonePitchDegrees,
        float facePitchDegrees,
        float faceRollDegrees)
    {
        public IReadOnlyDictionary<string, float> BonePitchDegrees => bonePitchDegrees;

        public float FacePitchDegrees => facePitchDegrees;

        public float FaceRollDegrees => faceRollDegrees;
    }
}
