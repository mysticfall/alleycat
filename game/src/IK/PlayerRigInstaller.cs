using AlleyCat.Character.Installer;
using AlleyCat.Control;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.IK.Pose;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Validates and activates the player-specific VRIK rig copied from the role template.
/// </summary>
[Tool]
[GlobalClass]
public partial class PlayerRigInstaller : CharacterIKSubsystemInstaller
{
    /// <inheritdoc />
    protected override CharacterIK ResolveIKNode(Node targetRoot)
        => !string.IsNullOrWhiteSpace(IKNodeName)
            ? RequireTargetNode<PlayerVRIK>(targetRoot, IKNodeName)
            : FindSingleDescendant<PlayerVRIK>(targetRoot)
            ?? throw new InvalidOperationException(
                $"Player rig installer could not resolve a template-authored {nameof(PlayerVRIK)} under '{targetRoot.GetPath()}'.");

    /// <inheritdoc />
    protected override void ValidateIK(CharacterIK ik)
    {
        base.ValidateIK(ik);

        if (ik is not PlayerVRIK playerVRIK)
        {
            throw new InvalidOperationException($"Player rig installer expected '{ik.GetPath()}' to be a PlayerVRIK node.");
        }

        EnsurePlayerProviderReferences(playerVRIK);
        PoseStateMachine poseStateMachine = playerVRIK.PoseStateMachine
            ?? throw new InvalidOperationException($"Player rig installer requires template-authored '{nameof(PlayerVRIK.PoseStateMachine)}' on '{playerVRIK.GetPath()}'.");
        poseStateMachine.AnimationTree = FindSingleDescendant<AnimationTree>(playerVRIK.GetParent() ?? playerVRIK, required: false)
            ?? poseStateMachine.AnimationTree;
        RequireAssigned(poseStateMachine.AnimationTree, poseStateMachine, nameof(PoseStateMachine.AnimationTree));
        poseStateMachine.RestartCurrentAnimationState();
        _ = poseStateMachine.CallDeferred(PoseStateMachine.MethodName.RestartCurrentAnimationState);

        RequireAssigned(playerVRIK.HeadFallbackIntentProvider, playerVRIK, nameof(PlayerVRIK.HeadFallbackIntentProvider));
        RequireAssigned(playerVRIK.RightHandFallbackIntentProvider, playerVRIK, nameof(PlayerVRIK.RightHandFallbackIntentProvider));
        RequireAssigned(playerVRIK.LeftHandFallbackIntentProvider, playerVRIK, nameof(PlayerVRIK.LeftHandFallbackIntentProvider));
        RequireAssigned(playerVRIK.RightFootFallbackIntentProvider, playerVRIK, nameof(PlayerVRIK.RightFootFallbackIntentProvider));
        RequireAssigned(playerVRIK.LeftFootFallbackIntentProvider, playerVRIK, nameof(PlayerVRIK.LeftFootFallbackIntentProvider));
    }

    private static void EnsurePlayerProviderReferences(PlayerVRIK playerVRIK)
    {
        if (!IsAssigned(playerVRIK.PoseStateMachine))
        {
            playerVRIK.PoseStateMachine = FindDirectChild<PoseStateMachine>(playerVRIK);
        }

        playerVRIK.HeadFallbackIntentProvider = EnsureProvider(
            playerVRIK,
            playerVRIK.HeadFallbackIntentProvider,
            "HeadFallbackIntentProvider");
        playerVRIK.RightHandFallbackIntentProvider = EnsureProvider(
            playerVRIK,
            playerVRIK.RightHandFallbackIntentProvider,
            "RightHandFallbackIntentProvider");
        playerVRIK.LeftHandFallbackIntentProvider = EnsureProvider(
            playerVRIK,
            playerVRIK.LeftHandFallbackIntentProvider,
            "LeftHandFallbackIntentProvider");
        playerVRIK.RightFootFallbackIntentProvider = EnsureProvider(
            playerVRIK,
            playerVRIK.RightFootFallbackIntentProvider,
            "RightFootFallbackIntentProvider");
        playerVRIK.LeftFootFallbackIntentProvider = EnsureProvider(
            playerVRIK,
            playerVRIK.LeftFootFallbackIntentProvider,
            "LeftFootFallbackIntentProvider");

        if (playerVRIK.RightHandIKTargetIntentProvider is HandGrabTargetProvider rightGrabProvider)
        {
            rightGrabProvider.DefaultProvider = playerVRIK.RightHandFallbackIntentProvider;
        }

        if (playerVRIK.LeftHandIKTargetIntentProvider is HandGrabTargetProvider leftGrabProvider)
        {
            leftGrabProvider.DefaultProvider = playerVRIK.LeftHandFallbackIntentProvider;
        }

        if (playerVRIK.HeadFallbackIntentProvider is XRHeadTargetIntentProvider headProvider)
        {
            headProvider.Viewpoint = playerVRIK.Viewpoint;
        }

        if (playerVRIK.RightFootFallbackIntentProvider is AnimationSynchronizedFootTargetProvider rightFootProvider)
        {
            rightFootProvider.FootTarget = playerVRIK.RightFootIKTarget;
        }

        if (playerVRIK.LeftFootFallbackIntentProvider is AnimationSynchronizedFootTargetProvider leftFootProvider)
        {
            leftFootProvider.FootTarget = playerVRIK.LeftFootIKTarget;
        }
    }

