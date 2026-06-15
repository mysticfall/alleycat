using AlleyCat.Body.Hands;
using AlleyCat.Character.Installer;
using AlleyCat.Core.Installer;
using Godot;
using BodyLimbSide = AlleyCat.Body.LimbSide;

namespace AlleyCat.IK;

/// <summary>
/// Validates and activates a template-authored humanoid IK node after template references are rebased.
/// </summary>
[Tool]
[GlobalClass]
public partial class CharacterIKSubsystemInstaller : CharacterSubsystemInstaller
{
    /// <summary>
    /// Gets or sets the conventional IK node name under the character root.
    /// </summary>
    [Export]
    public string IKNodeName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether this installer should validate and activate a role-specific CharacterIK node.
    /// </summary>
    [Export]
    public bool BindCharacterIKNode { get; set; } = true;

    /// <summary>
    /// Gets or sets whether hand behaviours should consume the staged grab providers assigned to the IK node.
    /// </summary>
    [Export]
    public bool BindHandGrabProviders { get; set; } = true;

    /// <inheritdoc />
    public override SceneInstallationResult Install(CharacterInstallationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            CharacterTemplateInstallation.RebaseTemplateReferences(context.TargetRoot, context, this);
            if (BindCharacterIKNode)
            {
                CharacterIK ik = ResolveIKNode(context.TargetRoot);
                ValidateIK(ik);
                ActivateBoundModifierPipeline(ik, context.Skeleton);
                ik.ResetRuntimeBindings();

                if (BindHandGrabProviders && HasInstalledHands(context.TargetRoot))
                {
                    BindHandsToGrabProviders(context.TargetRoot, ik);
                }
            }

            return SceneInstallationResult.Successful();
        }
        catch (InvalidOperationException ex)
        {
            return SceneInstallationResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Resolves the IK node targeted by this installer.
    /// </summary>
    protected virtual CharacterIK ResolveIKNode(Node targetRoot)
    {
        if (!string.IsNullOrWhiteSpace(IKNodeName))
        {
            return RequireTargetNode<CharacterIK>(targetRoot, IKNodeName);
        }

        CharacterIK? resolved = FindSingleDescendant<CharacterIK>(targetRoot);
        return resolved
            ?? throw new InvalidOperationException(
                $"Character IK subsystem installer could not resolve a template-authored {nameof(CharacterIK)} under '{targetRoot.GetPath()}'.");
    }

    /// <summary>
    /// Validates required CharacterIK bindings authored by the role template.
    /// </summary>
    protected virtual void ValidateIK(CharacterIK ik)
    {
        EnsureHandGrabProviders(ik);
        RequireAssigned(ik.Viewpoint, ik, nameof(CharacterIK.Viewpoint));
        RequireAssigned(ik.HeadIKTarget, ik, nameof(CharacterIK.HeadIKTarget));
        RequireAssigned(ik.HeadIKSolveTarget, ik, nameof(CharacterIK.HeadIKSolveTarget));
        RequireAssigned(ik.RightHandIKTarget, ik, nameof(CharacterIK.RightHandIKTarget));
        RequireAssigned(ik.LeftHandIKTarget, ik, nameof(CharacterIK.LeftHandIKTarget));
        RequireAssigned(ik.RightFootIKTarget, ik, nameof(CharacterIK.RightFootIKTarget));
        RequireAssigned(ik.LeftFootIKTarget, ik, nameof(CharacterIK.LeftFootIKTarget));
        RequireAssigned(ik.PhysicalRig, ik, nameof(CharacterIK.PhysicalRig));
        RequireAssigned(ik.RightHandIKTargetIntentProvider, ik, nameof(CharacterIK.RightHandIKTargetIntentProvider));
        RequireAssigned(ik.LeftHandIKTargetIntentProvider, ik, nameof(CharacterIK.LeftHandIKTargetIntentProvider));
        RequireGroup(ik.HeadModifierGroup, ik, nameof(CharacterIK.HeadModifierGroup));
        RequireGroup(ik.RightHandModifierGroup, ik, nameof(CharacterIK.RightHandModifierGroup));
        RequireGroup(ik.LeftHandModifierGroup, ik, nameof(CharacterIK.LeftHandModifierGroup));
        RequireGroup(ik.RightFootModifierGroup, ik, nameof(CharacterIK.RightFootModifierGroup));
        RequireGroup(ik.LeftFootModifierGroup, ik, nameof(CharacterIK.LeftFootModifierGroup));
    }

    private static void EnsureHandGrabProviders(CharacterIK ik)
    {
        if (!IsAssigned(ik.RightHandIKTargetIntentProvider))
        {
            ik.RightHandIKTargetIntentProvider = ik.GetNodeOrNull<IKTargetIntentProvider>("RightHandGrabProvider");
        }

        if (!IsAssigned(ik.LeftHandIKTargetIntentProvider))
        {
            ik.LeftHandIKTargetIntentProvider = ik.GetNodeOrNull<IKTargetIntentProvider>("LeftHandGrabProvider");
        }
    }

    /// <summary>
    /// Reactivates the skeleton modifier pipeline after all template-owned node references have been rebound.
    /// </summary>
    protected virtual void ActivateBoundModifierPipeline(CharacterIK ik, Skeleton3D skeleton)
    {
        foreach (SkeletonModifier3D modifier in FindDescendants<SkeletonModifier3D>(skeleton))
        {
            ActivateModifier(modifier);
        }

        ActivateModifierGroup(ik.HeadModifierGroup);
        ActivateModifierGroup(ik.RightHandModifierGroup);
        ActivateModifierGroup(ik.LeftHandModifierGroup);
        ActivateModifierGroup(ik.RightFootModifierGroup);
        ActivateModifierGroup(ik.LeftFootModifierGroup);
    }

    /// <summary>
    /// Restores a bound modifier to full influence after template installation's safe inactive default.
    /// </summary>
    protected static void ActivateModifier(SkeletonModifier3D modifier)
    {
        modifier.Active = true;
        modifier.Influence = 1.0f;
    }

    private static void ActivateModifierGroup(IEnumerable<SkeletonModifier3D> modifiers)
    {
        foreach (SkeletonModifier3D modifier in modifiers)
        {
            ActivateModifier(modifier);
        }
    }

    /// <summary>
    /// Binds shared hand behaviour grab-provider references to providers staged under the IK node.
    /// </summary>
    protected static void BindHandsToGrabProviders(Node targetRoot, CharacterIK ik)
    {
        HandPoseBehaviour[] hands = [.. FindDescendants<HandPoseBehaviour>(targetRoot)];
        foreach (HandPoseBehaviour hand in hands)
        {
            switch (hand.Side)
            {
                case BodyLimbSide.Right:
                    hand.GrabTargetProvider = ik.RightHandIKTargetIntentProvider as HandGrabTargetProvider;
                    break;
                case BodyLimbSide.Left:
                    hand.GrabTargetProvider = ik.LeftHandIKTargetIntentProvider as HandGrabTargetProvider;
                    break;
                default:
                    break;
            }
        }
    }

    private static bool HasInstalledHands(Node targetRoot)
        => FindDescendants<HandPoseBehaviour>(targetRoot).Any();

    /// <summary>
    /// Resolves a required IK child provider by name.
    /// </summary>
    protected static T RequireIKChild<T>(Node ik, string name)
        where T : Node
        => ik.GetNodeOrNull<T>(name)
            ?? throw new InvalidOperationException(
                $"Character IK subsystem installer could not resolve required {typeof(T).Name} '{name}' under '{ik.GetPath()}'.");

    /// <summary>
    /// Resolves the first direct child of the requested type.
    /// </summary>
    protected static T? FindDirectChild<T>(Node node)
        where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T typedChild)
            {
                return typedChild;
            }
        }

        return null;
    }

    /// <summary>
    /// Enumerates descendants of the requested type in scene order.
    /// </summary>
    protected static IEnumerable<T> FindDescendants<T>(Node node)
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

    /// <summary>
    /// Resolves a single descendant of the requested type in scene order.
    /// </summary>
    protected static T? FindSingleDescendant<T>(Node node)
        where T : Node
    {
        T? match = null;
        foreach (T candidate in FindDescendants<T>(node))
        {
            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Character IK subsystem installer found multiple {typeof(T).Name} nodes under '{node.GetPath()}'.");
            }

            match = candidate;
        }

        return match;
    }

    /// <summary>
    /// Fails when a required template-authored node reference is absent.
    /// </summary>
    protected static void RequireAssigned(Node? value, Node owner, string propertyName)
    {
        if (!IsAssigned(value))
        {
            throw new InvalidOperationException(
                $"Character IK subsystem installer requires template-authored '{propertyName}' on '{owner.GetPath()}'.");
        }
    }

    /// <summary>
    /// Returns whether a node reference is non-null and still points to a live Godot object.
    /// </summary>
    protected static bool IsAssigned(Node? value)
        => value is not null && IsInstanceValid(value);

    /// <summary>
    /// Fails when a required template-authored modifier group is absent or contains null entries.
    /// </summary>
    protected static void RequireGroup(IReadOnlyList<SkeletonModifier3D> modifiers, Node owner, string propertyName)
    {
        if (modifiers.Count == 0 || modifiers.Any(modifier => modifier is null))
        {
            throw new InvalidOperationException(
                $"Character IK subsystem installer requires template-authored '{propertyName}' on '{owner.GetPath()}'.");
        }
    }
}
