using AlleyCat.Core.Installer;
using Godot;

namespace AlleyCat.Character.Installer;

/// <summary>
/// Strongly-typed character template installation context with explicit template and skeleton dependencies.
/// </summary>
/// <param name="targetRoot">The character root being installed.</param>
/// <param name="metadataNamespace">The metadata namespace used for idempotency markers.</param>
/// <param name="templateRoot">The instantiated role template root.</param>
/// <param name="targetSkeleton">The target character skeleton.</param>
/// <param name="templateSkeleton">The template character skeleton.</param>
public sealed class CharacterInstallationContext(
    Node targetRoot,
    string metadataNamespace,
    Node templateRoot,
    Skeleton3D targetSkeleton,
    Skeleton3D templateSkeleton)
    : TemplateSceneInstallationContext(targetRoot, metadataNamespace, templateRoot)
{
    /// <summary>
    /// Gets the resolved skeleton for skeleton-bound character modules.
    /// </summary>
    public Skeleton3D Skeleton
    {
        get;
    } = targetSkeleton ?? throw new ArgumentNullException(nameof(targetSkeleton));

    /// <summary>
    /// Gets the resolved role-template skeleton used as the source for skeleton-bound modules.
    /// </summary>
    public Skeleton3D TemplateSkeleton
    {
        get;
    } = templateSkeleton ?? throw new ArgumentNullException(nameof(templateSkeleton));

    /// <summary>
    /// Creates a copy of the context with a different target root while preserving template and skeleton dependencies.
    /// </summary>
    public override CharacterInstallationContext WithTargetRoot(Node targetRoot)
        => new(targetRoot, MetadataNamespace, TemplateRoot, Skeleton, TemplateSkeleton);

    /// <summary>
    /// Creates a copy of the context targeting the resolved skeleton.
    /// </summary>
    public CharacterInstallationContext WithSkeletonTarget()
        => WithTargetRoot(Skeleton);

    /// <summary>
    /// Resolves the skeleton for a character installation invocation.
    /// </summary>
    /// <param name="sceneContext">The general scene installation context.</param>
    /// <returns>The parent-provided or conventionally discovered character skeleton.</returns>
    public static Skeleton3D ResolveTargetSkeleton(SceneInstallationContext sceneContext)
    {
        ArgumentNullException.ThrowIfNull(sceneContext);

        return ResolveSingleSkeleton(sceneContext.TargetRoot);
    }

    /// <summary>
    /// Resolves an explicitly configured skeleton path or discovers exactly one skeleton below the supplied root.
    /// </summary>
    public static Skeleton3D ResolveSkeleton(Node root, NodePath? skeletonPath, string roleDescription)
    {
        ArgumentNullException.ThrowIfNull(root);

        return skeletonPath is not null && !string.IsNullOrWhiteSpace(skeletonPath.ToString())
            ? root.GetNodeOrNull<Skeleton3D>(skeletonPath)
                ?? throw new InvalidOperationException(
                    $"Character installer {roleDescription} '{root.GetPath()}' could not resolve {nameof(Skeleton3D)} path '{skeletonPath}'.")
            : ResolveSingleSkeleton(root, roleDescription);
    }

    private static Skeleton3D ResolveSingleSkeleton(Node targetRoot, string roleDescription = "target")
    {
        if (targetRoot is Skeleton3D targetSkeleton)
        {
            return targetSkeleton;
        }

        List<Skeleton3D> skeletons = [];
        CollectSkeletonDescendants(targetRoot, skeletons);
        return skeletons.Count == 1
            ? skeletons[0]
            : throw new InvalidOperationException(
            skeletons.Count == 0
                ? $"Character installer {roleDescription} '{targetRoot.GetPath()}' does not contain a {nameof(Skeleton3D)}. "
                    + $"Configure a single root-level skeleton path or provide exactly one skeleton under the {roleDescription} root."
                : $"Character installer {roleDescription} '{targetRoot.GetPath()}' contains {skeletons.Count} skeletons. "
                    + $"Configure a single root-level skeleton path or provide exactly one skeleton under the {roleDescription} root.");
    }

    private static void CollectSkeletonDescendants(Node node, List<Skeleton3D> skeletons)
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is Skeleton3D skeleton)
            {
                skeletons.Add(skeleton);
            }

            CollectSkeletonDescendants(child, skeletons);
        }
    }

}
