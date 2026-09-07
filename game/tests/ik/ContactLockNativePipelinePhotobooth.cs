using AlleyCat.IK;
using AlleyCat.Rigging.Physics;
using Godot;

#pragma warning disable CS1591, IDE0007, IDE0008, IDE0048, IDE0052, IDE0055, IDE0058, IDE0072, IDE0290

namespace AlleyCat.Tests.IK;

/// <summary>
/// Test-only presenter for the native provider-to-<see cref="TwoBoneIK3D"/> Gate A/B fixture.
/// It deliberately reuses the neutral fixture topology, not a production character scene.
/// </summary>
[GlobalClass]
public partial class ContactLockNativePipelinePhotobooth : Node3D
{
    public const float FootTargetToleranceMetres = 0.006f;
    public const float SignalTargetToleranceMetres = 0.0001f;

    private const string FixtureScenePath = "res://tests/ik/contact_lock_leg_solve_fixture.tscn";
    private readonly Dictionary<string, RolePipeline> _roles = [];
    private Label3D? _scenarioLabel;

    public bool IsConfigured { get; private set; }
    public bool HasCompletedInitialNativeSettlement { get; private set; }

    public override void _Ready() => Configure();

    public override void _Process(double delta)
    {
        if (!IsConfigured)
        {
            return;
        }

        foreach (RolePipeline role in _roles.Values)
        {
            role.UpdateVisibleEvidence();
        }

        UpdateScenarioLabelPosition();
    }

    /// <summary>Applies the visual Gate A state: Player-left anchor and NPC-right anchor.</summary>
    public void ApplyGateA()
    {
        EnsureConfigured();

        RolePipeline player = _roles["Player"];
        RolePipeline npc = _roles["NPC"];
        player.Apply(Foot.Left, anchored: true);
        player.Apply(Foot.Right, anchored: false);
        npc.Apply(Foot.Left, anchored: false);
        npc.Apply(Foot.Right, anchored: true);

        _scenarioLabel!.Text = "GATE A — NATIVE PROVIDER → TWO-BONE IK\n"
            + "PLAYER LEFT = GREEN ANCHOR; PLAYER RIGHT = ORANGE FALLBACK\n"
            + "NPC RIGHT = GREEN ANCHOR; NPC LEFT = ORANGE FALLBACK\n"
            + "CYAN CUBE = TARGET • MAGENTA SPHERE = END-BONE • YELLOW SPHERE = POLE\n"
            + "WHITE STICKS + BLUE SPHERE = NATIVE SOLVED LEG + KNEE";
    }

    /// <summary>
    /// Applies the first paired Gate B fixture state. It samples Player-left fallback masking and NPC-right missing
    /// evidence while retaining independent refused cases for the opposite feet. This is visual test evidence only;
    /// it does not express production contact policy.
    /// </summary>
    public void ApplyGateBScenarioA()
    {
        EnsureConfigured();
        ApplyGateBCase("Player", Foot.Left, ContactLockGateBInjectedEvidence.FallbackMasking);
        ApplyGateBCase("Player", Foot.Right, ContactLockGateBInjectedEvidence.Invalid);
        ApplyGateBCase("NPC", Foot.Left, ContactLockGateBInjectedEvidence.Stale);
        ApplyGateBCase("NPC", Foot.Right, ContactLockGateBInjectedEvidence.Missing);
        SetGateBScenarioLabel(
            "SCENARIO A — PLAYER LEFT: FALLBACK-MASKING • NPC RIGHT: MISSING",
            "ORDINARY FALLBACK + FRESH NATIVE SOLVE SUCCEED; CONTACT REMAINS REFUSED/BLOCKED");
    }

    /// <summary>
    /// Applies the second paired Gate B fixture state. It samples Player-right stale and NPC-left contradictory
    /// evidence while retaining independent refused cases for the opposite feet. This is visual test evidence only;
    /// it does not express production contact policy.
    /// </summary>
    public void ApplyGateBScenarioB()
    {
        EnsureConfigured();
        ApplyGateBCase("Player", Foot.Left, ContactLockGateBInjectedEvidence.Missing);
        ApplyGateBCase("Player", Foot.Right, ContactLockGateBInjectedEvidence.Stale);
        ApplyGateBCase("NPC", Foot.Left, ContactLockGateBInjectedEvidence.Contradictory);
        ApplyGateBCase("NPC", Foot.Right, ContactLockGateBInjectedEvidence.FallbackMasking);
        SetGateBScenarioLabel(
            "SCENARIO B — PLAYER RIGHT: STALE • NPC LEFT: CONTRADICTORY",
            "ORDINARY FALLBACK + FRESH NATIVE SOLVE SUCCEED; CONTACT REMAINS REFUSED/BLOCKED");
    }

    /// <summary>Applies the neutral, all-ordinary-target framing state before the Gate A capture.</summary>
    public void ApplyAllFallback()
    {
        EnsureConfigured();
        foreach (RolePipeline role in _roles.Values)
        {
            role.Apply(Foot.Left, anchored: false);
            role.Apply(Foot.Right, anchored: false);
        }

        _scenarioLabel!.Text = "BASELINE — NATIVE PROVIDER → TWO-BONE IK\n"
            + "PLAYER + NPC: BOTH FEET = ORANGE ORDINARY FALLBACK\n"
            + "CYAN CUBE = TARGET • MAGENTA SPHERE = END-BONE • YELLOW SPHERE = POLE\n"
            + "WHITE STICKS + BLUE SPHERE = NATIVE SOLVED LEG + KNEE";
    }

    /// <summary>Returns the current end-bone error for the named visual role and foot.</summary>
    public float GetEndTargetError(string role, string foot) => GetRole(role).EndTargetError(ParseFoot(foot));

    /// <summary>Returns the target hand-off error sampled at the native TwoBoneIK3D completion signal.</summary>
    public float GetSignalTargetError(string role, string foot) => GetRole(role).SignalTargetError(ParseFoot(foot));

    /// <summary>Returns whether the test-only contact provider published the anchor for the named role and foot.</summary>
    public bool IsAnchorPublished(string role, string foot) => GetRole(role).IsAnchorPublished(ParseFoot(foot));

    /// <summary>Returns fixture-only injected Gate B evidence status for the named role and foot.</summary>
    public string GetGateBInjectedEvidenceStatus(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).InjectedEvidence.ToString();

    /// <summary>Returns the fixture-only provider selected after the refused contact evidence.</summary>
    public string GetGateBSelectedProvider(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).SelectedProvider.ToString();

    /// <summary>Returns whether the fresh signal-time native solver result reached the ordinary fallback target.</summary>
    public bool IsGateBFreshSignalNativeSolveSuccessful(string role, string foot) => GetRole(role).IsGateBFreshSignalNativeSolveSuccessful(ParseFoot(foot));

    /// <summary>Returns the test-only post-IK decision associated with the named Gate B case.</summary>
    public string GetGateBPostIKDecision(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).PostIKDecision.ToString();

    /// <summary>Returns the test-only blocked outcome associated with the named Gate B case.</summary>
    public string GetGateBBlockedOutcome(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).BlockedOutcome.ToString();

    /// <summary>Returns the distinct world-space marker shape selected from the injected evidence data.</summary>
    public string GetGateBVisibleMarkerShape(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).Evidence.MarkerShape.ToString();

    /// <summary>Returns the marker colour family selected from the injected evidence data.</summary>
    public string GetGateBVisibleMarkerColour(string role, string foot) => GetRole(role).GetGateBRecord(ParseFoot(foot)).Evidence.MarkerColourName;

    /// <summary>Returns the visible hollow aperture diameter for the named fixture-only refusal marker.</summary>
    public float GetGateBVisibleApertureDiameter(string role, string foot) => GetRole(role).VisibleApertureDiameter(ParseFoot(foot));

