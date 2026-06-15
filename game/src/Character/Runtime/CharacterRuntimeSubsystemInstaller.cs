using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Character.Installer;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Runtime;

/// <summary>
/// Validates and activates shared humanoid runtime components copied from the role template.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterRuntimeSubsystemInstaller : CharacterSubsystemInstaller
{
    private const string EyeAnimationLibraryPath = "res://assets/characters/reference/female/animations/eyes/eyes.tres";

    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            CharacterTemplateInstallation.RebaseTemplateReferences(context.TargetRoot, context, this);

            AnimationTree animationTree = FindSingleDescendant<AnimationTree>(context.TargetRoot)
                ?? throw new InvalidOperationException("Character runtime subsystem installer requires an authored AnimationTree copied from the role template.");
            AnimationPlayer animationPlayer = FindSingleDescendant<AnimationPlayer>(context.TargetRoot)
                ?? throw new InvalidOperationException("Character runtime subsystem installer requires an authored AnimationPlayer copied from the role template.");

            RebaseAnimationMixerRoots(animationTree, animationPlayer, context.Skeleton);
            EnsureEyeAnimationLibrary(animationPlayer);
            ValidateEyes(context.TargetRoot);
            ValidateHands(context.TargetRoot);
            ValidateLocomotion(context.TargetRoot);

            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    private static void RebaseAnimationMixerRoots(AnimationTree animationTree, AnimationPlayer animationPlayer, Skeleton3D skeleton)
    {
        Node modelRoot = skeleton.GetParent()
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer could not resolve a model root parent for skeleton '{skeleton.GetPath()}'.");
        NodePath modelRootPath = animationTree.GetPathTo(modelRoot);
        animationTree.RootNode = modelRootPath;
        animationPlayer.RootNode = animationPlayer.GetPathTo(modelRoot);
    }

    private static void EnsureEyeAnimationLibrary(AnimationPlayer animationPlayer)
    {
        StringName libraryName = new("eyes");
        if (animationPlayer.HasAnimationLibrary(libraryName))
        {
            return;
        }

        AnimationLibrary eyeLibrary = ResourceLoader.Load<AnimationLibrary>(EyeAnimationLibraryPath)
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer could not load eye animation library '{EyeAnimationLibraryPath}'.");
        Error result = animationPlayer.AddAnimationLibrary(libraryName, eyeLibrary);
        if (result != Error.Ok)
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer could not add eye animation library '{EyeAnimationLibraryPath}' to '{animationPlayer.GetPath()}': {result}.");
        }
    }

    private static void ValidateEyes(Node targetRoot)
    {
        EyesBehaviour? eyes = FindSingleDescendant<EyesBehaviour>(targetRoot, required: false);
        if (eyes is null)
        {
            return;
        }

        RequireAssigned(eyes.AnimationTree, eyes, nameof(EyesBehaviour.AnimationTree));
        RequireAssigned(eyes.EyeOrigin, eyes, nameof(EyesBehaviour.EyeOrigin));
    }

    private static void ValidateHands(Node targetRoot)
    {
        foreach (HandPoseBehaviour hand in FindDescendants<HandPoseBehaviour>(targetRoot))
        {
            RequireAssigned(hand.AnimationTree, hand, nameof(HandPoseBehaviour.AnimationTree));
            RequireAssigned(hand.HandTargetNode, hand, nameof(HandPoseBehaviour.HandTargetNode));
            RequireAssigned(hand.HandBoneAttachment, hand, nameof(HandPoseBehaviour.HandBoneAttachment));
            RequireAssigned(hand.HeldCollisionTarget, hand, nameof(HandPoseBehaviour.HeldCollisionTarget));
            RequireAssigned(hand.PhysicalRig, hand, nameof(HandPoseBehaviour.PhysicalRig));
        }
    }

    private static void ValidateLocomotion(Node targetRoot)
    {
        CharacterLocomotion? locomotion = FindSingleDescendant<CharacterLocomotion>(targetRoot, required: false);
        if (locomotion is null)
        {
            return;
        }

        locomotion.TargetCharacterBodyNode ??= targetRoot as CharacterBody3D
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer expected target root '{targetRoot.GetPath()}' to be a CharacterBody3D for locomotion binding.");
        RequireAssigned(locomotion.AnimationTree, locomotion, nameof(CharacterLocomotion.AnimationTree));
        RequireAssigned(locomotion.RootMotionReference, locomotion, nameof(CharacterLocomotion.RootMotionReference));
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
                ? throw new InvalidOperationException(
                    $"Character runtime subsystem installer found multiple {typeof(T).Name} nodes under '{node.GetPath()}'.")
                : null;
        }

        return match;
    }

    private static IEnumerable<T> FindDescendants<T>(Node node)
        where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void RequireAssigned(Node? value, Node owner, string propertyName)
    {
        if (value is null)
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer requires template-authored '{propertyName}' on '{owner.GetPath()}'.");
        }
    }
}
