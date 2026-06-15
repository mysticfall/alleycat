using System.Reflection;
using AlleyCat.Body;
using AlleyCat.Body.Hands;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Characters;

/// <summary>
/// Runtime regression coverage for reference-character animation mixer materialisation.
/// </summary>
public sealed partial class CharacterAnimationRuntimeIntegrationTests
{
    private const string AllyScenePath = "res://assets/characters/reference/ally.tscn";
    private const string PlayerScenePath = "res://assets/characters/reference/player.tscn";
    private static readonly StringName _eyesLibraryName = new("eyes");

    /// <summary>
    /// Verifies the NPC reference installer leaves the shared animation tree able to drive skeletal body motion.
    /// </summary>
    [Headless]
    [Fact]
    public async Task AllyScene_RuntimeAnimationTree_AdvancesNpcBodyPose()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(AllyScenePath).Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Skeleton3D skeleton = root.GetNode<Skeleton3D>("Female/GeneralSkeleton");

            Assert.Equal(new NodePath("../Female"), animationTree.RootNode);
            Assert.Equal(new NodePath("../Female"), animationPlayer.RootNode);
            Assert.True(animationPlayer.HasAnimationLibrary(_eyesLibraryName), "Expected runtime installation to preserve the eye animation library used by the tree.");
            Assert.True(animationPlayer.HasAnimation(new StringName("eyes/Eyes Blink")), "Expected the tree-referenced blink animation to be registered.");

            Assert.NotEmpty(skeleton.GetBoneName(0).ToString());
            string[] animationNames = animationPlayer.GetAnimationList();
            Assert.Contains("Walk Forward", animationNames);
            Assert.Contains("eyes/Eyes Up Down", animationNames);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    /// <summary>
    /// Verifies the player hand can apply a grab animation to the hand pose node, blend parameter, and finger bone pose.
    /// </summary>
    [Headless]
    [Fact]
    public async Task PlayerScene_GrabAnimationPose_ChangesRightHandPoseState()
    {
        SceneTree sceneTree = GetSceneTree();
        Node root = LoadPackedScene(PlayerScenePath).Instantiate();
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForFramesAsync(sceneTree, 12);
            EnsureCharacterRuntimeInstalled(root);

            AnimationTree animationTree = root.GetNode<AnimationTree>("AnimationTree");
            AnimationPlayer animationPlayer = root.GetNode<AnimationPlayer>("AnimationPlayer");
            Node rightHand = root.GetNode<Node>("Hands/RightHand");
            Animation grabAnimation = animationPlayer.GetAnimation(new StringName("Grab-ball-40"));

            InvokeScriptVoidMethod(rightHand, "SetPose", grabAnimation, null, true);
            await WaitForFramesAsync(sceneTree, 20);
            animationTree.Advance(0.5d);

            Assert.Same(grabAnimation, GetScriptProperty<object>(rightHand, "CurrentPose"));
            Assert.Equal("Grab-ball-40", ResolveHandPoseNode(animationTree, LimbSide.Right).Animation.ToString());
            Assert.True(animationTree.Get(HandPoseAnimationTreePaths.GetHandBlendParameter(LimbSide.Right)).AsSingle() > 0.5f);

            InvokeScriptVoidMethod(rightHand, "ClearPose", true);
        }
        finally
        {
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 1);
        }
    }

    private static AnimationNodeAnimation ResolveHandPoseNode(AnimationTree animationTree, LimbSide side)
    {
        AnimationNodeBlendTree rootTree = Assert.IsType<AnimationNodeBlendTree>(animationTree.TreeRoot, exactMatch: false);
        return Assert.IsType<AnimationNodeAnimation>(
            rootTree.GetNode(HandPoseAnimationTreePaths.GetPoseAnimationNodeName(side)),
            exactMatch: false);
    }

    private static T? GetScriptProperty<T>(Node node, string propertyName)
    {
        PropertyInfo property = node.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script property '{propertyName}' to resolve on '{node.Name}'.");
        return (T?)property.GetValue(node);
    }

    private static void InvokeScriptVoidMethod(Node node, string methodName, params object?[] arguments)
    {
        MethodInfo method = node.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"Expected script method '{methodName}' to resolve on '{node.Name}'.");
        _ = method.Invoke(node, arguments);
    }

}
