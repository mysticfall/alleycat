using AlleyCat.Character;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.IK;
using AlleyCat.IK.Pose;
using AlleyCat.Rigging.Installation;
using AlleyCat.Rigging.Physics;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Rigging.Installation;

/// <summary>
/// Integration coverage for rig-domain installers built on the CORE-005 scene installer framework.
/// </summary>
public sealed class RigInstallerIntegrationTests
{
    private const string PlayerInstallerPath =
        "res://assets/characters/templates/installers/player_installer.tscn";

    private const string NpcInstallerPath =
        "res://assets/characters/templates/installers/npc_installer.tscn";

    private const string ReferenceFemaleScenePath =
        "res://assets/characters/templates/reference_female/reference_female_base.tscn";

    private const string ReferenceFemaleModelScenePath =
        "res://assets/characters/reference/female/reference_female.blend";

    private const string ReferenceFemaleNpcTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_npc.tscn";

    private const string ReferenceFemalePlayerTemplatePath =
        "res://assets/characters/templates/reference_female/reference_female_player.tscn";

    private const string AllyScenePath =
        "res://assets/characters/reference/ally.tscn";

    private const string PlayerScenePath =
        "res://assets/characters/reference/player.tscn";

    private const string PlayerVRIKTemplatePath =
        "res://assets/characters/templates/ik/vrik.tscn";

    private static readonly string[] _expectedSkeletonModifierOrder =
    [
        "FootTargetSyncController",
        "NeckSpineIK",
        "HeadCopyRotation",
        "NeckTwistDisperser",
        "RightArmIKController",
        "LeftArmIKController",
        "RightArmTwoBoneIKController",
        "LeftArmTwoBoneIKController",
        "RightHandCopyRotation",
        "LeftHandCopyRotation",
        "RightLegIKController",
        "LeftLegIKController",
        "RightLegTwoBoneIKController",
        "LeftLegTwoBoneIKController",
        "CopyRightFootRotation",
        "CopyLeftFootRotation",
    ];

