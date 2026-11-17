using AlleyCat.IK;
using AlleyCat.Interaction.Hands;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Interaction;

/// <summary>
/// Isolates the optical-grab fixtures to the INTR-002 grab rig: the reference female holds her imported rest pose
/// (the authored <c>Reset</c> animation) and only the production INTR-003 hand-pose layers, the arm IK chains and the
/// grab/hand-tracking inputs these facts exercise move the upper body. Idle, locomotion, MotionBricks, the head/neck
/// and leg/foot IK chains, hip reconciliation, the pose-state machine, voice and transcription are detached before
/// Ready.
/// </summary>
/// <remarks>
/// The upstream <c>States</c> pose machine is replaced by <c>Reset</c>; the <c>LeftHandBlend</c>,
/// <c>RightHandBlend</c>, <c>LeftHandPose</c> and <c>RightHandPose</c> nodes are duplicated from the production player
/// root so their finger filters remain the authoritative current values. The fixture owns tree evaluation
/// (process callback) because no idle or locomotion owner is part of a grab component fixture; the same pattern
/// as the coordinator fixture's hand-pose-only tree.
/// </remarks>
internal static class OpticalGrabRestPoseRig
{
    /// <summary>
    /// Rest-pose re-derivation of the authored interaction layout. The original wrist, item and contact positions
    /// were authored around the animated standing posture, which carries the shoulders forward. Measured in the
    /// optical ball-closure scenario at its converged pending frame, the right shoulder sat at
    /// (0.1521, 1.2590, -0.0860) under that posture (grab-fix-02) and at (0.1521, 1.2884, -0.0272) on this reduced
    /// rest-pose rig (grab-fix-04: arm IK with its shoulder correction only; hips stay at rest). Every world-space
    /// interaction position is translated by the difference, so shoulder-to-target and hand-to-item geometry is
    /// unchanged. Movement deltas and parking positions are relative or deliberately out of reach and are not
    /// translated.
    /// </summary>
    public static readonly Vector3 LayoutOffset = new(0.0f, 0.0294f, 0.0588f);

    /// <summary>
    /// Mock head pose at the reference female's rest-pose viewpoint: the rest Head bone composed with the authored
    /// <c>Head/Viewpoint</c> offset. With the head chain detached the solved head is the rest head, so the player's
    /// XR origin compensation (which treats a missing head solver as unreachable) resolves to an identity shift.
    /// </summary>
    public static Transform3D RestViewpoint(Node playerRoot)
    {
        Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        Node3D viewpoint = playerRoot.GetNode<Node3D>("Female/GeneralSkeleton/Head/Viewpoint");
        Transform3D head = skeleton.GlobalTransform * skeleton.GetBoneGlobalRest(skeleton.FindBone("Head"));
        return new Transform3D(Basis.Identity, (head * viewpoint.Transform).Origin);
    }

    private static readonly string[] _detachedSkeletonModifiers =
    [
        "NeckSpineIK", "HeadCopyRotation", "NeckTwistDisperser",
        "FootTargetSyncController",
        "RightLegIKController", "LeftLegIKController", "RightLegTwoBoneIKController", "LeftLegTwoBoneIKController",
        "CopyRightFootRotation", "CopyLeftFootRotation",
        "HipReconciliationModifier",
    ];

    private const string ProductionTreeRootPath = "res://assets/characters/templates/animation/animation_tree_root_player.tres";
    private const string UpstreamNode = "Reset";

    /// <summary>Prepares the uninstalled player instance before it enters the tree.</summary>
    public static void Prepare(Node playerRoot)
    {
        Assert.False(playerRoot.IsInsideTree(), "The rest-pose rig must be prepared before the player enters the tree.");

        // MotionBricks is locomotion; it has no role in grab behaviour and must not write a base pose.
        if (playerRoot.GetNodeOrNull("MotionBricks") is { } motion)
        {
            playerRoot.RemoveChild(motion);
            motion.Free();
        }

        AnimationTree tree = playerRoot.GetNode<AnimationTree>("AnimationTree");
        tree.TreeRoot = CreateRestPoseHandTree();
        tree.CallbackModeProcess = AnimationMixer.AnimationCallbackModeProcess.Idle;

        // Arm-only IK: clear every VRIK reference to the head/neck and leg/foot chains first (an empty group is
        // refilled by name only from attached children), then detach the chains, hip reconciliation and the
        // pose-state machine (PlayerVRIK supports a null state machine).
        PlayerVRIK vrik = playerRoot.GetNode<PlayerVRIK>("VRIK");
        vrik.HeadModifierGroup = [];
        vrik.RightFootModifierGroup = [];
        vrik.LeftFootModifierGroup = [];
        vrik.RightFootTargetIntentProvider = null;
        vrik.LeftFootTargetIntentProvider = null;
        vrik.RightFootFallbackIntentProvider = null;
        vrik.LeftFootFallbackIntentProvider = null;
        vrik.PoseStateMachine = null;
        Skeleton3D skeleton = playerRoot.GetNode<Skeleton3D>("Female/GeneralSkeleton");
        foreach (string name in _detachedSkeletonModifiers)
        {
            Detach(skeleton.GetNode(name));
        }

        Detach(vrik.GetNode("PoseStateMachine"));
        Detach(vrik.GetNode("RightFootFallbackIntentProvider"));
        Detach(vrik.GetNode("LeftFootFallbackIntentProvider"));

        // Speech is unrelated to grabbing.
        Assert.IsAssignableFrom<AlleyCat.Character.Character>(playerRoot).Voice = null;
        Detach(playerRoot.GetNode("Female/GeneralSkeleton/Head/Voice"));
        Detach(playerRoot.GetNode("OpenAITranscriber"));
    }

    private static void Detach(Node node)
    {
        node.GetParent().RemoveChild(node);
        node.Free();
    }

    private static AnimationNodeBlendTree CreateRestPoseHandTree()
    {
        AnimationNodeBlendTree production = ResourceLoader.Load<AnimationNodeBlendTree>(ProductionTreeRootPath)
            ?? throw new InvalidOperationException("The production player animation tree root must load.");
        AnimationNodeBlendTree root = new();
        root.AddNode(UpstreamNode, new AnimationNodeAnimation
        {
            Animation = new StringName(HandPoseAnimationTreePaths.ResetAnimationName),
        });
        foreach (string name in new[]
                 {
                     HandPoseAnimationTreePaths.LeftHandBlendNode,
                     HandPoseAnimationTreePaths.RightHandBlendNode,
                     HandPoseAnimationTreePaths.LeftHandPoseNode,
                     HandPoseAnimationTreePaths.RightHandPoseNode,
                 })
        {
            root.AddNode(name, (AnimationNode)production.GetNode(name).Duplicate(true));
        }

        root.ConnectNode(HandPoseAnimationTreePaths.LeftHandBlendNode, 0, UpstreamNode);
        root.ConnectNode(HandPoseAnimationTreePaths.LeftHandBlendNode, 1, HandPoseAnimationTreePaths.LeftHandPoseNode);
        root.ConnectNode(HandPoseAnimationTreePaths.RightHandBlendNode, 0, HandPoseAnimationTreePaths.LeftHandBlendNode);
        root.ConnectNode(HandPoseAnimationTreePaths.RightHandBlendNode, 1, HandPoseAnimationTreePaths.RightHandPoseNode);
        root.ConnectNode("output", 0, HandPoseAnimationTreePaths.RightHandBlendNode);
        return root;
    }
}