    /// <summary>Returns the minimum clearance from a refusal marker to the cyan target and magenta end markers.</summary>
    public float GetGateBMarkerTargetEndClearance(string role, string foot) => GetRole(role).MarkerTargetEndClearance(ParseFoot(foot));

    /// <summary>Returns the separation between the two visible contradictory-contact elements.</summary>
    public float GetGateBContradictoryPairSeparation(string role, string foot) => GetRole(role).ContradictoryPairSeparation(ParseFoot(foot));

    /// <summary>Returns the visible bridge length joining the contradictory-contact pair.</summary>
    public float GetGateBContradictoryBridgeLength(string role, string foot) => GetRole(role).ContradictoryBridgeLength(ParseFoot(foot));

    /// <summary>Returns the status-label anchor clearance from cyan-target and magenta-end marker geometry.</summary>
    public float GetGateBStatusLabelTargetEndClearance(string role, string foot) => GetRole(role).StatusLabelTargetEndClearance(ParseFoot(foot));

    /// <summary>Returns the live arrow direction that explains the rig +Z/world +Z pole-forward convention.</summary>
    public Vector3 GetVisibleDirectionalMarkerForward(string role, string foot) => GetRole(role).DirectionalMarkerForward(ParseFoot(foot));

    /// <summary>Shows only one role-foot evidence bundle for a non-overlapping focused capture.</summary>
    public void FocusRoleFootEvidence(string role, string foot)
    {
        EnsureConfigured();
        Foot focusFoot = ParseFoot(foot);
        foreach (RolePipeline pipeline in _roles.Values)
        {
            pipeline.SetEvidenceFocus(pipeline.Name == role ? focusFoot : null, pipeline.Name == role);
        }
    }

    /// <summary>Restores every role-foot evidence bundle after a focused capture.</summary>
    public void ShowAllEvidence()
    {
        EnsureConfigured();
        foreach (RolePipeline pipeline in _roles.Values)
        {
            pipeline.SetEvidenceFocus(null, true);
        }
    }

    /// <summary>Returns the role-foot centre used by the runner to frame focused captures.</summary>
    public Vector3 GetRoleFootEvidenceCentre(string role, string foot) => GetRole(role).EvidenceCentre(ParseFoot(foot));

    /// <summary>Returns the signed forward pole offset from the hip-foot midpoint.</summary>
    public float GetPoleForwardOffset(string role, string foot) => GetRole(role).PoleForwardOffset(ParseFoot(foot));

    /// <summary>Returns the knee's lateral distance from the hip-foot line.</summary>
    public float GetKneeLateralOffset(string role, string foot) => GetRole(role).KneeLateralOffset(ParseFoot(foot));

    /// <summary>
    /// Returns whether every asserted native TwoBoneIK3D modifier has processed since the current scenario was applied.
    /// </summary>
    public bool HasFreshNativeEvidence()
        => IsConfigured && _roles.Values.All(role => role.HasFreshNativeEvidence());

    /// <summary>Returns the fixture avatar-forward vector in world space for the requested role.</summary>
    public Vector3 GetAvatarForwardWorld(string role) => GetRole(role).AvatarForwardWorld;

    /// <summary>Returns a live configured knee-pole position for directional-marker verification.</summary>
    public Vector3 GetPoleWorldPosition(string role, string foot) => GetRole(role).PoleWorldPosition(ParseFoot(foot));

    /// <summary>
    /// Validates the exact photobooth scenarios without using screenshot output. The pole and knee checks are a
    /// deliberate guard against a visually plausible but anatomically impossible back-pole/straight-knee artefact.
    /// </summary>
    public void AssertGateAScenario()
    {
        EnsureConfigured();
        AssertRoleFoot("Player", Foot.Left, expectedPublished: true);
        AssertRoleFoot("Player", Foot.Right, expectedPublished: false);
        AssertRoleFoot("NPC", Foot.Left, expectedPublished: false);
        AssertRoleFoot("NPC", Foot.Right, expectedPublished: true);
    }

    /// <summary>Asserts the complete fixture-only refusal and native-fallback result for Gate B scenario A.</summary>
    public void AssertGateBScenarioA()
    {
        EnsureConfigured();
        AssertGateBScenario();
        AssertGateBCase("Player", Foot.Left, ContactLockGateBInjectedEvidence.FallbackMasking);
        AssertGateBCase("NPC", Foot.Right, ContactLockGateBInjectedEvidence.Missing);
    }

    /// <summary>Asserts the complete fixture-only refusal and native-fallback result for Gate B scenario B.</summary>
    public void AssertGateBScenarioB()
    {
        EnsureConfigured();
        AssertGateBScenario();
        AssertGateBCase("Player", Foot.Right, ContactLockGateBInjectedEvidence.Stale);
        AssertGateBCase("NPC", Foot.Left, ContactLockGateBInjectedEvidence.Contradictory);
    }

    public async void Configure()
    {
        var photobooth = GetParent<Node3D>();
        foreach (Node child in photobooth.GetChildren())
        {
            if (child != this && child.Name != "Cameras" && child.Name != "Markers" && child.Name != "Floor" && child.Name != "Environment")
            {
                child.QueueFree();
            }
        }

        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        CreateRoleRoots();
    }

    public async void CreateRoleRoots()
    {
        var photobooth = GetParent<Node3D>();
        PackedScene fixtureTopology = GD.Load<PackedScene>(FixtureScenePath);
        photobooth.AddChild(RolePipeline.InstantiateRoot("Player", fixtureTopology, new Vector3(-0.39f, 0.0f, -0.14f)));
        photobooth.AddChild(RolePipeline.InstantiateRoot("NPC", fixtureTopology, new Vector3(0.39f, 0.0f, 0.14f)));
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        ConfigurePipelines();
    }

    public async void ConfigurePipelines()
    {
        var photobooth = GetParent<Node3D>();
        RolePipeline player = RolePipeline.Create("Player", photobooth.GetNode<Node3D>("PlayerNeutralRole"));
        RolePipeline npc = RolePipeline.Create("NPC", photobooth.GetNode<Node3D>("NPCNeutralRole"));
        _roles.Add(player.Name, player);
        _roles.Add(npc.Name, npc);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        FinalisePipelines();
    }

    public async void FinalisePipelines()
    {
        var photobooth = GetParent<Node3D>();
        ConfigureCameraRig(photobooth);
        foreach (RolePipeline role in _roles.Values)
        {
            role.InitialisePipeline();
            role.ResetNativeEvidence();
        }

        await WaitForInitialNativeSettlementAsync();

        foreach (RolePipeline role in _roles.Values)
        {
            role.AssertReadyTopologyAndBoneAttachments(photobooth);
        }

        _scenarioLabel = CreateLabel("ScenarioLabel", new Color(1.0f, 1.0f, 1.0f), 22);
        photobooth.AddChild(_scenarioLabel);
        foreach (RolePipeline role in _roles.Values)
        {
            role.CreateVisibleEvidence(photobooth);
        }

        IsConfigured = true;
        ApplyGateA();
        UpdateScenarioLabelPosition();
    }

    private async Task WaitForInitialNativeSettlementAsync()
    {
        const int maximumPhysicsFrames = 8;
        for (int frame = 0; frame < maximumPhysicsFrames; frame++)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (_roles.Values.All(role => role.HasFreshNativeEvidence()))
            {
                HasCompletedInitialNativeSettlement = true;
                return;
            }
        }