    private static IKTargetIntentProvider? EnsureProvider(PlayerVRIK playerVRIK, IKTargetIntentProvider? provider, string providerName)
        => IsAssigned(provider) ? provider : playerVRIK.GetNodeOrNull<IKTargetIntentProvider>(providerName);

    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        SceneInstallationResult result = base.Install(context);
        if (!result.Succeeded)
        {
            return result;
        }

        try
        {
            OrderHipReconciliationModifier(context.Skeleton);
            PoseStateMachine poseStateMachine = ResolveIKNode(context.TargetRoot) is PlayerVRIK playerVRIK && playerVRIK.PoseStateMachine is not null
                ? playerVRIK.PoseStateMachine
                : throw new InvalidOperationException("Player rig installer could not resolve template-authored pose state machine after VRIK validation.");

            LocomotionBase locomotion = FindSingleDescendant<LocomotionBase>(context.TargetRoot)
                ?? throw new InvalidOperationException("Player rig installer requires template-authored locomotion under the target root.");
            if (locomotion is CharacterLocomotion characterLocomotion)
            {
                characterLocomotion.IdleAnimationStateName = StandingPoseState.DefaultAnimationStateName;
            }

            if (FindSingleDescendant<AnimationTree>(context.TargetRoot, required: false)?.Get("parameters/States/playback").As<AnimationNodeStateMachinePlayback>()
                is AnimationNodeStateMachinePlayback playback)
            {
                playback.Start(StandingPoseState.DefaultAnimationStateName, reset: true);
            }

            locomotion.PermissionSourceNodes = [poseStateMachine];
            if (FindSingleDescendant<PlayerController>(context.TargetRoot, required: false) is PlayerController playerController)
            {
                playerController.LocomotionNode ??= locomotion;
            }

            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    private static void OrderHipReconciliationModifier(Skeleton3D skeleton)
    {
        Node hipModifier = FindSingleDirectChild<HipReconciliationModifier>(skeleton)
            ?? throw new InvalidOperationException("Player rig installer requires exactly one template-authored hip reconciliation modifier under the skeleton.");
        Node footSync = FindSingleDirectChild<FootTargetSyncController>(skeleton)
            ?? throw new InvalidOperationException("Player rig installer requires exactly one template-authored foot target sync controller under the skeleton.");
        int desiredIndex = footSync.GetIndex() + 1;
        if (hipModifier.GetIndex() != desiredIndex)
        {
            skeleton.MoveChild(hipModifier, desiredIndex);
        }
    }

    private static T? FindSingleDescendant<T>(Node node, bool required = true)
        where T : Node
    {
        T? match = null;
        foreach (T candidate in FindDescendants<T>(node))
        {
            if (match is null)
            {
                match = candidate;
                continue;
            }

            return required
                ? throw new InvalidOperationException($"Player rig installer found multiple {typeof(T).Name} nodes under '{node.GetPath()}'.")
                : null;
        }

        return match;
    }

    private static T? FindSingleDirectChild<T>(Node node)
        where T : Node
    {
        T? match = null;
        foreach (Node child in node.GetChildren())
        {
            if (child is not T candidate)
            {
                continue;
            }

            if (match is not null)
            {
                return null;
            }

            match = candidate;
        }

        return match;
    }
}