    /// <summary>
    /// Attachment installation uses the configured skeleton path and does not assume a Female/GeneralSkeleton topology.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_ContextSkeleton_CreatesAttachmentsUnderActualSkeleton()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using Node targetTemplateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("Female/GeneralSkeleton"),
            TargetSkeleton = true,
        };
        using var targetInstaller = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("IKTargets"),
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);
        RigInstallationContext targetContext = CreateCharacterContext(fixture, targetTemplateRoot);

        SceneInstallationResult targets = targetInstaller.Install(targetContext);
        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(targets.Succeeded, string.Join('\n', targets.Errors));
        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.False(fixture.Root.HasNode("Female/GeneralSkeleton"));
        AssertInstalledAttachment(fixture.Skeleton, "Head");
        AssertInstalledAttachment(fixture.Skeleton, "LeftHand");
        AssertInstalledAttachment(fixture.Skeleton, "RightHand");

        BoneAttachment3D head = fixture.Skeleton.GetNode<BoneAttachment3D>("Head");
        Marker3D viewpoint = head.GetNode<Marker3D>("Viewpoint");
        Assert.Same(head, viewpoint.GetParent());
        Assert.NotEqual(Vector3.Zero, viewpoint.Transform.Origin);
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "Head"));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "LeftHand"));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "RightHand"));
    }

    /// <summary>
    /// IK target installation creates root-level targets with stable node types and remains idempotent.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_ContextTemplate_CreatesRootLevelTargetContainerOnce()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("IKTargets"),
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);

        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Node3D targets = fixture.Root.GetNode<Node3D>("IKTargets");
        Assert.Same(fixture.Root, targets.GetParent());
        _ = Assert.IsType<CharacterBody3D>(targets.GetNode("Head"));
        _ = Assert.IsType<Marker3D>(targets.GetNode("HeadSolve"));
        _ = Assert.IsType<Marker3D>(targets.GetNode("RightElbow"));
        _ = Assert.IsType<Marker3D>(targets.GetNode("LeftElbow"));
        _ = Assert.IsType<AnimatableBody3D>(targets.GetNode("RightHand"));
        _ = Assert.IsType<AnimatableBody3D>(targets.GetNode("LeftHand"));
        _ = Assert.IsType<Marker3D>(targets.GetNode("RightKnee"));
        _ = Assert.IsType<Marker3D>(targets.GetNode("LeftKnee"));
        _ = Assert.IsType<CharacterBody3D>(targets.GetNode("RightFoot"));
        _ = Assert.IsType<CharacterBody3D>(targets.GetNode("LeftFoot"));
        Assert.Equal(1, CountDirectChildren(fixture.Root, "IKTargets"));
        Assert.Equal(10, targets.GetChildCount());
    }

    /// <summary>
    /// Rig installers conventionally resolve a single skeleton under the composite target root.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_MissingSkeletonService_FailsClearly()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("Female/GeneralSkeleton"),
            TargetSkeleton = true,
        };

        SceneInstallationResult result = installer.Install(new SceneInstallationContext(fixture.Root));

        Assert.False(result.Succeeded);
        Assert.Contains(nameof(RigInstallationContext), string.Join('\n', result.Errors), StringComparison.Ordinal);
        Assert.False(fixture.Skeleton.HasNode("Head/Viewpoint"));
    }

    /// <summary>
    /// Dynamic physical rig installation is a context-only template consumer and fails rather than no-oping without a template root.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRigTemplateInstaller_MissingTemplateRootWithSkeletonService_FailsClearly()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using var installer = new DynamicPhysicalRigTemplateInstaller
        {
            Name = "DynamicPhysicalRigInstaller",
            TargetSkeleton = true,
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("Female/GeneralSkeleton/DynamicPhysicalRig"),
        };
        SceneInstallationResult result = installer.Install(new SceneInstallationContext(fixture.Root));

        Assert.False(result.Succeeded);
        string errors = string.Join('\n', result.Errors);
        Assert.Contains(nameof(RigInstallationContext), errors, StringComparison.Ordinal);
        Assert.False(fixture.Skeleton.HasNode("DynamicPhysicalRig"));
    }

    /// <summary>
    /// Dynamic physical rig installation still copies and configures a role-provided template rig under the resolved skeleton.
    /// </summary>
    [Headless]
    [Fact]
    public void DynamicPhysicalRigTemplateInstaller_ContextTemplate_InstallsRigUnderSkeleton()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = CreatePackedTemplateRoot(
            new Node { Name = "TemplateRoot" },
            root =>
            {
                var templateSkeleton = new Skeleton3D { Name = "TemplateSkeleton" };
                templateSkeleton.AddChild(new DynamicPhysicalRig { Name = "DynamicPhysicalRig" });
                root.AddChild(templateSkeleton);
            }).Instantiate();
        using var installer = new DynamicPhysicalRigTemplateInstaller
        {
            Name = "DynamicPhysicalRigInstaller",
            TargetSkeleton = true,
            InstallMode = TemplateInstallMode.SelectedNode,
            Enabled = false,
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);

        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        DynamicPhysicalRig rig = fixture.Skeleton.GetNode<DynamicPhysicalRig>("DynamicPhysicalRig");
        Assert.Same(fixture.Skeleton, rig.GetParent());
        Assert.False(rig.Enabled);
        Assert.True(SceneInstallationMetadata.HasInstalled(rig, context, installer));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "DynamicPhysicalRig"));
    }

    /// <summary>
    /// Ambiguous skeleton discovery fails even when one candidate uses a legacy fixture name.
    /// </summary>
    [Headless]
    [Fact]
    public void RigRoleTemplateSceneInstaller_WithMultipleSkeletonsFailsBeforeChildrenRun()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        var legacyNamedSkeleton = new Skeleton3D
        {
            Name = "GeneralSkeleton",
        };
        fixture.Root.AddChild(legacyNamedSkeleton);
        legacyNamedSkeleton.Owner = fixture.Root;
        using var installer = new RigRoleTemplateSceneInstaller
        {
            Template = LoadPackedScene(ReferenceFemaleScenePath),
            Installers =
            [
                new RigTemplateSubtreeInstaller
                {
                    InstallMode = TemplateInstallMode.SelectedNodeChildren,
                    SourcePath = new NodePath("Female/GeneralSkeleton"),
                    TargetSkeleton = true,
                },
            ],
        };

        SceneInstallationResult result = installer.Install(new SceneInstallationContext(fixture.Root));

        Assert.False(result.Succeeded);
        string errors = string.Join('\n', result.Errors);
        Assert.Contains("contains 2 skeletons", errors, StringComparison.Ordinal);
        Assert.Contains("Configure a single root-level skeleton path", errors, StringComparison.OrdinalIgnoreCase);
        Assert.False(legacyNamedSkeleton.HasNode("Head/Viewpoint"));
        Assert.False(fixture.Skeleton.HasNode("Head/Viewpoint"));
    }

    /// <summary>
    /// Character modules encapsulate placement while remaining independently idempotent.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterModules_DelegatePlacementToTypedModules()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node attachmentsTemplateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using Node targetsTemplateRoot = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        using var attachmentInstaller = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("Female/GeneralSkeleton"),
            TargetSkeleton = true,
        };
        using var targetInstaller = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.SelectedNode,
            SourcePath = new NodePath("IKTargets"),
        };
        RigInstallationContext context = CreateCharacterContext(fixture, attachmentsTemplateRoot);
        RigInstallationContext targetsContext = CreateCharacterContext(fixture, targetsTemplateRoot);

        SceneInstallationResult targetFirst = targetInstaller.Install(targetsContext);
        SceneInstallationResult first = attachmentInstaller.Install(context);
        SceneInstallationResult targetSecond = targetInstaller.Install(targetsContext);
        SceneInstallationResult second = attachmentInstaller.Install(context);

        Assert.True(targetFirst.Succeeded, string.Join('\n', targetFirst.Errors));
        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(targetSecond.Succeeded, string.Join('\n', targetSecond.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.True(fixture.Skeleton.HasNode("Head/Viewpoint"));
        Assert.True(fixture.Root.HasNode("IKTargets/RightHand"));
        Assert.Same(fixture.Skeleton, fixture.Skeleton.GetNode("Head").GetParent());
        Assert.Same(fixture.Root, fixture.Root.GetNode("IKTargets").GetParent());
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "Head"));
        Assert.Equal(1, CountDirectChildren(fixture.Root, "IKTargets"));
    }

    /// <summary>
    /// The reference female template exposes IK controller references as inspector-readable node references.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleTemplate_UsesInspectorReadableIKControllerReferences()
    {
        using Node modifiersTemplate = LoadPackedScene(ReferenceFemaleScenePath).Instantiate();
        Node skeleton = modifiersTemplate.GetNode("Female/GeneralSkeleton");

        Assert.False(skeleton.GetNode("FootTargetSyncController").HasMeta("template_bindings"));
        Assert.Same(modifiersTemplate.GetNode("IKTargets/LeftFoot"), GetPropertyValue(skeleton.GetNode("FootTargetSyncController"), "LeftFootTarget"));
        Assert.Same(modifiersTemplate.GetNode("IKTargets/RightFoot"), GetPropertyValue(skeleton.GetNode("RightLegIKController"), "FootTarget"));
        Assert.Same(modifiersTemplate.GetNode("IKTargets/LeftKnee"), GetPropertyValue(skeleton.GetNode("LeftLegIKController"), "PoleTarget"));
    }

    /// <summary>
    /// The player installer is a template-aware role installer scene that owns the full player template binding.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerInstallerAsset_UsesTemplateAwareRootAndAddsPlayerRigLast()
    {
        string installerText = ReadProjectFile(PlayerInstallerPath);

        Assert.Contains(ReferenceFemalePlayerTemplatePath, installerText, StringComparison.Ordinal);
        Assert.Contains(ReferenceFemaleModelScenePath, installerText, StringComparison.Ordinal);
        Assert.Contains("RigRoleTemplateSceneInstaller.cs", installerText, StringComparison.Ordinal);
        Assert.Contains("TemplateRootSubtreesInstaller", installerText, StringComparison.Ordinal);
        Assert.Contains("TemplateSkeletonSubtreesInstaller", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationTreeRootOverride", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("animation_tree_root_player.tres", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerVRIKTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("HipReconciliationTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentsTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerVRIKTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("HipReconciliationTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentsTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("instance=ExtResource(\"1_base\")", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("CharacterIKSubsystemInstaller.cs", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("SourcePath = NodePath(\"Female\")", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Female/GeneralSkeleton", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetSkeleton", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("\nSkeleton =", installerText, StringComparison.Ordinal);

        PackedScene moduleScene = LoadPackedScene(PlayerInstallerPath);

        using Node instance = moduleScene.Instantiate();

        Assert.Equal("PlayerCharacterInstaller", instance.Name);
        AssertInstallerType(instance, typeof(RigRoleTemplateSceneInstaller));
        object[] installers = GetInstallerArray(instance);
        Assert.Equal(6, installers.Length);
        AssertInstallerType(installers[0], typeof(RigTemplateSubtreeInstaller));
        AssertInstallerType(installers[1], typeof(RigTemplateSkeletonSubtreeInstaller));
        AssertInstallerType(installers[2], typeof(BodyPartsInstaller));
        AssertInstallerType(installers[3], typeof(DynamicPhysicalRigTemplateInstaller));
        AssertInstallerType(installers[4], typeof(CharacterRuntimeSubsystemInstaller));
        AssertInstallerType(installers[5], typeof(PlayerRigInstaller));
        AssertRoleChildInstallersDoNotExposeTemplateAssets(instance);
    }

    /// <summary>
    /// The NPC installer is a template-aware role installer scene that owns the full NPC template binding.
    /// </summary>
    [Headless]
    [Fact]
    public void NpcInstallerAsset_UsesTemplateAwareRootAndAddsNpcIKOwner()
    {
        string installerText = ReadProjectFile(NpcInstallerPath);

        Assert.Contains(ReferenceFemaleNpcTemplatePath, installerText, StringComparison.Ordinal);
        Assert.Contains(ReferenceFemaleModelScenePath, installerText, StringComparison.Ordinal);
        Assert.Contains("RigRoleTemplateSceneInstaller.cs", installerText, StringComparison.Ordinal);
        Assert.Contains("TemplateRootSubtreesInstaller", installerText, StringComparison.Ordinal);
        Assert.Contains("TemplateSkeletonSubtreesInstaller", installerText, StringComparison.Ordinal);
        Assert.Contains("CharacterIKSubsystemInstaller.cs", installerText, StringComparison.Ordinal);
        Assert.Contains("NPCIKSubsystemInstaller", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("IKTargetsTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("IKModifiersTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("HandGrabProvidersTemplate = ExtResource", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("IKTargetsTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("IKModifiersTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("HandGrabProvidersTemplate", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("SourcePath = NodePath(\"Female\")", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("Female/GeneralSkeleton", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("TargetSkeleton", installerText, StringComparison.Ordinal);
        Assert.DoesNotContain("\nSkeleton =", installerText, StringComparison.Ordinal);

        using Node instance = LoadPackedScene(NpcInstallerPath).Instantiate();

        Assert.Equal("NPCCharacterInstaller", instance.Name);
        AssertInstallerType(instance, typeof(RigRoleTemplateSceneInstaller));
        object[] installers = GetInstallerArray(instance);
        Assert.Equal(6, installers.Length);
        AssertInstallerType(installers[0], typeof(RigTemplateSubtreeInstaller));
        AssertInstallerType(installers[1], typeof(RigTemplateSkeletonSubtreeInstaller));
        AssertInstallerType(installers[2], typeof(BodyPartsInstaller));
        AssertInstallerType(installers[3], typeof(DynamicPhysicalRigTemplateInstaller));
        AssertInstallerType(installers[4], typeof(CharacterRuntimeSubsystemInstaller));
        AssertInstallerType(installers[5], typeof(CharacterIKSubsystemInstaller));
        AssertRoleChildInstallersDoNotExposeTemplateAssets(instance);
    }

    /// <summary>
    /// The player scene delegates player-specific runtime setup to templates instead of baking runtime nodes itself.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerSceneAsset_DoesNotSerialiseBakedPlayerIKSetup()
    {
        string playerSceneText = ReadProjectFile(PlayerScenePath);

        Assert.Contains("[node name=\"Player\"", playerSceneText, StringComparison.Ordinal);
        Assert.Contains("groups=[\"Player\"]", playerSceneText, StringComparison.Ordinal);
        Assert.Contains("player_installer.tscn", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(ReferenceFemalePlayerTemplatePath, playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerController", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAITranscriber", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("PlayerController.cs", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAITranscriber.cs", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("RigRoleTemplateSceneInstaller.cs", playerSceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("BaseCharacterInstaller", playerSceneText, StringComparison.Ordinal);

        string[] forbiddenMarkers =
        [
            "[node name=\"VRIK\"",
            "[node name=\"PoseStateMachine\"",
            "HeadFallbackIntentProvider",
            "RightHandFallbackIntentProvider",
            "LeftHandFallbackIntentProvider",
            "RightFootFallbackIntentProvider",
            "LeftFootFallbackIntentProvider",
            "RightHandGrabProvider",
            "LeftHandGrabProvider",
            "HipReconciliationModifier",
            "PlayerRigInstaller.cs",
            "AnimationTreeRootOverride",
            "animation_tree_root_player.tres",
            "PermissionSourceNodes",
            "[editable path=\"VRIK\"]",
        ];

        foreach (string forbiddenMarker in forbiddenMarkers)
        {
            Assert.DoesNotContain(forbiddenMarker, playerSceneText, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The inherited player template leaves common runtime nodes on the base scene and owns only player-specific
    /// overrides.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerTemplate_OwnsPlayerAnimationRootAndInspectableVRIKBindings()
    {
        string playerTemplateText = ReadProjectFile(ReferenceFemalePlayerTemplatePath);
        string playerRigInstallerSource = ReadProjectFile("res://src/IK/PlayerRigInstaller.cs");

        Assert.Contains("[node name=\"Player\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("reference_female_base.tscn", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"AnimationTree\" parent=\".\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("animation_tree_root_player.tres", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"VRIK\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"PlayerController\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"OpenAITranscriber\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("vrik.tscn", playerTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("AnimationTreeRootOverride", playerTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"Locomotion\" parent=\".\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("HandHolderNode = NodePath(\"../Hands\")", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"RightHand\" parent=\"Hands\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"LeftHand\" parent=\"Hands\"", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("GrabTargetProvider = NodePath(\"../../VRIK/RightHandGrabProvider\")", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("GrabTargetProvider = NodePath(\"../../VRIK/LeftHandGrabProvider\")", playerTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("PermissionSourceNodes", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("PoseStateMachine = NodePath(\"PoseStateMachine\")", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("HeadFallbackIntentProvider = NodePath(\"HeadFallbackIntentProvider\")", playerTemplateText, StringComparison.Ordinal);
        Assert.Contains("RightFootFallbackIntentProvider = NodePath(\"RightFootFallbackIntentProvider\")", playerTemplateText, StringComparison.Ordinal);
        string baseTemplateText = ReadProjectFile(ReferenceFemaleScenePath);
        Assert.Contains("AnimationTree = NodePath(\"../../AnimationTree\")", baseTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("animation_tree_root_player.tres", playerRigInstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ResourceLoader.Load", playerRigInstallerSource, StringComparison.Ordinal);

        string playerVRIKText = ReadProjectFile(PlayerVRIKTemplatePath);
        Assert.Contains("[node name=\"RightHandGrabProvider\"", playerVRIKText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"LeftHandGrabProvider\"", playerVRIKText, StringComparison.Ordinal);
        Assert.Contains("DefaultProvider = NodePath(\"../RightHandFallbackIntentProvider\")", playerVRIKText, StringComparison.Ordinal);
        Assert.Contains("DefaultProvider = NodePath(\"../LeftHandFallbackIntentProvider\")", playerVRIKText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The inherited NPC template leaves base/common hand runtime nodes on the base scene and relies on installers for
    /// grab-provider rebinding.
    /// </summary>
    [Headless]
    [Fact]
    public void NpcTemplate_DoesNotRedeclareInheritedBaseRuntimeHandNodes()
    {
        string npcTemplateText = ReadProjectFile(ReferenceFemaleNpcTemplatePath);

        Assert.Contains("reference_female_base.tscn", npcTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"CharacterIK\"", npcTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"RightHandGrabProvider\"", npcTemplateText, StringComparison.Ordinal);
        Assert.Contains("[node name=\"LeftHandGrabProvider\"", npcTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"RightHand\" parent=\"Hands\"", npcTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"LeftHand\" parent=\"Hands\"", npcTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("GrabTargetProvider = NodePath(\"../../CharacterIK/RightHandGrabProvider\")", npcTemplateText, StringComparison.Ordinal);
        Assert.DoesNotContain("GrabTargetProvider = NodePath(\"../../CharacterIK/LeftHandGrabProvider\")", npcTemplateText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runtime player installation still materialises and binds the player-specific rig inventory.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerScene_RuntimeInstaller_MaterialisesAndBindsPlayerRig()
    {
        using Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();
        object result = InvokeLoadedInstaller(playerRoot.GetNode("PlayerCharacterInstaller"), playerRoot);

        AssertLoadedInstallSucceeded(result);

        Node playerVRIK = playerRoot.GetNode("VRIK");
        Assert.Contains(typeof(PlayerVRIK).FullName, GetTypeHierarchyNames(playerVRIK.GetType()), StringComparer.Ordinal);
        Node poseStateMachine = playerRoot.GetNode("VRIK/PoseStateMachine");
        Assert.Contains(typeof(PoseStateMachine).FullName, GetTypeHierarchyNames(poseStateMachine.GetType()), StringComparer.Ordinal);
        Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Node hipModifier = skeleton.GetNode("HipReconciliationModifier");
        Assert.Contains(typeof(HipReconciliationModifier).FullName, GetTypeHierarchyNames(hipModifier.GetType()), StringComparer.Ordinal);
        AnimationTree animationTree = Assert.IsType<AnimationTree>(playerRoot.GetNode("AnimationTree"), exactMatch: false);
        Node locomotion = playerRoot.GetNode("Locomotion");
        Assert.Contains(typeof(LocomotionBase).FullName, GetTypeHierarchyNames(locomotion.GetType()), StringComparer.Ordinal);
        AssertSharedRuntimeBindings(playerRoot);

        Assert.Same(poseStateMachine, GetPropertyValue(playerVRIK, nameof(PlayerVRIK.PoseStateMachine)));
        Assert.Same(poseStateMachine, GetPropertyValue(hipModifier, nameof(HipReconciliationModifier.StateMachine)));
        Node?[] permissionSourceNodes = Assert.IsType<Node?[]>(GetPropertyValue(locomotion, nameof(LocomotionBase.PermissionSourceNodes)));
        Assert.Same(poseStateMachine, Assert.Single(permissionSourceNodes));
        AssertPlayerAnimationTreeRuntime(playerRoot, animationTree, expectPlaybackCurrentState: false);

        string[] providerPaths =
        [
            "HeadFallbackIntentProvider",
            "RightHandFallbackIntentProvider",
            "LeftHandFallbackIntentProvider",
            "RightFootFallbackIntentProvider",
            "LeftFootFallbackIntentProvider",
            "RightHandGrabProvider",
            "LeftHandGrabProvider",
        ];

        foreach (string providerPath in providerPaths)
        {
            Assert.True(playerVRIK.HasNode(providerPath), $"Expected player VRIK runtime installation to contain '{providerPath}'.");
        }

        Assert.Same(playerVRIK.GetNode("RightHandGrabProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.RightHandIKTargetIntentProvider)));
        Assert.Same(playerVRIK.GetNode("LeftHandGrabProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.LeftHandIKTargetIntentProvider)));
        Assert.Same(playerVRIK.GetNode("HeadFallbackIntentProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.HeadFallbackIntentProvider)));
        Assert.Same(playerVRIK.GetNode("RightHandFallbackIntentProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.RightHandFallbackIntentProvider)));
        Assert.Same(playerVRIK.GetNode("LeftHandFallbackIntentProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.LeftHandFallbackIntentProvider)));
        Assert.Same(playerVRIK.GetNode("RightFootFallbackIntentProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.RightFootFallbackIntentProvider)));
        Assert.Same(playerVRIK.GetNode("LeftFootFallbackIntentProvider"), GetPropertyValue(playerVRIK, nameof(CharacterIK.LeftFootFallbackIntentProvider)));
        Assert.Same(playerVRIK.GetNode("RightHandFallbackIntentProvider"), GetPropertyValue(playerVRIK.GetNode("RightHandGrabProvider"), nameof(HandGrabTargetProvider.DefaultProvider)));
        Assert.Same(playerVRIK.GetNode("LeftHandFallbackIntentProvider"), GetPropertyValue(playerVRIK.GetNode("LeftHandGrabProvider"), nameof(HandGrabTargetProvider.DefaultProvider)));
        Assert.Same(playerRoot.GetNode("Female/GeneralSkeleton/Head/Viewpoint"), GetPropertyValue(playerVRIK.GetNode("HeadFallbackIntentProvider"), nameof(XRHeadTargetIntentProvider.Viewpoint)));
        Assert.Same(playerRoot.GetNode("IKTargets/RightFoot"), GetPropertyValue(playerVRIK.GetNode("RightFootFallbackIntentProvider"), nameof(AnimationSynchronizedFootTargetProvider.FootTarget)));
        Assert.Same(playerRoot.GetNode("IKTargets/LeftFoot"), GetPropertyValue(playerVRIK.GetNode("LeftFootFallbackIntentProvider"), nameof(AnimationSynchronizedFootTargetProvider.FootTarget)));
    }

    /// <summary>
    /// Player rig installation fails fast when its required animation runtime dependency is missing.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerRigInstaller_MissingAnimationTree_FailsWithRequiredDependencyMessage()
    {
        using Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();
        object initialResult = InvokeLoadedInstaller(playerRoot.GetNode("PlayerCharacterInstaller"), playerRoot);
        AssertLoadedInstallSucceeded(initialResult);
        RemoveRequiredNode(playerRoot, "AnimationTree");

        object result = InvokeLoadedInstaller(playerRoot.GetNode("PlayerCharacterInstaller/PlayerRigInstaller"), playerRoot);

        AssertLoadedInstallFailedContaining(result, "Rig installer");
    }

    /// <summary>
    /// Player rig installation fails fast when its required locomotion runtime dependency is missing.
    /// </summary>
    [Headless]
    [Fact]
    public void PlayerRigInstaller_MissingLocomotion_FailsWithRequiredDependencyMessage()
    {
        using Node playerRoot = LoadPackedScene(PlayerScenePath).Instantiate();
        object initialResult = InvokeLoadedInstaller(playerRoot.GetNode("PlayerCharacterInstaller"), playerRoot);
        AssertLoadedInstallSucceeded(initialResult);
        RemoveRequiredNode(playerRoot, "Locomotion");

        object result = InvokeLoadedInstaller(playerRoot.GetNode("PlayerCharacterInstaller/PlayerRigInstaller"), playerRoot);

        AssertLoadedInstallFailedContaining(result, "Rig installer");
    }

    /// <summary>
    /// The shipped player scene resolves runtime and player-role bindings through the real auto-install path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_AutoInstall_BindsRuntimeAndRoleReferences()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Assert.Equal(Error.Ok, sceneTree.ChangeSceneToPacked(LoadPackedScene(PlayerScenePath)));
        await WaitForFramesAsync(sceneTree, 8);

        Node playerRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected player scene to become current scene.");

        AssertSharedRuntimeBindings(playerRoot);
        Node playerVRIK = playerRoot.GetNode("VRIK");
        Node poseStateMachine = playerRoot.GetNode("VRIK/PoseStateMachine");
        Node locomotion = playerRoot.GetNode("Locomotion");
        Node hands = playerRoot.GetNode("Hands");
        Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");

        Assert.Contains(typeof(PlayerVRIK).FullName, GetTypeHierarchyNames(playerVRIK.GetType()), StringComparer.Ordinal);
        Assert.Same(poseStateMachine, GetPropertyValue(playerVRIK, nameof(PlayerVRIK.PoseStateMachine)));
        Assert.Same(poseStateMachine, GetPropertyValue(skeleton.GetNode("HipReconciliationModifier"), nameof(HipReconciliationModifier.StateMachine)));
        Node?[] permissionSourceNodes = Assert.IsType<Node?[]>(GetPropertyValue(locomotion, nameof(LocomotionBase.PermissionSourceNodes)));
        Assert.Same(poseStateMachine, Assert.Single(permissionSourceNodes));
        Assert.Same(locomotion, GetPropertyValue(playerRoot.GetNode("PlayerController"), "LocomotionNode"));
        Assert.Same(hands, GetPropertyValue(playerRoot.GetNode("PlayerController"), "HandHolderNode"));
        AssertPlayerAnimationTreeRuntime(playerRoot, playerRoot.GetNode<AnimationTree>("AnimationTree"), expectPlaybackCurrentState: false);
    }

    /// <summary>
    /// The shipped NPC reference scene resolves runtime and NPC IK bindings through the real auto-install path.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AllyScene_AutoInstall_BindsRuntimeAndRoleReferences()
    {
        SceneTree sceneTree = GetSceneTree();
        await WaitForFramesAsync(sceneTree, 2);

        Assert.Equal(Error.Ok, sceneTree.ChangeSceneToPacked(LoadPackedScene(AllyScenePath)));
        await WaitForFramesAsync(sceneTree, 8);

        Node npcRoot = sceneTree.CurrentScene
            ?? throw new Xunit.Sdk.XunitException("Expected ally scene to become current scene.");

        AssertSharedRuntimeBindings(npcRoot);
        Node characterIK = npcRoot.GetNode("CharacterIK");
        Skeleton3D skeleton = npcRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");

        Assert.Contains(typeof(CharacterIK).FullName, GetTypeHierarchyNames(characterIK.GetType()), StringComparer.Ordinal);
        Assert.Same(skeleton.GetNode("Head/Viewpoint"), GetPropertyValue(characterIK, nameof(CharacterIK.Viewpoint)));
        Assert.Same(npcRoot.GetNode("IKTargets/RightHand"), GetPropertyValue(characterIK, nameof(CharacterIK.RightHandIKTarget)));
        Assert.Same(npcRoot.GetNode("IKTargets/LeftHand"), GetPropertyValue(characterIK, nameof(CharacterIK.LeftHandIKTarget)));
        Assert.Same(skeleton.GetNode("DynamicPhysicalRig"), GetPropertyValue(characterIK, nameof(CharacterIK.PhysicalRig)));
        Assert.Same(characterIK.GetNode("RightHandGrabProvider"), GetPropertyValue(characterIK, nameof(CharacterIK.RightHandIKTargetIntentProvider)));
        Assert.Same(characterIK.GetNode("LeftHandGrabProvider"), GetPropertyValue(characterIK, nameof(CharacterIK.LeftHandIKTargetIntentProvider)));
        Assert.Same(characterIK.GetNode("RightHandGrabProvider"), GetPropertyValue(npcRoot.GetNode("Hands/RightHand"), "GrabTargetProvider"));
        Assert.Same(characterIK.GetNode("LeftHandGrabProvider"), GetPropertyValue(npcRoot.GetNode("Hands/LeftHand"), "GrabTargetProvider"));
    }

    /// <summary>
    /// The high-level IK subsystem binds IK topology copied from the role-provided template context.
    /// </summary>
    [Headless]
    [Fact]
    public void CharacterIKSubsystemInstaller_BindsContextInstalledTargetsAndModifiers()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = LoadPackedScene(ReferenceFemaleNpcTemplatePath).Instantiate();
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);
        using var rootInstaller = new RigTemplateSubtreeInstaller
        {
            Name = "TemplateRootSubtreesInstaller",
            InstallMode = TemplateInstallMode.TemplateRootChildren,
        };
        using var skeletonInstaller = new RigTemplateSubtreeInstaller
        {
            Name = "TemplateSkeletonSubtreesInstaller",
            TargetSkeleton = true,
            InstallMode = TemplateInstallMode.SelectedNodeChildren,
            SourcePath = new NodePath("Female/GeneralSkeleton"),
        };
        using var installer = new CharacterIKSubsystemInstaller
        {
            Name = "IKSubsystemInstaller",
            BindCharacterIKNode = false,
        };

        Assert.True(rootInstaller.Install(context).Succeeded);
        Assert.True(skeletonInstaller.Install(context).Succeeded);
        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.True(fixture.Root.HasNode("IKTargets/RightHand"));
        Assert.True(fixture.Skeleton.HasNode("RightArmIKController"));
        Assert.True(fixture.Skeleton.HasNode("CopyLeftFootRotation"));
        Assert.Equal(1, CountDirectChildren(fixture.Root, "IKTargets"));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "RightArmIKController"));
    }

    /// <summary>
    /// The visual reference scene inherits from the imported Blender scene without embedding imported mesh resources.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemaleVisualAsset_InheritsBlendSceneWithoutEmbeddedMeshResources()
    {
        string sceneText = ReadProjectFile(ReferenceFemaleScenePath);

        Assert.Contains("path=\"res://assets/characters/reference/female/reference_female.blend\"", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain(".godot/imported", sceneText, StringComparison.Ordinal);
        AssertNoEmbeddedMeshData(sceneText);

        PackedScene visualScene = LoadPackedScene(ReferenceFemaleScenePath);

        using Node visual = visualScene.Instantiate();

        Assert.True(visual.HasNode("Female/GeneralSkeleton"));
        Assert.True(visual.HasNode("AnimationPlayer"));
    }

    /// <summary>
    /// The runtime role scenes are minimal actual scenes backed by template-only role sources.
    /// </summary>
    [Headless]
    [Fact]
    public void RuntimeRoleScenes_UseRoleTemplateInstallerWithoutEmbeddingVisualMeshData()
    {
        AssertRuntimeRoleSceneUsesTemplateInstaller(
            PlayerScenePath,
            ReferenceFemalePlayerTemplatePath,
            "PlayerCharacterInstaller",
            "PlayerCharacterInstaller/PlayerRigInstaller");
        AssertRuntimeRoleSceneUsesTemplateInstaller(
            AllyScenePath,
            ReferenceFemaleNpcTemplatePath,
            "NPCCharacterInstaller",
            "NPCCharacterInstaller/NPCIKSubsystemInstaller");

        string allySceneText = ReadProjectFile(AllyScenePath);
        Assert.DoesNotContain("CharacterIK.cs", allySceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("HandGrabTargetProvider.cs", allySceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("[node name=\"CharacterIK\"", allySceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("RightHandGrabProvider", allySceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("LeftHandGrabProvider", allySceneText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Role installer children expose no child-owned template asset properties.
    /// </summary>
    [Headless]
    [Fact]
    public void RoleInstallerModules_DoNotExposeChildTemplateAssets()
    {
        using Node playerModuleNode = LoadPackedScene(PlayerInstallerPath).Instantiate();
        using Node npcModuleNode = LoadPackedScene(NpcInstallerPath).Instantiate();

        AssertRoleChildInstallersDoNotExposeTemplateAssets(playerModuleNode);
        AssertRoleChildInstallersDoNotExposeTemplateAssets(npcModuleNode);
    }

    /// <summary>
    /// Female/GeneralSkeleton remains an asset fixture setting rather than framework source-code knowledge.
    /// </summary>
    [Headless]
    [Fact]
    public void ReferenceFemalePath_IsConfinedToAssetConfiguration()
    {
        Assert.DoesNotContain("Female/GeneralSkeleton", ReadProjectFile("res://src/Rigging/Installation/RigSceneInstaller.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("Female/GeneralSkeleton", ReadProjectFile("res://src/Rigging/Physics/DynamicPhysicalRigTemplateInstaller.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Role-critical installer code resolves authored components by typed contracts rather than reusable template names.
    /// </summary>
    [Headless]
    [Fact]
    public void RoleCriticalInstallerSources_DoNotHardCodeReusableTemplateTopologyNames()
    {
        string playerRigSource = ReadProjectFile("res://src/IK/PlayerRigInstaller.cs");
        string runtimeInstallerSource = ReadProjectFile("res://src/Character/CharacterRuntimeSubsystemInstaller.cs");

        Assert.DoesNotContain("\"HipReconciliationModifier\"", playerRigSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"FootTargetSyncController\"", playerRigSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDirectChild<AnimationTree>", runtimeInstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDirectChild<AnimationPlayer>", runtimeInstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDirectChild<EyesBehaviour>", runtimeInstallerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDirectChild<CharacterLocomotion>", runtimeInstallerSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Skeleton-dependent child installers fail clearly when refreshed without role-provided context services.
    /// </summary>
    [Headless]
    [Fact]
    public void SubsystemInstaller_InstallInEditorWithoutRoleContext_FailsClearly()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using var bodyParts = new BodyPartsInstaller
        {
            Name = "BodyPartsInstaller",
            TargetRoot = fixture.Root,
        };
        using var ik = new CharacterIKSubsystemInstaller
        {
            Name = "IKSubsystemInstaller",
            TargetRoot = fixture.Root,
            BindCharacterIKNode = false,
        };

        InvalidOperationException bodyPartsError = Assert.Throws<InvalidOperationException>(bodyParts.InstallNowInEditor);
        InvalidOperationException ikError = Assert.Throws<InvalidOperationException>(ik.InstallNowInEditor);

        Assert.False(fixture.Skeleton.HasNode("Head/Viewpoint"));
        Assert.False(fixture.Root.HasNode("IKTargets"));
        Assert.False(fixture.Skeleton.HasNode("RightArmIKController"));
        Assert.Contains(nameof(RigInstallationContext), bodyPartsError.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RigInstallationContext), ikError.Message, StringComparison.Ordinal);
        Assert.False(HasGodotProperty(bodyParts, "AttachmentsTemplate"));
        Assert.False(HasGodotProperty(ik, "IKTargetsTemplate"));
    }

    /// <summary>
    /// Child installers under the reference composite require root role context when refreshed individually.
    /// </summary>
    [Headless]
    [Fact]
    public void BaseCharacterChildInstallers_InstallInEditorWithoutRoleContext_FailsClearly()
    {
        using Node character = LoadPackedScene(AllyScenePath).Instantiate();
        object installResult = InvokeLoadedInstaller(character.GetNode("NPCCharacterInstaller"), character);
        AssertLoadedInstallSucceeded(installResult);
        Node composite = character.GetNode("NPCCharacterInstaller");
        Node bodyParts = composite.GetNode("BodyPartsInstaller");
        Node ik = composite.GetNode("NPCIKSubsystemInstaller");

        InvalidOperationException bodyPartsError = Assert.Throws<InvalidOperationException>(() => RefreshIndividually(bodyParts));
        InvalidOperationException ikError = Assert.Throws<InvalidOperationException>(() => RefreshIndividually(ik));

        Skeleton3D skeleton = character.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Assert.Contains(nameof(RigInstallationContext), bodyPartsError.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(RigInstallationContext), ikError.Message, StringComparison.Ordinal);
        Assert.True(character.HasNode("IKTargets/RightHand"));
        Assert.True(skeleton.HasNode("Head/Viewpoint"));
        Assert.True(skeleton.HasNode("RightArmIKController"));
        Assert.True(skeleton.HasNode("DynamicPhysicalRig"));
        Assert.True(character.HasNode("AnimationTree"));
        Assert.True(character.HasNode("Eyes"));
        Assert.True(character.HasNode("Locomotion"));
        Assert.True(character.HasNode("Hands/RightHand"));
        Assert.False(composite.HasNode("IKTargets"));
        Assert.False(composite.HasNode("AnimationTree"));
        Assert.False(composite.HasNode("Eyes"));
        Assert.False(composite.HasNode("Locomotion"));
        Assert.False(composite.HasNode("Hands"));
        Assert.Equal(1, CountDirectChildren(character, "IKTargets"));
        Assert.Equal(1, CountDirectChildren(character, "AnimationTree"));
        Assert.Equal(1, CountDirectChildren(character, "Eyes"));
        Assert.Equal(1, CountDirectChildren(character, "Locomotion"));
        Assert.Equal(1, CountDirectChildren(character, "Hands"));
        Assert.Equal(1, CountDirectChildren(skeleton, "Head"));
        Assert.Equal(1, CountDirectChildren(skeleton, "DynamicPhysicalRig"));
        Assert.Equal(1, CountDirectChildren(skeleton, "RightArmIKController"));
    }

    /// <summary>
    /// Character subtree installers install an authored root once and reuse it on repeated runs.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_RootTemplateInstall_IsIdempotent()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = CreatePackedTemplateRoot(new Node3D { Name = "ReusableModule" }, root => root.AddChild(new Marker3D { Name = "AuthoredMarker" })).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.TemplateRoot,
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);

        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        Assert.True(fixture.Root.HasNode("ReusableModule/AuthoredMarker"));
        _ = Assert.IsType<Node3D>(fixture.Root.GetNode("ReusableModule"));
        _ = Assert.IsType<Marker3D>(fixture.Root.GetNode("ReusableModule/AuthoredMarker"));
        Assert.Equal(1, CountDirectChildren(fixture.Root, "ReusableModule"));
    }

    /// <summary>
    /// Character subtree installers place authored children under the context-provided skeleton without duplicating them.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_SkeletonChildTemplateInstall_IsIdempotent()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        using Node templateRoot = CreatePackedTemplateRoot(new Node { Name = "TemplateInventory" }, root =>
        {
            root.AddChild(new Marker3D { Name = "CustomPole" });
            root.AddChild(new Node3D { Name = "CustomTarget" });
        }).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.TemplateRootChildren,
            TargetSkeleton = true,
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);

        SceneInstallationResult first = installer.Install(context);
        SceneInstallationResult second = installer.Install(context);

        Assert.True(first.Succeeded, string.Join('\n', first.Errors));
        Assert.True(second.Succeeded, string.Join('\n', second.Errors));
        _ = Assert.IsType<Marker3D>(fixture.Skeleton.GetNode("CustomPole"));
        _ = Assert.IsType<Node3D>(fixture.Skeleton.GetNode("CustomTarget"));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "CustomPole"));
        Assert.Equal(1, CountDirectChildren(fixture.Skeleton, "CustomTarget"));
    }

    /// <summary>
    /// Existing nodes with the same name but incompatible type fail with a clear authoring diagnostic.
    /// </summary>
    [Headless]
    [Fact]
    public void RigTemplateSubtreeInstaller_WrongExistingType_FailsClearly()
    {
        using CharacterFixture fixture = CreateAliceFixture();
        fixture.Root.AddChild(new Node3D { Name = "ReusableModule" });
        using Node templateRoot = CreatePackedTemplateRoot(new Marker3D { Name = "ReusableModule" }).Instantiate();
        using var installer = new RigTemplateSubtreeInstaller
        {
            InstallMode = TemplateInstallMode.TemplateRoot,
        };
        RigInstallationContext context = CreateCharacterContext(fixture, templateRoot);

        SceneInstallationResult result = installer.Install(context);

        Assert.False(result.Succeeded);
        string error = Assert.Single(result.Errors);
        Assert.Contains("ReusableModule", error, StringComparison.Ordinal);
        Assert.Contains("Node3D", error, StringComparison.Ordinal);
        Assert.Contains("Marker3D", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reusable template installer sources do not encode production character inventories or concrete asset paths.
    /// </summary>
    [Headless]
    [Fact]
    public void TemplateInstallerSources_DoNotHardCodeProductionInventories()
    {
        string[] reusableInstallerSources =
        [
            "res://src/Core/Installer/TemplateSceneInstallation.cs",
            "res://src/Rigging/Installation/RigTemplateSubtreeInstaller.cs",
        ];
        string[] forbiddenInventoryNames = ["RightElbow", "HeadSolve", "Female/GeneralSkeleton"];

        foreach (string sourcePath in reusableInstallerSources)
        {
            string source = ReadProjectFile(sourcePath);
            foreach (string forbiddenInventoryName in forbiddenInventoryNames)
            {
                Assert.DoesNotContain(forbiddenInventoryName, source, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertInstalledAttachment(Skeleton3D skeleton, string name)
    {
        BoneAttachment3D attachment = skeleton.GetNode<BoneAttachment3D>(name);
        Assert.Same(skeleton, attachment.GetParent());
        Assert.Equal(name, attachment.BoneName.ToString());
        Assert.Equal(skeleton.FindBone(name), attachment.BoneIdx);
    }

    private static void RefreshIndividually(params Node[] installers)
    {
        foreach (Node installer in installers)
        {
            InvokeLoadedRefresh(installer);
        }
    }

    private static void AssertReferenceFemaleInventory(Node character)
    {
        EnsureReferenceFemaleInventoryInstalled(character);

        Assert.True(character.HasNode("Female/GeneralSkeleton/Head/Viewpoint"));
        Assert.True(character.HasNode("Female/GeneralSkeleton/RightHand"));
        Assert.True(character.HasNode("Female/GeneralSkeleton/LeftHand"));
        Assert.True(character.HasNode("Female/GeneralSkeleton/DynamicPhysicalRig"));
        Assert.True(character.HasNode("IKTargets/Head"));
        Assert.True(character.HasNode("IKTargets/HeadSolve"));
        Assert.True(character.HasNode("IKTargets/RightElbow"));
        Assert.True(character.HasNode("IKTargets/LeftElbow"));
        Assert.True(character.HasNode("IKTargets/RightHand"));
        Assert.True(character.HasNode("IKTargets/LeftHand"));
        Assert.True(character.HasNode("IKTargets/RightKnee"));
        Assert.True(character.HasNode("IKTargets/LeftKnee"));
        Assert.True(character.HasNode("IKTargets/RightFoot"));
        Assert.True(character.HasNode("IKTargets/LeftFoot"));
        Assert.True(character.HasNode("AnimationTree"));
        Assert.True(character.HasNode("Eyes"));
        Assert.True(character.HasNode("Locomotion"));
        Assert.True(character.HasNode("Hands/RightHand"));
        Assert.True(character.HasNode("Hands/LeftHand"));
    }

    private static void AssertSharedRuntimeBindings(Node character)
    {
        AnimationTree animationTree = Assert.IsType<AnimationTree>(character.GetNode("AnimationTree"), exactMatch: false);
        Node rightHand = character.GetNode("Hands/RightHand");
        Node leftHand = character.GetNode("Hands/LeftHand");
        Node locomotion = character.GetNode("Locomotion");

        Assert.Contains(typeof(LocomotionBase).FullName, GetTypeHierarchyNames(locomotion.GetType()), StringComparer.Ordinal);
        Assert.Same(animationTree, GetPropertyValue(rightHand, "AnimationTree"));
        Assert.Same(animationTree, GetPropertyValue(leftHand, "AnimationTree"));
        Assert.Same(animationTree, GetPropertyValue(locomotion, nameof(CharacterLocomotion.AnimationTree)));
        Assert.Same(character.GetNode("IKTargets/RightHand"), GetPropertyValue(rightHand, "HandTargetNode"));
        Assert.Same(character.GetNode("IKTargets/LeftHand"), GetPropertyValue(leftHand, "HandTargetNode"));
        Assert.Same(character.GetNode("Female/GeneralSkeleton/RightHand"), GetPropertyValue(rightHand, "HandBoneAttachment"));
        Assert.Same(character.GetNode("Female/GeneralSkeleton/LeftHand"), GetPropertyValue(leftHand, "HandBoneAttachment"));
        Assert.Same(character.GetNode("Female/GeneralSkeleton/DynamicPhysicalRig"), GetPropertyValue(rightHand, "PhysicalRig"));
        Assert.Same(character.GetNode("Female/GeneralSkeleton/DynamicPhysicalRig"), GetPropertyValue(leftHand, "PhysicalRig"));
    }

    private static void AssertPlayerAnimationTreeRuntime(
        Node playerRoot,
        AnimationTree animationTree,
        bool expectPlaybackCurrentState)
    {
        Assert.Equal("res://assets/characters/templates/animation/animation_tree_root_player.tres", animationTree.TreeRoot.ResourcePath);
        AnimationNodeBlendTree root = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
        Assert.NotNull(root.GetNode("LeftHandBlend"));
        Assert.NotNull(root.GetNode("RightHandBlend"));
        Assert.NotNull(root.GetNode("LeftHandPose"));
        Assert.NotNull(root.GetNode("RightHandPose"));

        AnimationNodeStateMachine states = Assert.IsType<AnimationNodeStateMachine>(root.GetNode("States"), exactMatch: false);
        foreach (StringName stateName in states.GetNodeList())
        {
            Assert.NotNull(states.GetNode(stateName));
        }

        if (expectPlaybackCurrentState)
        {
            AnimationNodeStateMachinePlayback playback = animationTree.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
                ?? throw new Xunit.Sdk.XunitException("Expected player AnimationTree States playback to resolve.");
            Assert.Equal("StandingCrouching", playback.GetCurrentNode().ToString());
        }

        Node locomotion = playerRoot.GetNode("Locomotion");
        Assert.Contains(typeof(CharacterLocomotion).FullName, GetTypeHierarchyNames(locomotion.GetType()), StringComparer.Ordinal);
        Assert.Equal("StandingCrouching", GetPropertyValue(locomotion, nameof(CharacterLocomotion.IdleAnimationStateName))?.ToString());
    }

    private static void EnsureReferenceFemaleInventoryInstalled(Node character)
    {
        if (character.HasNode("IKTargets/RightHand") && character.HasNode("Female/GeneralSkeleton/DynamicPhysicalRig"))
        {
            return;
        }

        Node installer = character.GetNode("NPCCharacterInstaller");
        object result = InvokeLoadedInstaller(installer, character);
        AssertLoadedInstallSucceeded(result);
    }

    private static void AssertModifierOrder(Skeleton3D skeleton)
    {
        int previousIndex = -1;
        foreach (string modifierName in _expectedSkeletonModifierOrder)
        {
            Node modifier = skeleton.GetNode(modifierName);
            int index = modifier.GetIndex();
            Assert.True(index > previousIndex, $"Modifier '{modifierName}' should appear after previous IK modifier.");
            previousIndex = index;
        }
    }

    private static void AssertRuntimeRoleSceneUsesTemplateInstaller(
        string scenePath,
        string templatePath,
        string roleInstallerPath,
        params string[] expectedRoleNodes)
    {
        string sceneText = ReadProjectFile(scenePath);
        Assert.DoesNotContain(templatePath, sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("RigRoleTemplateSceneInstaller.cs", sceneText, StringComparison.Ordinal);
        Assert.DoesNotContain("instance=ExtResource(\"1_reference\")", sceneText, StringComparison.Ordinal);
        AssertNoEmbeddedMeshData(sceneText);

        PackedScene scene = LoadPackedScene(scenePath);

        using Node roleRoot = scene.Instantiate();

        Assert.True(roleRoot.HasNode(roleInstallerPath), $"Expected role scene '{scenePath}' to contain role installer '{roleInstallerPath}'.");
        foreach (string expectedRoleNode in expectedRoleNodes)
        {
            Assert.True(roleRoot.HasNode(expectedRoleNode), $"Expected role scene '{scenePath}' to contain '{expectedRoleNode}'.");
        }
    }

    private static void RemoveReferenceFemaleRoleBindings(Node moduleNode)
    {
        Node subsystemInstaller = moduleNode.GetNode("ReferenceFemaleRuntimeSubsystemInstaller");
        moduleNode.RemoveChild(subsystemInstaller);
        subsystemInstaller.Dispose();
    }

    private static void AssertNoEmbeddedMeshData(string sceneText)
    {
        string[] forbiddenMarkers = ["ArrayMesh", "Skin", "_surfaces", "vertex_data", "PackedByteArray"];
        foreach (string marker in forbiddenMarkers)
        {
            Assert.DoesNotContain(marker, sceneText, StringComparison.Ordinal);
        }
    }

    private static PackedScene LoadPackedScene(string path)
    {
        PackedScene? scene = ResourceLoader.Load<PackedScene>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        Assert.NotNull(scene);
        return scene;
    }

    private static PackedScene CreatePackedTemplateRoot(Node root, Action<Node>? configure = null)
    {
        configure?.Invoke(root);
        AssignTemplateOwnerRecursively(root, root);

        var packedScene = new PackedScene();
        Assert.Equal(Error.Ok, packedScene.Pack(root));
        root.Dispose();
        return packedScene;
    }

    private static void AssignTemplateOwnerRecursively(Node owner, Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            child.Owner = owner;
            AssignTemplateOwnerRecursively(owner, child);
        }
    }

    private static string ReadProjectFile(string path)
        => File.ReadAllText(ProjectSettings.GlobalizePath(path));

    private static object[] GetInstallerArray(object composite)
    {
        object? installers = composite.GetType().GetProperty(nameof(RigRoleTemplateSceneInstaller.Installers))?.GetValue(composite);
        Assert.NotNull(installers);
        return [.. Assert.IsAssignableFrom<Array>(installers).Cast<object>()];
    }

    private static void AssertRoleChildInstallersDoNotExposeTemplateAssets(Node roleInstaller)
    {
        string[] forbiddenPropertyNames =
        [
            "Template",
            "AttachmentsTemplate",
            "AnimationTreeTemplate",
            "IKTargetsTemplate",
            "IKModifiersTemplate",
            "HandGrabProvidersTemplate",
            "CharacterIKTemplate",
            "PlayerVRIKTemplate",
            "HipReconciliationTemplate",
            "AnimationTreeRootOverride",
            "Skeleton",
            "EyesTemplate",
            "HandsTemplate",
            "LocomotionTemplate",
        ];

        foreach (Node child in roleInstaller.GetChildren())
        {
            foreach (string propertyName in forbiddenPropertyNames)
            {
                Assert.False(
                    HasGodotProperty(child, propertyName),
                    $"Role child installer '{child.Name}' must not expose template asset property '{propertyName}'.");
            }
        }
    }

    private static bool HasGodotProperty(Node node, string propertyName)
    {
        foreach (Godot.Collections.Dictionary property in node.GetPropertyList())
        {
            if (property["name"].AsString() == propertyName)
            {
                return true;
            }
        }

        return false;
    }

    private static void SetInstallerProperty(object installer, string propertyName, object value)
    {
        System.Reflection.PropertyInfo? property = installer.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        property.SetValue(installer, value);
    }

    private static object? GetPropertyValue(object instance, string propertyName)
    {
        System.Reflection.PropertyInfo? property = instance.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return property.GetValue(instance);
    }

    private static void AssertInstallerType(object installer, Type expectedType, bool exactMatch = true)
    {
        Type actualType = installer.GetType();
        if (exactMatch)
        {
            Assert.Equal(expectedType.FullName, actualType.FullName);
            return;
        }

        Assert.Contains(expectedType.FullName, GetTypeHierarchyNames(actualType), StringComparer.Ordinal);
    }

    private static bool HasInstallerType(object installer, Type expectedType)
        => installer.GetType().FullName == expectedType.FullName;

    private static object InvokeLoadedInstaller(object installer, Node targetRoot)
    {
        Type installerType = installer.GetType();
        if (HasInstallerType(installer, typeof(RigSceneInstaller)) && TryResolveSingleSkeleton(targetRoot, out Skeleton3D? skeleton))
        {
            Skeleton3D resolvedSkeleton = skeleton
                ?? throw new InvalidOperationException("Expected a resolved skeleton for loaded rig installer invocation.");
            Type characterContextType = installerType.Assembly.GetType(typeof(RigInstallationContext).FullName!)
                ?? throw new InvalidOperationException("Failed to resolve loaded RigInstallationContext type.");
            object characterContext = Activator.CreateInstance(
                characterContextType,
                targetRoot,
                SceneInstallationMetadata.DefaultNamespace,
                new Node { Name = "LoadedInstallerTemplateContext" },
                resolvedSkeleton)
                ?? throw new InvalidOperationException("Failed to create loaded character installation context.");
            System.Reflection.MethodInfo? characterInstallMethod = installerType.GetMethod(
                nameof(SceneInstaller.Install),
                [characterContextType]);
            Assert.NotNull(characterInstallMethod);
            object? characterResult = characterInstallMethod.Invoke(installer, [characterContext]);
            Assert.NotNull(characterResult);
            return characterResult;
        }

        Type contextType = installerType.Assembly.GetType(typeof(SceneInstallationContext).FullName!)
            ?? throw new InvalidOperationException("Failed to resolve loaded SceneInstallationContext type.");
        object context = Activator.CreateInstance(contextType, targetRoot, SceneInstallationMetadata.DefaultNamespace)
            ?? throw new InvalidOperationException("Failed to create loaded scene installation context.");
        System.Reflection.MethodInfo? installMethod = installerType.GetMethod(nameof(SceneInstaller.Install), [contextType]);
        Assert.NotNull(installMethod);
        object? result = installMethod.Invoke(installer, [context]);
        Assert.NotNull(result);
        return result;
    }

    private static bool TryResolveSingleSkeleton(Node root, out Skeleton3D? skeleton)
    {
        List<Skeleton3D> skeletons = [];
        CollectSkeletons(root, skeletons);
        skeleton = skeletons.Count == 1 ? skeletons[0] : null;
        return skeleton is not null;
    }

    private static void CollectSkeletons(Node node, List<Skeleton3D> skeletons)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Skeleton3D skeleton)
            {
                skeletons.Add(skeleton);
            }

            CollectSkeletons(child, skeletons);
        }
    }

    private static void InvokeLoadedRefresh(object installer)
    {
        Type installerType = installer.GetType();
        System.Reflection.MethodInfo? method = installerType.GetMethod(nameof(SceneInstaller.InstallNowInEditor));
        Assert.NotNull(method);
        try
        {
            _ = method.Invoke(installer, []);
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static void AssertLoadedInstallSucceeded(object result)
    {
        bool succeeded = (bool)(result.GetType().GetProperty(nameof(SceneInstallationResult.Succeeded))?.GetValue(result)
            ?? false);
        object? errors = result.GetType().GetProperty(nameof(SceneInstallationResult.Errors))?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors
            ? string.Join('\n', typedErrors)
            : errors?.ToString() ?? string.Empty;
        Assert.True(succeeded, errorText);
    }

    private static void AssertLoadedInstallFailedContaining(object result, string expectedError)
    {
        bool succeeded = (bool)(result.GetType().GetProperty(nameof(SceneInstallationResult.Succeeded))?.GetValue(result)
            ?? true);
        object? errors = result.GetType().GetProperty(nameof(SceneInstallationResult.Errors))?.GetValue(result);
        string errorText = errors is IEnumerable<string> typedErrors
            ? string.Join('\n', typedErrors)
            : errors?.ToString() ?? string.Empty;
        Assert.False(succeeded, errorText);
        Assert.Contains(expectedError, errorText, StringComparison.Ordinal);
    }

    private static void RemoveRequiredNode(Node root, string path)
    {
        Node node = root.GetNode(path);
        root.RemoveChild(node);
        node.Dispose();
    }

    private static IEnumerable<string?> GetTypeHierarchyNames(Type type)
    {
        for (Type? current = type; current is not null; current = current.BaseType)
        {
            yield return current.FullName;
        }
    }

    private static int CountDirectChildren(Node parent, string childName)
    {
        int count = 0;
        for (int index = 0; index < parent.GetChildCount(); index++)
        {
            if (parent.GetChild(index).Name == childName)
            {
                count++;
            }
        }

        return count;
    }

    private static RigInstallationContext CreateCharacterContext(CharacterFixture fixture, Node templateRoot)
    {
        Skeleton3D templateSkeleton;
        try
        {
            templateSkeleton = RigInstallationContext.ResolveSkeleton(templateRoot, null, "template");
        }
        catch (InvalidOperationException)
        {
            templateSkeleton = fixture.Skeleton;
        }

        return new(
            fixture.Root,
            SceneInstallationMetadata.DefaultNamespace,
            templateRoot,
            fixture.Skeleton,
            templateSkeleton);
    }

    private static CharacterFixture CreateAliceFixture()
    {
        var root = new CharacterBody3D
        {
            Name = "Alice",
        };
        var rigRoot = new Node3D
        {
            Name = "Rig",
        };
        var skeleton = new Skeleton3D
        {
            Name = "AliceSkeleton",
        };

        root.AddChild(rigRoot);
        rigRoot.AddChild(skeleton);
        rigRoot.Owner = root;
        skeleton.Owner = root;
        AddBone(skeleton, "Head", new Vector3(0.0f, 1.6f, 0.0f));
        AddBone(skeleton, "LeftHand", new Vector3(-0.45f, 1.1f, 0.0f));
        AddBone(skeleton, "RightHand", new Vector3(0.45f, 1.1f, 0.0f));

        return new CharacterFixture(root, skeleton);
    }

    private static void AddBone(Skeleton3D skeleton, string name, Vector3 restPosition)
    {
        int index = skeleton.GetBoneCount();
        _ = skeleton.AddBone(name);
        skeleton.SetBoneRest(index, new Transform3D(Basis.Identity, restPosition));
    }

    private sealed class CharacterFixture(CharacterBody3D root, Skeleton3D skeleton) : IDisposable
    {
        public CharacterBody3D Root => root;

        public Skeleton3D Skeleton => skeleton;

        public void Dispose() => Root.Dispose();
    }
}