        throw new InvalidOperationException(
            "Both minimal fixture roles must receive fresh native TwoBoneIK3D ModificationProcessed signals before configuration completes.");
    }

    private void AssertRoleFoot(string roleName, Foot foot, bool expectedPublished)
    {
        RolePipeline role = GetRole(roleName);
        if (role.IsAnchorPublished(foot) != expectedPublished)
        {
            throw new InvalidOperationException($"{roleName} {foot} published state does not match the Gate A scenario.");
        }

        float error = role.EndTargetError(foot);
        if (error > FootTargetToleranceMetres)
        {
            throw new InvalidOperationException($"{roleName} {foot} native TwoBoneIK3D end-bone error was {error:F4} m.");
        }

        float signalTargetError = role.SignalTargetError(foot);
        if (signalTargetError > SignalTargetToleranceMetres)
        {
            throw new InvalidOperationException(
                $"{roleName} {foot} native TwoBoneIK3D signal-time target error was {signalTargetError:F4} m.");
        }

        if (role.PoleForwardOffset(foot) <= 0.01f)
        {
            throw new InvalidOperationException($"{roleName} {foot} knee pole is not in front of the leg.");
        }

        if (role.KneeLateralOffset(foot) <= 0.015f)
        {
            throw new InvalidOperationException($"{roleName} {foot} knee has no visible flexion/lateral offset.");
        }
    }

    private void ApplyGateBCase(string roleName, Foot foot, ContactLockGateBInjectedEvidence injectedEvidence)
        => GetRole(roleName).ApplyGateB(foot, injectedEvidence);

    private void SetGateBScenarioLabel(string focus, string result)
    {
        _scenarioLabel!.Text = "IK-003 GATE B — FIXTURE/TEST-ONLY; NOT PRODUCTION POLICY\n"
            + focus + "\n"
            + result + "\n"
            + "LABELS: INJECTED • SELECTED PROVIDER • FRESH SIGNAL SOLVE • POST-IK • BLOCKED OUTCOME\n"
            + "ORANGE = ORDINARY FALLBACK • CYAN = SIGNAL TARGET • MAGENTA = SOLVED END • RED = REFUSED CONTACT";
    }

    private void AssertGateBScenario()
    {
        foreach (RolePipeline role in _roles.Values)
        {
            foreach (Foot foot in Enum.GetValues<Foot>())
            {
                GateBVisualRecord record = role.GetGateBRecord(foot);
                if (role.IsAnchorPublished(foot))
                {
                    throw new InvalidOperationException($"{role.Name} {foot} Gate B fixture case promoted refused contact evidence.");
                }

                if (record.SelectedProvider != ContactLockGateBSelectedProvider.OrdinaryFallback
                    || record.PostIKDecision != ContactLockGateBPostIKDecision.PreIKRejected
                    || record.BlockedOutcome != ContactLockGateBBlockedOutcome.ContactRefusedDespiteFallbackSolve)
                {
                    throw new InvalidOperationException($"{role.Name} {foot} Gate B fixture record drifted from the refusal contract.");
                }

                if (!role.IsGateBFreshSignalNativeSolveSuccessful(foot))
                {
                    throw new InvalidOperationException($"{role.Name} {foot} ordinary fallback did not reach its fresh signal-time native solve target.");
                }

                AssertRoleFoot(role.Name, foot, expectedPublished: false);
                role.MarkGateBVisualResultFresh(foot);
            }
        }
    }

    private void AssertGateBCase(string roleName, Foot foot, ContactLockGateBInjectedEvidence expectedEvidence)
    {
        GateBVisualRecord record = GetRole(roleName).GetGateBRecord(foot);
        if (record.InjectedEvidence != expectedEvidence)
        {
            throw new InvalidOperationException(
                $"{roleName} {foot} Gate B injected evidence was {record.InjectedEvidence}, expected {expectedEvidence}.");
        }
    }

    private RolePipeline GetRole(string role)
    {
        EnsureConfigured();
        return _roles.TryGetValue(role, out RolePipeline? pipeline)
            ? pipeline
            : throw new ArgumentOutOfRangeException(nameof(role), role, "Expected Player or NPC.");
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("The native pipeline photobooth has not finished configuring.");
        }
    }

    private static Foot ParseFoot(string foot) => foot switch
    {
        "Left" => Foot.Left,
        "Right" => Foot.Right,
        _ => throw new ArgumentOutOfRangeException(nameof(foot), foot, "Expected Left or Right."),
    };

    private void UpdateScenarioLabelPosition()
    {
        if (_scenarioLabel is null || _roles.Count == 0)
        {
            return;
        }

        Vector3 centre = Vector3.Zero;
        foreach (RolePipeline role in _roles.Values)
        {
            centre += role.VisibleEvidenceCentre;
        }

        // Keep the scenario legend above the signal-time hip/knee segments in every lower-body camera.
        _scenarioLabel.GlobalPosition = (centre / _roles.Count) + Vector3.Up * 1.18f;
    }

        private static Label3D CreateLabel(string name, Color colour, int fontSize)
        {
            return new Label3D
        {
            Name = name,
            FontSize = fontSize,
            PixelSize = 0.0015f,
            OutlineSize = 6,
            Modulate = colour,
                OutlineModulate = new Color(0.01f, 0.01f, 0.01f),
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                FixedSize = true,
                // Focused role-foot captures have exactly one evidence bundle. Keeping this billboarding panel in
                // front of its own markers makes every decision line readable from the front and right views.
                NoDepthTest = true,
        };
    }

    private static void ConfigureCameraRig(Node photobooth)
    {
        foreach (string cameraName in new[] { "FrontCamera", "RightCamera", "LeftCamera" })
        {
            photobooth.GetNodeOrNull($"Cameras/{cameraName}")?.Set("orthogonal_scale", 1.52f);
        }
    }

    private enum Foot
    {
        Left,
        Right,
    }

    private readonly record struct GateBVisualRecord(
        ContactLockGateBEvidence Evidence,
        ContactLockGateBSelectedProvider SelectedProvider,
        ContactLockGateBPostIKDecision PostIKDecision,
        ContactLockGateBBlockedOutcome BlockedOutcome)
    {
        public ContactLockGateBInjectedEvidence InjectedEvidence => Evidence.InjectedEvidence;
    }

    private sealed partial class FixtureIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent Intent { get; set; }

        public override IKTargetIntent GetTargetIntent() => Intent;
    }

    private sealed partial class FixtureContactProvider : IKTargetIntentProvider
    {
        public bool PublishAnchor { get; set; }
        public Transform3D Anchor { get; set; } = Transform3D.Identity;

        public override IKTargetIntent GetTargetIntent() => new(Transform3D.Identity, 0.0f);

        public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
        {
            if (PublishAnchor)
            {
                resolution = new IKTargetIntentResolution(new IKTargetIntent(Anchor, 1.0f), true);
                return true;
            }

            resolution = default;
            return false;
        }
    }

    private sealed class RolePipeline
    {
        private readonly Skeleton3D _skeleton;
        private readonly Node3D _leftFootTarget;
        private readonly Node3D _rightFootTarget;
        private readonly Node3D _leftPoleTarget;
        private readonly Node3D _rightPoleTarget;
        private readonly FootTargetSyncController _sync;
        private readonly LegIKController _leftPole;
        private readonly LegIKController _rightPole;
        private readonly TwoBoneIK3D _leftTwoBone;
        private readonly TwoBoneIK3D _rightTwoBone;
        private readonly FixtureIntentProvider _leftProvider;
        private readonly FixtureIntentProvider _rightProvider;
        private readonly FixtureContactProvider _leftContact;
        private readonly FixtureContactProvider _rightContact;
        private readonly CharacterIK _characterIK;
        private readonly Dictionary<Foot, EvidenceVisual> _evidence = [];
        private readonly Dictionary<Foot, NativeTwoBonePipelineEvidence> _nativeEvidence = [];
        private readonly Dictionary<Foot, GateBVisualRecord> _gateBRecords = [];
        private long _gateBGeneration;

        private RolePipeline(
            string name,
            Node3D root,
            Skeleton3D skeleton,
            Node3D leftFootTarget,
            Node3D rightFootTarget,
            Node3D leftPoleTarget,
            Node3D rightPoleTarget,
            FootTargetSyncController sync,
            LegIKController leftPole,
            LegIKController rightPole,
            TwoBoneIK3D leftTwoBone,
            TwoBoneIK3D rightTwoBone,
            FixtureIntentProvider leftProvider,
            FixtureIntentProvider rightProvider,
            FixtureContactProvider leftContact,
            FixtureContactProvider rightContact,
            CharacterIK characterIK)
        {
            Name = name;
            Root = root;
            _skeleton = skeleton;
            _leftFootTarget = leftFootTarget;
            _rightFootTarget = rightFootTarget;
            _leftPoleTarget = leftPoleTarget;
            _rightPoleTarget = rightPoleTarget;
            _sync = sync;
            _leftPole = leftPole;
            _rightPole = rightPole;
            _leftTwoBone = leftTwoBone;
            _rightTwoBone = rightTwoBone;
            _leftProvider = leftProvider;
            _rightProvider = rightProvider;
            _leftContact = leftContact;
            _rightContact = rightContact;
            _characterIK = characterIK;
        }

        public string Name { get; }
        public Node3D Root { get; }
        public Vector3 AvatarForwardWorld => (_skeleton.GlobalTransform.Basis.Orthonormalized() * Vector3.Back).Normalized();

        public static Node3D InstantiateRoot(string name, PackedScene topology, Vector3 position)
        {
            Node3D root = topology.Instantiate<Node3D>();
            root.Name = $"{name}NeutralRole";
            root.Position = position;
            return root;
        }

        public static RolePipeline Create(string name, Node3D root)
        {
            Skeleton3D skeleton = RequireDescendant<Skeleton3D>(root, "GeneralSkeleton");
            skeleton.Set("modifier_callback_mode_process", 0);
            skeleton.ResetBonePoses();

            Node3D leftFootTarget = RequireNode3D(root, "Markers/LeftFootTarget");
            Node3D rightFootTarget = RequireNode3D(root, "Markers/RightFootTarget");
            Node3D leftPoleTarget = RequireNode3D(root, "Markers/LeftKneePoleTarget");
            Node3D rightPoleTarget = RequireNode3D(root, "Markers/RightKneePoleTarget");
            LegIKController leftPole = RequireChild<LegIKController>(skeleton, "LeftLegIKController");
            LegIKController rightPole = RequireChild<LegIKController>(skeleton, "RightLegIKController");
            TwoBoneIK3D leftTwoBone = RequireChild<TwoBoneIK3D>(skeleton, "LeftLegTwoBoneIKController");
            TwoBoneIK3D rightTwoBone = RequireChild<TwoBoneIK3D>(skeleton, "RightLegTwoBoneIKController");
            leftTwoBone.Active = true;
            rightTwoBone.Active = true;
            leftTwoBone.Influence = 1.0f;
            rightTwoBone.Influence = 1.0f;

            FootTargetSyncController sync = RequireChild<FootTargetSyncController>(skeleton, "FootTargetSyncController");
            sync.LeftFootTarget = leftFootTarget;
            sync.RightFootTarget = rightFootTarget;
            sync.Active = true;
            sync.Influence = 1.0f;

            Node3D targetRoot = new() { Name = "ContactLockPhotoboothTargets" };
            root.AddChild(targetRoot);
            Marker3D viewpoint = new() { Name = "Viewpoint" };
            CharacterBody3D head = new() { Name = "Head" };
            Node3D headSolve = new() { Name = "HeadSolve" };
            AnimatableBody3D rightHand = new() { Name = "RightHand" };
            AnimatableBody3D leftHand = new() { Name = "LeftHand" };
            targetRoot.AddChild(viewpoint);
            targetRoot.AddChild(head);
            targetRoot.AddChild(headSolve);
            targetRoot.AddChild(rightHand);
            targetRoot.AddChild(leftHand);

            FixtureIntentProvider leftProvider = new() { Name = "LeftOrdinaryProvider" };
            FixtureIntentProvider rightProvider = new() { Name = "RightOrdinaryProvider" };
            FixtureContactProvider leftContact = new() { Name = "LeftContactProvider" };
            FixtureContactProvider rightContact = new() { Name = "RightContactProvider" };
            PriorityIKTargetIntentProvider leftChain = new()
            {
                Name = "LeftProviderChain",
                Providers = [leftContact, leftProvider],
            };
            PriorityIKTargetIntentProvider rightChain = new()
            {
                Name = "RightProviderChain",
                Providers = [rightContact, rightProvider],
            };
            AnimationSynchronizedFootTargetProvider leftFallback = new() { Name = "LeftFallback", FootTarget = leftFootTarget };
            AnimationSynchronizedFootTargetProvider rightFallback = new() { Name = "RightFallback", FootTarget = rightFootTarget };
            targetRoot.AddChild(leftProvider);
            targetRoot.AddChild(rightProvider);
            targetRoot.AddChild(leftContact);
            targetRoot.AddChild(rightContact);
            targetRoot.AddChild(leftChain);
            targetRoot.AddChild(rightChain);
            targetRoot.AddChild(leftFallback);
            targetRoot.AddChild(rightFallback);

            DynamicPhysicalRig physicalRig = new() { Name = "PhotoboothPhysicalRig", Enabled = false, TargetSkeleton = skeleton };
            skeleton.AddChild(physicalRig);
            CharacterIK characterIK = new()
            {
                Name = "CharacterIK",
                Active = true,
                Viewpoint = viewpoint,
                HeadIKTarget = head,
                HeadIKSolveTarget = headSolve,
                RightHandIKTarget = rightHand,
                LeftHandIKTarget = leftHand,
                LeftFootIKTarget = leftFootTarget,
                RightFootIKTarget = rightFootTarget,
                LeftFootTargetIntentProvider = leftChain,
                RightFootTargetIntentProvider = rightChain,
                LeftFootFallbackIntentProvider = leftFallback,
                RightFootFallbackIntentProvider = rightFallback,
                LeftFootModifierGroup = [leftPole, leftTwoBone],
                RightFootModifierGroup = [rightPole, rightTwoBone],
                PhysicalRig = physicalRig,
            };
            root.AddChild(characterIK);

            RolePipeline pipeline = new(
                name,
                root,
                skeleton,
                leftFootTarget,
                rightFootTarget,
                leftPoleTarget,
                rightPoleTarget,
                sync,
                leftPole,
                rightPole,
                leftTwoBone,
                rightTwoBone,
                leftProvider,
                rightProvider,
                leftContact,
                rightContact,
                characterIK);
            leftTwoBone.ModificationProcessed += () => pipeline.RecordNativeTwoBoneProcessing(Foot.Left);
            rightTwoBone.ModificationProcessed += () => pipeline.RecordNativeTwoBoneProcessing(Foot.Right);
            pipeline.SetOrdinaryTargets();
            return pipeline;
        }

        public void Apply(Foot foot, bool anchored)
        {
            Transform3D current = FootWorldPose(foot);
            Transform3D anchor = CreateAnchorInsideReach(foot);
            Transform3D ordinary = current;
            Provider(foot).Intent = new IKTargetIntent(ordinary, 1.0f);
            Contact(foot).Anchor = anchor;
            Contact(foot).PublishAnchor = anchored;
            _nativeEvidence.Remove(foot);
            _gateBRecords.Remove(foot);
            _evidence[foot].SetStandardScenario(anchored);
        }

        public void ApplyGateB(Foot foot, ContactLockGateBInjectedEvidence injectedEvidence)
        {
            Transform3D anchor = CreateAnchorInsideReach(foot);
            // Retain the current ordinary-provider pose. The cyan signal target, magenta native end, and white
            // solved-leg segments separately expose the real fallback/native relationship without perturbing pole IK.
            Transform3D ordinaryFallback = FootWorldPose(foot);
            ContactLockGateBEvidence sourceEvidence = ContactLockGateBEvidence.Create(
                Name,
                foot.ToString(),
                ++_gateBGeneration,
                injectedEvidence,
                anchor,
                ordinaryFallback);
            GateBVisualRecord record = new(
                sourceEvidence,
                sourceEvidence.SelectedProvider,
                sourceEvidence.PostIKDecision,
                sourceEvidence.BlockedOutcome);

            if (sourceEvidence.PreIKDecision != ContactLockGateBPreIKDecision.Rejected)
            {
                throw new InvalidOperationException($"{Name} {foot} Gate B fixture source evidence unexpectedly published a contact anchor.");
            }

            Provider(foot).Intent = new IKTargetIntent(ordinaryFallback, 1.0f);
            Contact(foot).Anchor = anchor;
            Contact(foot).PublishAnchor = false;
            _nativeEvidence.Remove(foot);
            _gateBRecords[foot] = record;
            _evidence[foot].SetGateBScenario(record);
        }

        public void InitialisePipeline() => _characterIK._PhysicsProcess(1.0d / 60.0d);

        public void ResetNativeEvidence() => _nativeEvidence.Clear();

        public bool HasFreshNativeEvidence()
            => _nativeEvidence.Count == Enum.GetValues<Foot>().Length
                && _nativeEvidence.Values.All(evidence => evidence.ProcessCount > 0);

        public GateBVisualRecord GetGateBRecord(Foot foot)
            => _gateBRecords.TryGetValue(foot, out GateBVisualRecord record)
                ? record
                : throw new InvalidOperationException($"{Name} {foot} does not have a Gate B visual record.");

        public bool IsGateBFreshSignalNativeSolveSuccessful(Foot foot)
        {
            NativeTwoBonePipelineEvidence evidence = RequireNativeEvidence(foot);
            return evidence.ProcessCount > 0
                && evidence.EndBoneAtProcessing.Origin.DistanceTo(evidence.TargetAtProcessing.Origin) <= FootTargetToleranceMetres;
        }

        public void MarkGateBVisualResultFresh(Foot foot) => _evidence[foot].MarkGateBFreshNativeSolveSuccess();

        public void SetEvidenceFocus(Foot? foot, bool roleVisible)
        {
            foreach ((Foot candidate, EvidenceVisual visual) in _evidence)
            {
                visual.SetVisible(roleVisible && (foot is null || candidate == foot));
            }
        }

        public Vector3 EvidenceCentre(Foot foot) => _evidence[foot].Centre;

        public float VisibleApertureDiameter(Foot foot) => _evidence[foot].ApertureDiameter;

        public float MarkerTargetEndClearance(Foot foot) => _evidence[foot].TargetEndClearance;

        public float ContradictoryPairSeparation(Foot foot) => _evidence[foot].ContradictoryPairSeparation;

        public float ContradictoryBridgeLength(Foot foot) => _evidence[foot].ContradictoryBridgeLength;

        public float StatusLabelTargetEndClearance(Foot foot) => _evidence[foot].StatusLabelTargetEndClearance;

        public Vector3 DirectionalMarkerForward(Foot foot) => _evidence[foot].DirectionalMarkerForward;

        public void AssertReadyTopologyAndBoneAttachments(Node visualHost)
        {
            Node? cameraHarness = visualHost.GetNodeOrNull("Cameras");
            if (cameraHarness?.GetParent() != visualHost || Root.GetParent() != visualHost)
            {
                throw new InvalidOperationException(
                    $"{Name} camera harness and role root must be sibling children of the visual host without reparenting role topology.");
            }

            if (!Root.IsAncestorOf(_skeleton))
            {
                throw new InvalidOperationException($"{Name} skeleton was reparented outside its authored role topology.");
            }

            AssertMinimalFixtureModifierPipeline();

            List<string> invalidAttachments = [];
            foreach (Node node in _skeleton.FindChildren("*", "BoneAttachment3D", recursive: true, owned: false))
            {
                if (node is BoneAttachment3D attachment && attachment.BoneIdx < 0)
                {
                    invalidAttachments.Add($"{attachment.GetPath()} ({attachment.BoneName})");
                }
            }

            if (invalidAttachments.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{Name} authored BoneAttachment3D mappings were not initialised after tree readiness: {string.Join(", ", invalidAttachments)}.");
            }
        }

        private void AssertMinimalFixtureModifierPipeline()
        {
            if (Root.FindChildren("*", "AnimationTree", recursive: true, owned: false).Count > 0)
            {
                throw new InvalidOperationException($"{Name} minimal role must not require an AnimationTree.");
            }

            SkeletonModifier3D[] expectedPipeline =
            [
                _sync,
                RequireChild<SkeletonModifier3D>(_skeleton, "CharacterIKBeginStage"),
                RequireChild<SkeletonModifier3D>(_skeleton, "CharacterIKFootProviderStage"),
                _leftPole,
                _leftTwoBone,
                _rightPole,
                _rightTwoBone,
                RequireChild<SkeletonModifier3D>(_skeleton, "CharacterIKEndStage"),
            ];
            SkeletonModifier3D[] actualPipeline = [.. _skeleton.GetChildren().OfType<SkeletonModifier3D>()];

            if (actualPipeline.Length != expectedPipeline.Length
                || actualPipeline.Where((modifier, index) => !ReferenceEquals(modifier, expectedPipeline[index])).Any())
            {
                throw new InvalidOperationException(
                    $"{Name} minimal fixture must use only FootTargetSyncController, CharacterIK stages, and pole/native TwoBoneIK3D lower-leg modifiers in generic pipeline order.");
            }
        }

        public void CreateVisibleEvidence(Node3D parent)
        {
            foreach (Foot foot in Enum.GetValues<Foot>())
            {
                EvidenceVisual visual = new(
                    Name,
                    foot,
                    () => Contact(foot).Anchor,
                    () => SignalTargetPose(foot),
                    () => SignalEndBonePose(foot),
                    () => SignalHipPose(foot),
                    () => SignalKneePose(foot),
                    () => PoleTarget(foot).GlobalTransform,
                    () => AvatarForwardWorld);
                visual.AddTo(parent);
                _evidence.Add(foot, visual);
            }

        }

        public void UpdateVisibleEvidence()
        {
            foreach (EvidenceVisual visual in _evidence.Values)
            {
                visual.Update();
            }
        }

        public float EndTargetError(Foot foot)
        {
            NativeTwoBonePipelineEvidence evidence = RequireNativeEvidence(foot);
            return evidence.EndBoneAtProcessing.Origin.DistanceTo(evidence.TargetAtProcessing.Origin);
        }

        public float SignalTargetError(Foot foot)
        {
            NativeTwoBonePipelineEvidence evidence = RequireNativeEvidence(foot);
            return evidence.TargetAtProcessing.Origin.DistanceTo(EffectiveTarget(foot).Origin);
        }
        public bool IsAnchorPublished(Foot foot) => Contact(foot).PublishAnchor;
        public Vector3 PoleWorldPosition(Foot foot) => PoleTarget(foot).GlobalPosition;
        public Vector3 VisibleEvidenceCentre => (SignalTargetPose(Foot.Left).Origin + SignalTargetPose(Foot.Right).Origin) * 0.5f;

        public float PoleForwardOffset(Foot foot)
        {
            Vector3 hip = BoneWorldPose(foot == Foot.Left ? "LeftUpperLeg" : "RightUpperLeg").Origin;
            Vector3 footPosition = FootWorldPose(foot).Origin;
            return (PoleTarget(foot).GlobalPosition - ((hip + footPosition) * 0.5f)).Dot(AvatarForwardWorld);
        }

        public float KneeLateralOffset(Foot foot)
        {
            Vector3 hip = BoneWorldPose(foot == Foot.Left ? "LeftUpperLeg" : "RightUpperLeg").Origin;
            Vector3 knee = BoneWorldPose(foot == Foot.Left ? "LeftLowerLeg" : "RightLowerLeg").Origin;
            Vector3 footPosition = FootWorldPose(foot).Origin;
            Vector3 axis = footPosition - hip;
            return axis.IsZeroApprox() ? 0.0f : (knee - hip).Cross(axis).Length() / axis.Length();
        }

        private void SetOrdinaryTargets()
        {
            foreach (Foot foot in Enum.GetValues<Foot>())
            {
                Provider(foot).Intent = new IKTargetIntent(FootWorldPose(foot), 1.0f);
                Contact(foot).PublishAnchor = false;
            }
        }

        private Transform3D CreateAnchorInsideReach(Foot foot)
        {
            Transform3D upper = BoneWorldPose(foot == Foot.Left ? "LeftUpperLeg" : "RightUpperLeg");
            Transform3D current = FootWorldPose(foot);
            // This test-only offset keeps the target comfortably inside reach while making the native solved state
            // visibly distinct from the ordinary target in the Gate A capture.
            return new Transform3D(current.Basis, current.Origin + ((upper.Origin - current.Origin).Normalized() * 0.09f));
        }

        private Transform3D FootWorldPose(Foot foot) => BoneWorldPose(foot == Foot.Left ? "LeftFoot" : "RightFoot");
        private Transform3D SignalTargetPose(Foot foot) => _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
            ? evidence.TargetAtProcessing
            : FootTarget(foot).GlobalTransform;
        private Transform3D SignalEndBonePose(Foot foot) => _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
            ? evidence.EndBoneAtProcessing
            : FootWorldPose(foot);
        private Transform3D SignalHipPose(Foot foot) => _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
            ? evidence.HipAtProcessing
            : BoneWorldPose(foot == Foot.Left ? "LeftUpperLeg" : "RightUpperLeg");
        private Transform3D SignalKneePose(Foot foot) => _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
            ? evidence.KneeAtProcessing
            : BoneWorldPose(foot == Foot.Left ? "LeftLowerLeg" : "RightLowerLeg");
        private Transform3D EffectiveTarget(Foot foot) => Contact(foot).PublishAnchor ? Contact(foot).Anchor : Provider(foot).Intent.WorldTransform;
        private Transform3D BoneWorldPose(string name) => _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(RequireBone(name));
        private Node3D FootTarget(Foot foot) => foot == Foot.Left ? _leftFootTarget : _rightFootTarget;
        private Node3D PoleTarget(Foot foot) => foot == Foot.Left ? _leftPoleTarget : _rightPoleTarget;
        private FixtureIntentProvider Provider(Foot foot) => foot == Foot.Left ? _leftProvider : _rightProvider;
        private FixtureContactProvider Contact(Foot foot) => foot == Foot.Left ? _leftContact : _rightContact;
        private int RequireBone(string name) => _skeleton.FindBone(name) is int index and not -1 ? index : throw new InvalidOperationException($"Missing fixture bone '{name}'.");

        private NativeTwoBonePipelineEvidence RequireNativeEvidence(Foot foot)
            => _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
                ? evidence
                : throw new InvalidOperationException($"{Name} {foot} native TwoBoneIK3D has not emitted ModificationProcessed.");

        private void RecordNativeTwoBoneProcessing(Foot foot)
        {
            NativeTwoBonePipelineEvidence previous = _nativeEvidence.TryGetValue(foot, out NativeTwoBonePipelineEvidence evidence)
                ? evidence
                : default;
            _nativeEvidence[foot] = new NativeTwoBonePipelineEvidence(
                previous.ProcessCount + 1,
                FootTarget(foot).GlobalTransform,
                FootWorldPose(foot),
                BoneWorldPose(foot == Foot.Left ? "LeftUpperLeg" : "RightUpperLeg"),
                BoneWorldPose(foot == Foot.Left ? "LeftLowerLeg" : "RightLowerLeg"));
        }

        private static T RequireChild<T>(Node parent, string name)
            where T : Node
            => parent.GetNodeOrNull<T>(name) ?? throw new InvalidOperationException($"Missing fixture node '{name}'.");

        private static T RequireDescendant<T>(Node parent, string name)
            where T : Node
            => parent.FindChild(name, owned: true, recursive: true) as T ?? throw new InvalidOperationException($"Missing fixture descendant '{name}'.");

        private static Node3D RequireNode3D(Node parent, string path)
            => parent.GetNodeOrNull<Node3D>(path) ?? throw new InvalidOperationException($"Missing fixture node '{path}'.");
    }

    private readonly record struct NativeTwoBonePipelineEvidence(
        int ProcessCount,
        Transform3D TargetAtProcessing,
        Transform3D EndBoneAtProcessing,
        Transform3D HipAtProcessing,
        Transform3D KneeAtProcessing);

    private sealed class EvidenceVisual
    {
        private readonly string _role;
        private readonly Func<Transform3D> _anchorPose;
        private readonly Func<Transform3D> _signalTargetPose;
        private readonly Func<Transform3D> _signalEndBonePose;
        private readonly Func<Transform3D> _signalHipPose;
        private readonly Func<Transform3D> _signalKneePose;
        private readonly Func<Transform3D> _polePose;
        private readonly Func<Vector3> _avatarForward;
        private readonly Foot _foot;
        private readonly MeshInstance3D _upperLegLine = MakeSolvedLegLine();
        private readonly MeshInstance3D _lowerLegLine = MakeSolvedLegLine();
        private readonly MeshInstance3D _kneeMarker = MakeMarker(new SphereMesh(), new Color(0.20f, 0.50f, 1.0f));
        private readonly MeshInstance3D _anchorMarker = MakeMarker(new CylinderMesh(), new Color(0.15f, 1.0f, 0.25f));
        private readonly MeshInstance3D _ordinaryMarker = MakeMarker(new BoxMesh(), new Color(1.0f, 0.55f, 0.08f));
        private readonly MeshInstance3D _targetMarker = MakeMarker(new BoxMesh(), new Color(0.08f, 0.95f, 1.0f));
        private readonly MeshInstance3D _endMarker = MakeMarker(new SphereMesh(), new Color(1.0f, 0.2f, 0.78f));
        private readonly MeshInstance3D _poleMarker = MakeMarker(new SphereMesh(), new Color(1.0f, 0.9f, 0.05f));
        private readonly MeshInstance3D _missingRing = MakeMarker(
            new TorusMesh { InnerRadius = 0.38f, OuterRadius = 0.70f },
            new Color(1.0f, 0.08f, 0.08f),
            uniformScale: 0.15f);
        private readonly MeshInstance3D _stalePrism = MakeMarker(new PrismMesh(), new Color(0.60f, 0.18f, 1.0f));
        private readonly MeshInstance3D _invalidCone = MakeMarker(new CylinderMesh { TopRadius = 0.0f, BottomRadius = 0.5f, Height = 1.25f }, new Color(1.0f, 0.70f, 0.05f));
        private readonly MeshInstance3D _contradictoryFirst = MakeMarker(new SphereMesh(), new Color(1.0f, 0.05f, 0.80f), uniformScale: 0.075f);
        private readonly MeshInstance3D _contradictorySecond = MakeMarker(new SphereMesh(), new Color(0.86f, 0.08f, 1.0f), uniformScale: 0.075f);
        private readonly MeshInstance3D _contradictoryBridge = MakeMarker(
            new CylinderMesh { Height = 1.0f, TopRadius = 0.22f, BottomRadius = 0.22f },
            new Color(1.0f, 0.28f, 0.62f));
        private readonly MeshInstance3D _maskingStack = MakeMarker(new BoxMesh(), new Color(1.0f, 0.50f, 0.02f));
        private readonly MeshInstance3D _maskingBlocker = MakeMarker(new BoxMesh(), new Color(0.05f, 0.05f, 0.05f));
        private readonly MeshInstance3D _forwardArrowShaft = MakeMarker(new CylinderMesh { Height = 1.0f, TopRadius = 0.012f, BottomRadius = 0.012f }, new Color(0.05f, 0.90f, 1.0f));
        private readonly MeshInstance3D _forwardArrowHead = MakeMarker(new CylinderMesh { Height = 0.08f, TopRadius = 0.0f, BottomRadius = 0.045f }, new Color(0.05f, 0.90f, 1.0f));
        private readonly Label3D _stateLabel;
        private bool _anchored;
        private GateBVisualRecord? _gateBRecord;
        private bool _gateBFreshNativeSolveSuccessful;
        private bool _isVisible = true;

        public EvidenceVisual(
            string role,
            Foot foot,
            Func<Transform3D> anchorPose,
            Func<Transform3D> signalTargetPose,
            Func<Transform3D> signalEndBonePose,
            Func<Transform3D> signalHipPose,
            Func<Transform3D> signalKneePose,
            Func<Transform3D> polePose,
            Func<Vector3> avatarForward)
        {
            _role = role;
            _anchorPose = anchorPose;
            _foot = foot;
            _signalTargetPose = signalTargetPose;
            _signalEndBonePose = signalEndBonePose;
            _signalHipPose = signalHipPose;
            _signalKneePose = signalKneePose;
            _polePose = polePose;
            _avatarForward = avatarForward;
            _stateLabel = CreateLabel($"{role}{foot}Evidence", new Color(1.0f, 1.0f, 1.0f), 20);
        }

        public Vector3 Centre => _signalTargetPose().Origin;
        public Vector3 DirectionalMarkerForward
            => (_forwardArrowHead.GlobalPosition - _forwardArrowShaft.GlobalPosition).Normalized();

        public float ApertureDiameter => 2.0f * 0.38f * _missingRing.Scale.X;

        public float TargetEndClearance
        {
            get
            {
                Vector3 target = _signalTargetPose().Origin;
                Vector3 end = _signalEndBonePose().Origin;
                ContactLockGateBMarkerShape shape = _gateBRecord.GetValueOrDefault().Evidence.MarkerShape;
                return shape == ContactLockGateBMarkerShape.SplitPair
                    ? MathF.Min(
                        MathF.Min(
                            MarkerClearance(_contradictoryFirst.GlobalPosition, target, 0.075f),
                            MarkerClearance(_contradictoryFirst.GlobalPosition, end, 0.075f)),
                        MathF.Min(
                            MarkerClearance(_contradictorySecond.GlobalPosition, target, 0.075f),
                            MarkerClearance(_contradictorySecond.GlobalPosition, end, 0.075f)))
                    : MathF.Min(
                        MarkerClearance(_missingRing.GlobalPosition, target, 0.105f),
                        MarkerClearance(_missingRing.GlobalPosition, end, 0.105f));
            }
        }

        public float ContradictoryPairSeparation => _contradictoryFirst.GlobalPosition.DistanceTo(_contradictorySecond.GlobalPosition);

        public float ContradictoryBridgeLength => _contradictoryBridge.Scale.Y;

        public float StatusLabelTargetEndClearance
        {
            get
            {
                Vector3 target = _signalTargetPose().Origin;
                Vector3 end = _signalEndBonePose().Origin;
                return MathF.Min(
                    _stateLabel.GlobalPosition.DistanceTo(target),
                    _stateLabel.GlobalPosition.DistanceTo(end));
            }
        }

        public void AddTo(Node3D parent)
        {
            parent.AddChild(_upperLegLine);
            parent.AddChild(_lowerLegLine);
            parent.AddChild(_kneeMarker);
            parent.AddChild(_anchorMarker);
            parent.AddChild(_ordinaryMarker);
            parent.AddChild(_targetMarker);
            parent.AddChild(_endMarker);
            parent.AddChild(_poleMarker);
            parent.AddChild(_missingRing);
            parent.AddChild(_stalePrism);
            parent.AddChild(_invalidCone);
            parent.AddChild(_contradictoryFirst);
            parent.AddChild(_contradictorySecond);
            parent.AddChild(_contradictoryBridge);
            parent.AddChild(_maskingStack);
            parent.AddChild(_maskingBlocker);
            parent.AddChild(_forwardArrowShaft);
            parent.AddChild(_forwardArrowHead);
            parent.AddChild(_stateLabel);
            Update();
        }

        public void SetStandardScenario(bool anchored)
        {
            _anchored = anchored;
            _gateBRecord = null;
            _gateBFreshNativeSolveSuccessful = false;
            _anchorMarker.Visible = anchored;
            _ordinaryMarker.Visible = !anchored;
            _stateLabel.Text = $"{_role.ToUpperInvariant()} {_foot.ToString().ToUpperInvariant()}\n"
                + (_anchored ? "GREEN ANCHOR" : "ORANGE FALLBACK");
            Update();
        }

        public void SetGateBScenario(GateBVisualRecord record)
        {
            _anchored = false;
            _gateBRecord = record;
            _gateBFreshNativeSolveSuccessful = false;
            _anchorMarker.Visible = false;
            _ordinaryMarker.Visible = true;
            UpdateGateBLabel();
            Update();
        }

        public void SetVisible(bool visible)
        {
            _isVisible = visible;
            Update();
        }

        public void MarkGateBFreshNativeSolveSuccess()
        {
            _gateBFreshNativeSolveSuccessful = true;
            UpdateGateBLabel();
        }

        public void Update()
        {
            Transform3D anchor = _anchorPose();
            Transform3D target = _signalTargetPose();
            Transform3D endBone = _signalEndBonePose();
            Transform3D hip = _signalHipPose();
            Transform3D knee = _signalKneePose();
            Transform3D pole = _polePose();
            UpdateSolvedLegLine(_upperLegLine, hip.Origin, knee.Origin);
            UpdateSolvedLegLine(_lowerLegLine, knee.Origin, endBone.Origin);
            _kneeMarker.GlobalPosition = knee.Origin;
            _anchorMarker.GlobalPosition = anchor.Origin + Vector3.Up * 0.012f;
            _ordinaryMarker.GlobalPosition = target.Origin + Vector3.Up * 0.012f;
            _targetMarker.GlobalPosition = target.Origin + Vector3.Up * 0.030f;
            _endMarker.GlobalPosition = endBone.Origin + Vector3.Up * 0.066f;
            _poleMarker.GlobalPosition = pole.Origin;
            _stateLabel.GlobalPosition = (_anchored ? anchor.Origin : target.Origin) + LabelOffset();
            UpdateRefusedEvidenceGeometry(anchor, pole.Origin);
            // Keep the semantic pole-forward arrow beside the target/end markers rather than behind the leg, so the
            // front and lateral focused cameras show its geometric +Z direction without relying on its label.
            UpdateDirectionalArrow(target.Origin + Vector3.Up * 0.17f, _avatarForward());
            ApplyVisibility();
        }

        private void UpdateRefusedEvidenceGeometry(Transform3D anchor, Vector3 pole)
        {
            // The torus's local Y normal is turned diagonally between the focused front and right views. Its open
            // centre is therefore visible in both captures, while the elevated Z-side lane keeps it clear of the
            // cyan signal target and magenta native end marker.
            _missingRing.GlobalPosition = anchor.Origin + new Vector3(0.0f, 0.21f, 0.18f);
            _missingRing.GlobalRotation = new Vector3(0.0f, Mathf.Pi * 0.25f, Mathf.Pi * 0.5f);
            _missingRing.Scale = Vector3.One * 0.15f;
            _stalePrism.GlobalPosition = anchor.Origin + Vector3.Up * 0.045f;
            _invalidCone.GlobalPosition = anchor.Origin + Vector3.Up * 0.040f;
            Vector3 contradictoryCentre = anchor.Origin + new Vector3(0.0f, 0.22f, -0.17f);
            Vector3 contradictorySeparation = new(0.20f, 0.0f, 0.22f);
            _contradictoryFirst.GlobalPosition = contradictoryCentre - (contradictorySeparation * 0.5f);
            _contradictorySecond.GlobalPosition = contradictoryCentre + (contradictorySeparation * 0.5f);
            UpdateEvidenceBridge(_contradictoryBridge, _contradictoryFirst.GlobalPosition, _contradictorySecond.GlobalPosition);
            _maskingStack.GlobalPosition = anchor.Origin + new Vector3(0.0f, 0.030f, 0.0f);
            _maskingBlocker.GlobalPosition = anchor.Origin + new Vector3(0.0f, 0.072f, 0.0f);
            _ = pole;
        }

        private void UpdateDirectionalArrow(Vector3 pole, Vector3 avatarForward)
        {
            Vector3 end = pole + (avatarForward.Normalized() * 0.18f);
            UpdateSolvedLegLine(_forwardArrowShaft, pole, end);
            _forwardArrowHead.GlobalPosition = end;
            _forwardArrowHead.LookAt(pole, Vector3.Up);
            _forwardArrowHead.RotateObjectLocal(Vector3.Right, -Mathf.Pi * 0.5f);
        }

        private void ApplyVisibility()
        {
            ContactLockGateBMarkerShape shape = _gateBRecord.GetValueOrDefault().Evidence.MarkerShape;
            _upperLegLine.Visible = _isVisible;
            _lowerLegLine.Visible = _isVisible;
            _kneeMarker.Visible = _isVisible;
            _anchorMarker.Visible = _isVisible && _anchored;
            _ordinaryMarker.Visible = _isVisible && !_anchored;
            _targetMarker.Visible = _isVisible;
            _endMarker.Visible = _isVisible;
            _poleMarker.Visible = _isVisible;
            _forwardArrowShaft.Visible = _isVisible;
            _forwardArrowHead.Visible = _isVisible;
            _stateLabel.Visible = _isVisible;
            _missingRing.Visible = _isVisible && shape == ContactLockGateBMarkerShape.HollowRing;
            _stalePrism.Visible = _isVisible && shape == ContactLockGateBMarkerShape.TallPrism;
            _invalidCone.Visible = _isVisible && shape == ContactLockGateBMarkerShape.WarningCone;
            bool contradictory = _isVisible && shape == ContactLockGateBMarkerShape.SplitPair;
            _contradictoryFirst.Visible = contradictory;
            _contradictorySecond.Visible = contradictory;
            _contradictoryBridge.Visible = contradictory;
            bool masking = _isVisible && shape == ContactLockGateBMarkerShape.MaskingStack;
            _maskingStack.Visible = masking;
            _maskingBlocker.Visible = masking;
        }

        private void UpdateGateBLabel()
        {
            if (_gateBRecord is not GateBVisualRecord record)
            {
                return;
            }

            string freshNativeResult = _gateBFreshNativeSolveSuccessful ? "SUCCESS" : "AWAITING";
            _stateLabel.Text = $"{_role.ToUpperInvariant()} {_foot.ToString().ToUpperInvariant()} — FIXTURE ONLY\n"
                + $"REFUSED EVIDENCE: {ToVisibleText(record.InjectedEvidence)} ({record.Evidence.MarkerShape})\n"
                + $"MARKER: {record.Evidence.MarkerColourName} • CONTACT REFUSED/BLOCKED\n"
                + "SELECTED: ORDINARY FALLBACK → CYAN SIGNAL TARGET\n"
                + $"NATIVE TWO-BONE SOLVE: {freshNativeResult} → MAGENTA END\n"
                + "POST-IK: PRE-IK REJECTED • CYAN ARROW: RIG +Z / WORLD +Z";
        }

        private static string ToVisibleText(ContactLockGateBInjectedEvidence injectedEvidence) => injectedEvidence switch
        {
            ContactLockGateBInjectedEvidence.Missing => "MISSING",
            ContactLockGateBInjectedEvidence.Stale => "STALE",
            ContactLockGateBInjectedEvidence.Invalid => "INVALID",
            ContactLockGateBInjectedEvidence.Contradictory => "CONTRADICTORY",
            ContactLockGateBInjectedEvidence.FallbackMasking => "FALLBACK-MASKING (INVALID)",
            ContactLockGateBInjectedEvidence.Admitted => "ADMITTED",
            _ => throw new ArgumentOutOfRangeException(nameof(injectedEvidence), injectedEvidence, null),
        };

        private static Vector3 LabelOffset()
            =>
            // Focused captures retain one bundle. Reserve a high, screen-edge lane for its billboarding status panel,
            // leaving clear negative space around the cyan target and magenta end geometry in both front and right
            // cameras. The position remains tied to the live fallback target rather than becoming a detached legend.
                new(0.26f, 0.46f, 0.30f);

        private static float MarkerClearance(Vector3 markerCentre, Vector3 target, float markerRadius)
            => markerCentre.DistanceTo(target) - markerRadius;

        private static void UpdateSolvedLegLine(MeshInstance3D line, Vector3 start, Vector3 end)
        {
            float length = start.DistanceTo(end);
            line.Visible = length > 0.0001f;
            if (!line.Visible)
            {
                return;
            }

            line.GlobalPosition = (start + end) * 0.5f;
            line.LookAt(end, Vector3.Up);
            line.RotateObjectLocal(Vector3.Right, -Mathf.Pi * 0.5f);
            line.Scale = new Vector3(1.0f, length, 1.0f);
        }

        private static void UpdateEvidenceBridge(MeshInstance3D bridge, Vector3 start, Vector3 end)
        {
            float length = start.DistanceTo(end);
            bridge.Visible = length > 0.0001f;
            if (!bridge.Visible)
            {
                return;
            }

            bridge.GlobalPosition = (start + end) * 0.5f;
            bridge.LookAt(end, Vector3.Up);
            bridge.RotateObjectLocal(Vector3.Right, -Mathf.Pi * 0.5f);
            bridge.Scale = new Vector3(0.075f, length, 0.075f);
        }

        private static MeshInstance3D MakeSolvedLegLine()
        {
            StandardMaterial3D material = new()
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.92f, 0.95f, 1.0f),
                EmissionEnabled = true,
                Emission = new Color(0.45f, 0.52f, 0.7f),
            };
            CylinderMesh mesh = new()
            {
                Height = 1.0f,
                TopRadius = 0.012f,
                BottomRadius = 0.012f,
            };
            return new MeshInstance3D { Mesh = mesh, MaterialOverride = material };
        }

        private static MeshInstance3D MakeMarker(Mesh mesh, Color colour, float uniformScale = 0.05f)
        {
            StandardMaterial3D material = new()
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = colour,
                EmissionEnabled = true,
                Emission = colour,
            };
            return new MeshInstance3D { Mesh = mesh, MaterialOverride = material, Scale = Vector3.One * uniformScale };
        }
    }
}

#pragma warning restore CS1591, IDE0007, IDE0008, IDE0048, IDE0052, IDE0055, IDE0058, IDE0072, IDE0290
