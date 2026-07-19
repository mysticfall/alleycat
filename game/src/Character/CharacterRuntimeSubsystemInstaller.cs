using AlleyCat.Body.Eyes;
using AlleyCat.Body.Hands;
using AlleyCat.Control.Locomotion;
using AlleyCat.Core.Installer;
using AlleyCat.Rigging.Installation;
using Godot;

namespace AlleyCat.Character;

/// <summary>
/// Validates and activates shared humanoid runtime components copied from the role template.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterRuntimeSubsystemInstaller : RigSubsystemInstaller
{
    private static readonly StringName _eyesLibraryName = new("eyes");
    private static readonly StringName _authoredTreeRootResourcePathMeta = new("authored_tree_root_resource_path");
    private static readonly StringName[] _requiredEyeAnimationNames =
    [
        new(EyesAnimationTreePaths.BlinkAnimationName),
        new(EyesAnimationTreePaths.HorizontalLookAnimationName),
        new(EyesAnimationTreePaths.VerticalLookAnimationName),
    ];

    private static readonly IReadOnlySet<string> _horizontalLookBlendShapeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        EyesAnimationTreePaths.EyeLookInRightBlendShapeName,
        EyesAnimationTreePaths.EyeLookInLeftBlendShapeName,
        EyesAnimationTreePaths.EyeLookOutRightBlendShapeName,
        EyesAnimationTreePaths.EyeLookOutLeftBlendShapeName,
    };

    private static readonly IReadOnlySet<string> _verticalLookBlendShapeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        EyesAnimationTreePaths.EyeLookUpRightBlendShapeName,
        EyesAnimationTreePaths.EyeLookUpLeftBlendShapeName,
        EyesAnimationTreePaths.EyeLookDownRightBlendShapeName,
        EyesAnimationTreePaths.EyeLookDownLeftBlendShapeName,
    };

    /// <inheritdoc />
    public override SceneInstallationResult Install(RigInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            Character targetCharacter = ValidateCharacterHub(context.TargetRoot);
            Character templateCharacter = ValidateTemplateCharacterHub(context.TemplateRoot);

            TemplateSceneReferenceRebaser.CopyExportedPropertyValues(
                templateCharacter,
                targetCharacter,
                context.TemplateRoot,
                context.TargetRoot,
                this,
                failOnUnresolved: true,
                targetSceneOverrides: context.TargetSceneOverrides);

            RigTemplateInstallation.RebaseTemplateReferences(context.TargetRoot, context, this);

            AnimationTree animationTree = FindSingleDescendant<AnimationTree>(context.TargetRoot)
                ?? throw new InvalidOperationException("Character runtime subsystem installer requires an authored AnimationTree copied from the role template.");
            AnimationPlayer animationPlayer = FindSingleDescendant<AnimationPlayer>(context.TargetRoot)
                ?? throw new InvalidOperationException("Character runtime subsystem installer requires an authored AnimationPlayer copied from the role template.");

            RebaseAnimationMixerRoots(animationTree, animationPlayer, context.Skeleton);
            EyeAnimationTreeFilterTargets eyeFilterTargets = ValidateEyeAnimationLibrary(animationPlayer);
            ConfigureEyeAnimationTreeFilters(animationTree, eyeFilterTargets);
            ValidateEyes(targetCharacter.Eyes);
            ValidateHands(targetCharacter.LeftHand, targetCharacter.RightHand);
            ValidateLocomotion(targetCharacter.Locomotion, context.TargetRoot);
            targetCharacter.RefreshComponents();

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

    private static EyeAnimationTreeFilterTargets ValidateEyeAnimationLibrary(AnimationPlayer animationPlayer)
    {
        if (!animationPlayer.HasAnimationLibrary(_eyesLibraryName))
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer requires imported AnimationLibrary '{_eyesLibraryName}' on '{animationPlayer.GetPath()}'. Reimport the character source with the eye animation import script configured.");
        }

        IReadOnlyList<NodePath>? blinkFilterPaths = null;
        IReadOnlyList<NodePath>? horizontalFilterPaths = null;
        IReadOnlyList<NodePath>? verticalFilterPaths = null;

        foreach (StringName animationName in _requiredEyeAnimationNames)
        {
            if (!animationPlayer.HasAnimation(animationName))
            {
                throw new InvalidOperationException(
                    $"Character runtime subsystem installer requires imported eye animation '{animationName}' on '{animationPlayer.GetPath()}'. Reimport the character source with the eye animation import script configured.");
            }

            Animation animation = animationPlayer.GetAnimation(animationName);
            EyeAnimationTrackTargets trackTargets = ValidateBlendShapeTrackTargets(animationPlayer, animationName, animation);
            HashSet<string> blendShapeNames = trackTargets.BlendShapeNames;
            ValidateRequiredEyeAnimationTracks(animationName, blendShapeNames);

            string animationNameString = animationName.ToString();
            switch (animationNameString)
            {
                case EyesAnimationTreePaths.BlinkAnimationName:
                    blinkFilterPaths = trackTargets.FilterPaths;
                    break;
                case EyesAnimationTreePaths.HorizontalLookAnimationName:
                    horizontalFilterPaths = trackTargets.FilterPaths;
                    break;
                case EyesAnimationTreePaths.VerticalLookAnimationName:
                    verticalFilterPaths = trackTargets.FilterPaths;
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Character runtime subsystem installer does not recognise required eye animation '{animationName}'.");
            }
        }

        return new EyeAnimationTreeFilterTargets(
            blinkFilterPaths ?? throw new InvalidOperationException("Character runtime subsystem installer could not resolve blink eye filter targets."),
            horizontalFilterPaths ?? throw new InvalidOperationException("Character runtime subsystem installer could not resolve horizontal eye filter targets."),
            verticalFilterPaths ?? throw new InvalidOperationException("Character runtime subsystem installer could not resolve vertical eye filter targets."));
    }

    private static EyeAnimationTrackTargets ValidateBlendShapeTrackTargets(AnimationPlayer animationPlayer, StringName animationName, Animation animation)
    {
        Node rootNode = animationPlayer.GetNodeOrNull(animationPlayer.RootNode)
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer could not resolve AnimationPlayer root '{animationPlayer.RootNode}' for '{animationPlayer.GetPath()}'.");

        HashSet<string> blendShapeNames = new(StringComparer.Ordinal);
        var filterPaths = new List<NodePath>(animation.GetTrackCount());

        for (int trackIndex = 0; trackIndex < animation.GetTrackCount(); trackIndex++)
        {
            if (animation.TrackGetType(trackIndex) != Animation.TrackType.BlendShape)
            {
                throw new InvalidOperationException(
                    $"Character runtime subsystem installer requires imported eye animation '{animationName}' to contain only blend-shape tracks, but track {trackIndex} is '{animation.TrackGetType(trackIndex)}'.");
            }

            string trackPath = animation.TrackGetPath(trackIndex).ToString();
            int subnameSeparator = trackPath.IndexOf(':', StringComparison.Ordinal);
            if (subnameSeparator <= 0 || subnameSeparator == trackPath.Length - 1)
            {
                throw new InvalidOperationException(
                    $"Character runtime subsystem installer requires blend-shape track '{animationName}'[{trackIndex}] to target '<mesh>:<blend_shape>', but found '{trackPath}'.");
            }

            string meshPath = trackPath[..subnameSeparator];
            string blendShapeName = trackPath[(subnameSeparator + 1)..];
            MeshInstance3D meshInstance = rootNode.GetNodeOrNull<MeshInstance3D>(new NodePath(meshPath))
                ?? throw new InvalidOperationException(
                    $"Character runtime subsystem installer could not resolve eye animation track '{animationName}'[{trackIndex}] mesh '{meshPath}' from root '{rootNode.GetPath()}'.");

            if (!MeshHasBlendShape(meshInstance, blendShapeName))
            {
                throw new InvalidOperationException(
                    $"Character runtime subsystem installer could not resolve eye animation track '{animationName}'[{trackIndex}] blend shape '{blendShapeName}' on mesh '{meshInstance.GetPath()}'.");
            }

            _ = blendShapeNames.Add(blendShapeName);
            filterPaths.Add(animation.TrackGetPath(trackIndex));
        }

        return blendShapeNames.Count == 0
            ? throw new InvalidOperationException(
                $"Character runtime subsystem installer requires imported eye animation '{animationName}' to contain at least one blend-shape track.")
            : new EyeAnimationTrackTargets(blendShapeNames, filterPaths);
    }

    private static void ConfigureEyeAnimationTreeFilters(AnimationTree animationTree, EyeAnimationTreeFilterTargets filterTargets)
    {
        AnimationNodeBlendTree treeRoot = animationTree.TreeRoot as AnimationNodeBlendTree
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer requires '{animationTree.GetPath()}' to use an AnimationNodeBlendTree root for eye filter setup.");
        string authoredTreeRootResourcePath = treeRoot.ResourcePath;
        if (!string.IsNullOrEmpty(authoredTreeRootResourcePath))
        {
            animationTree.SetMeta(_authoredTreeRootResourcePathMeta, authoredTreeRootResourcePath);
        }

        AnimationNodeBlendTree instanceTreeRoot = RequiresPerCharacterEyeFilterInstance(treeRoot, filterTargets)
            ? treeRoot.Duplicate(true) as AnimationNodeBlendTree
                ?? throw new InvalidOperationException(
                    $"Character runtime subsystem installer could not duplicate AnimationTree root for per-character eye filter setup on '{animationTree.GetPath()}'.")
            : treeRoot;

        if (!ReferenceEquals(instanceTreeRoot, treeRoot))
        {
            animationTree.TreeRoot = instanceTreeRoot;
        }

        ConfigureFilteredNode<AnimationNodeBlend2>(
            instanceTreeRoot,
            EyesAnimationTreePaths.HorizontalLookBlendNode,
            filterTargets.HorizontalLookFilterPaths);
        ConfigureFilteredNode<AnimationNodeBlend2>(
            instanceTreeRoot,
            EyesAnimationTreePaths.VerticalLookBlendNode,
            filterTargets.VerticalLookFilterPaths);
        ConfigureFilteredNode<AnimationNodeOneShot>(
            instanceTreeRoot,
            EyesAnimationTreePaths.BlinkOneShotNode,
            filterTargets.BlinkFilterPaths);
    }

    private static bool RequiresPerCharacterEyeFilterInstance(
        AnimationNodeBlendTree treeRoot,
        EyeAnimationTreeFilterTargets filterTargets)
        => !HasRequiredFilters<AnimationNodeBlend2>(
                treeRoot,
                EyesAnimationTreePaths.HorizontalLookBlendNode,
                filterTargets.HorizontalLookFilterPaths)
            || !HasRequiredFilters<AnimationNodeBlend2>(
                treeRoot,
                EyesAnimationTreePaths.VerticalLookBlendNode,
                filterTargets.VerticalLookFilterPaths)
            || !HasRequiredFilters<AnimationNodeOneShot>(
                treeRoot,
                EyesAnimationTreePaths.BlinkOneShotNode,
                filterTargets.BlinkFilterPaths);

    private static bool HasRequiredFilters<T>(AnimationNodeBlendTree treeRoot, string nodeName, IReadOnlyList<NodePath> expectedFilterPaths)
        where T : AnimationNode
    {
        T node = treeRoot.GetNode(nodeName) as T
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer requires AnimationTree eye node '{nodeName}' to be {typeof(T).Name}.");

        if (!node.FilterEnabled)
        {
            return false;
        }

        for (int index = 0; index < expectedFilterPaths.Count; index++)
        {
            if (!node.IsPathFiltered(expectedFilterPaths[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static void ConfigureFilteredNode<T>(AnimationNodeBlendTree treeRoot, string nodeName, IReadOnlyList<NodePath> filterPaths)
        where T : AnimationNode
    {
        T node = treeRoot.GetNode(nodeName) as T
            ?? throw new InvalidOperationException(
                $"Character runtime subsystem installer requires AnimationTree eye node '{nodeName}' to be {typeof(T).Name}.");

        ClearExistingFilters(node);
        node.FilterEnabled = true;

        for (int index = 0; index < filterPaths.Count; index++)
        {
            node.SetFilterPath(filterPaths[index], true);
        }

        ValidateConfiguredFilters(nodeName, node, filterPaths);
    }

    private static void ClearExistingFilters(AnimationNode node)
    {
        Godot.Collections.Array filters = node.Get("filters").AsGodotArray();
        var existingFilters = new NodePath[filters.Count];
        for (int index = 0; index < filters.Count; index++)
        {
            existingFilters[index] = filters[index].AsNodePath();
        }

        for (int index = 0; index < existingFilters.Length; index++)
        {
            node.SetFilterPath(existingFilters[index], false);
        }
    }

    private static void ValidateConfiguredFilters(string nodeName, AnimationNode node, IReadOnlyList<NodePath> expectedFilterPaths)
    {
        if (!node.FilterEnabled)
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer requires AnimationTree eye node '{nodeName}' to have filtering enabled.");
        }

        for (int index = 0; index < expectedFilterPaths.Count; index++)
        {
            if (!node.IsPathFiltered(expectedFilterPaths[index]))
            {
                throw new InvalidOperationException(
                    $"Character runtime subsystem installer failed to configure AnimationTree eye node '{nodeName}' filter '{expectedFilterPaths[index]}'.");
            }
        }
    }

    private static void ValidateRequiredEyeAnimationTracks(StringName animationName, IReadOnlySet<string> blendShapeNames)
    {
        string animationNameString = animationName.ToString();
        switch (animationNameString)
        {
            case EyesAnimationTreePaths.BlinkAnimationName:
                RequireBlendShape(animationName, blendShapeNames, EyesAnimationTreePaths.EyeBlinkLeftBlendShapeName);
                RequireBlendShape(animationName, blendShapeNames, EyesAnimationTreePaths.EyeBlinkRightBlendShapeName);
                break;
            case EyesAnimationTreePaths.HorizontalLookAnimationName:
                RequireAnyBlendShape(animationName, blendShapeNames, _horizontalLookBlendShapeNames, "horizontal look");
                break;
            case EyesAnimationTreePaths.VerticalLookAnimationName:
                RequireAnyBlendShape(animationName, blendShapeNames, _verticalLookBlendShapeNames, "vertical look");
                break;
            default:
                break;
        }
    }

    private static void RequireBlendShape(StringName animationName, IReadOnlySet<string> blendShapeNames, string requiredBlendShapeName)
    {
        if (!blendShapeNames.Contains(requiredBlendShapeName))
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer requires imported eye animation '{animationName}' to include blend-shape track target '{requiredBlendShapeName}'.");
        }
    }

    private static void RequireAnyBlendShape(
        StringName animationName,
        IReadOnlySet<string> blendShapeNames,
        IReadOnlySet<string> allowedBlendShapeNames,
        string description)
    {
        // The runtime only needs at least one axis-specific look target to keep partial one-eye rigs valid,
        // but rejecting unrelated eye tracks catches importer/library mismatches before the AnimationTree silently stalls.
        if (!blendShapeNames.Any(allowedBlendShapeNames.Contains))
        {
            throw new InvalidOperationException(
                $"Character runtime subsystem installer requires imported eye animation '{animationName}' to include at least one {description} blend-shape track target.");
        }
    }

    private static bool MeshHasBlendShape(MeshInstance3D meshInstance, string blendShapeName)
    {
        if (meshInstance.Mesh is not ArrayMesh mesh)
        {
            return false;
        }

        for (int blendShapeIndex = 0; blendShapeIndex < mesh.GetBlendShapeCount(); blendShapeIndex++)
        {
            if (string.Equals(mesh.GetBlendShapeName(blendShapeIndex).ToString(), blendShapeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateEyes(EyesBehaviour? eyes)
    {
        if (eyes is null)
        {
            return;
        }

        RequireAssigned(eyes.AnimationTree, eyes, nameof(EyesBehaviour.AnimationTree));
        RequireAssigned(eyes.EyeOrigin, eyes, nameof(EyesBehaviour.EyeOrigin));
    }

    private static void ValidateHands(params HandPoseBehaviour?[] hands)
    {
        foreach (HandPoseBehaviour? hand in hands)
        {
            if (hand is null)
            {
                continue;
            }

            RequireAssigned(hand.AnimationTree, hand, nameof(HandPoseBehaviour.AnimationTree));
            RequireAssigned(hand.HandTargetNode, hand, nameof(HandPoseBehaviour.HandTargetNode));
            RequireAssigned(hand.HandBoneAttachment, hand, nameof(HandPoseBehaviour.HandBoneAttachment));
            RequireAssigned(hand.HeldCollisionTarget, hand, nameof(HandPoseBehaviour.HeldCollisionTarget));
            RequireAssigned(hand.PhysicalRig, hand, nameof(HandPoseBehaviour.PhysicalRig));
        }
    }

    private static void ValidateLocomotion(CharacterLocomotion? locomotion, Node targetRoot)
    {
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

    internal static Character ValidateCharacterHub(Node targetRoot)
    {
        return targetRoot is not Character character
            ? throw new InvalidOperationException(
                $"Character runtime subsystem installer requires target root '{targetRoot.GetPath()}' to be an {typeof(Character).FullName} CharacterBody3D root with the Character.cs script assigned.")
            : character;
    }

    internal static Character ValidateTemplateCharacterHub(Node templateRoot)
        => templateRoot is Character character
            ? character
            : throw new InvalidOperationException(
                $"Character runtime subsystem installer requires template root '{templateRoot.GetPath()}' to be an {typeof(Character).FullName} CharacterBody3D root with the Character.cs script assigned.");

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

    private sealed record EyeAnimationTrackTargets(
        HashSet<string> BlendShapeNames,
        IReadOnlyList<NodePath> FilterPaths);

    private sealed record EyeAnimationTreeFilterTargets(
        IReadOnlyList<NodePath> BlinkFilterPaths,
        IReadOnlyList<NodePath> HorizontalLookFilterPaths,
        IReadOnlyList<NodePath> VerticalLookFilterPaths);
}
