using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Reusable resource that queries body collider descriptors from an authored collider scene.
/// </summary>
[Tool]
[GlobalClass]
public partial class BodyColliderProfile : Resource
{
    /// <summary>
    /// Source authoring scene that contains collider shapes under bone attachments.
    /// </summary>
    [Export]
    public PackedScene? SourceScene
    {
        get; set;
    }

    /// <summary>
    /// Instantiates the source scene and returns immutable descriptors for every authored collision shape.
    /// </summary>
    public IReadOnlyList<BodyColliderShapeDescriptor> QueryShapeDescriptors()
    {
        PackedScene sourceScene = SourceScene
                                   ?? throw new InvalidOperationException(
                                       $"{nameof(BodyColliderProfile)} '{ResourcePath}' requires a configured source scene.");

        Node sourceRoot = sourceScene.Instantiate();
        try
        {
            List<BodyColliderShapeDescriptor> descriptors = CollectShapeDescriptors(sourceRoot);
            return descriptors.Count == 0
                ? throw new InvalidOperationException(
                    $"Source scene '{sourceScene.ResourcePath}' does not contain any {nameof(CollisionShape3D)} nodes.")
                : (IReadOnlyList<BodyColliderShapeDescriptor>)descriptors;
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    /// <summary>
    /// Returns descriptors authored for the requested bone name.
    /// </summary>
    public IReadOnlyList<BodyColliderShapeDescriptor> QueryShapeDescriptorsForBone(StringName boneName)
    {
        IReadOnlyList<BodyColliderShapeDescriptor> descriptors = QueryShapeDescriptors();
        List<BodyColliderShapeDescriptor> matchingDescriptors = [];

        foreach (BodyColliderShapeDescriptor descriptor in descriptors)
        {
            if (descriptor.SourceBoneName == boneName)
            {
                matchingDescriptors.Add(descriptor);
            }
        }

        return matchingDescriptors;
    }

    private static List<BodyColliderShapeDescriptor> CollectShapeDescriptors(Node root)
    {
        List<BodyColliderShapeDescriptor> descriptors = [];
        Stack<Node> pending = new([root]);

        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current is CollisionShape3D collisionShape)
            {
                descriptors.Add(CreateDescriptor(collisionShape));
            }

            foreach (Node child in current.GetChildren())
            {
                pending.Push(child);
            }
        }

        return descriptors;
    }

    private static BodyColliderShapeDescriptor CreateDescriptor(CollisionShape3D sourceShape)
    {
        BoneAttachment3D sourceBoneAttachment = ResolveSourceBoneAttachment(sourceShape);
        PhysicsBody3D sourcePhysicsBody = ResolvePhysicsBodyAncestor(sourceShape);
        Shape3D sourceShapeResource = sourceShape.Shape
                                      ?? throw new InvalidOperationException(
                                          $"Source shape '{sourceShape.Name}' requires a configured {nameof(Shape3D)} resource.");
        Transform3D sourceShapeFrameTransform = ResolveSourceShapeTransform(sourceShape, sourceBoneAttachment);
        Transform3D localTransform = ResolveSourceAttachmentTransform(sourceBoneAttachment).AffineInverse()
                                     * sourceShapeFrameTransform;

        return new BodyColliderShapeDescriptor(
            sourceShape.Name,
            sourceBoneAttachment.BoneName,
            ResolveSourceIdentifier(sourceBoneAttachment),
            sourcePhysicsBody.Name,
            sourceShapeResource,
            sourceShape.Disabled,
            localTransform,
            sourceShapeFrameTransform);
    }

    private static Transform3D ResolveSourceShapeTransform(CollisionShape3D sourceShape, BoneAttachment3D sourceBoneAttachment)
    {
        Node? sourceFrameRoot = FindAncestor<Skeleton3D>(sourceBoneAttachment) ?? sourceBoneAttachment.GetParent();
        return ComposeTransformRelativeToAncestor(sourceShape, sourceFrameRoot);
    }

    private static Transform3D ResolveSourceAttachmentTransform(BoneAttachment3D sourceBoneAttachment)
    {
        Node? sourceFrameRoot = FindAncestor<Skeleton3D>(sourceBoneAttachment) ?? sourceBoneAttachment.GetParent();
        return ComposeTransformRelativeToAncestor(sourceBoneAttachment, sourceFrameRoot);
    }

    private static Transform3D ComposeTransformRelativeToAncestor(Node3D node, Node? ancestor)
    {
        Transform3D transform = node.Transform;

        for (Node? current = node.GetParent(); current is not null && current != ancestor; current = current.GetParent())
        {
            if (current is Node3D current3D)
            {
                transform = current3D.Transform * transform;
            }
        }

        return transform;
    }

    private static T? FindAncestor<T>(Node start)
        where T : Node
    {
        for (Node? current = start.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is T ancestor)
            {
                return ancestor;
            }
        }

        return null;
    }

    private static BoneAttachment3D ResolveSourceBoneAttachment(CollisionShape3D sourceShape)
    {
        for (Node? current = sourceShape.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D sourceBoneAttachment)
            {
                return sourceBoneAttachment;
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve source shape '{sourceShape.Name}' because it does not have a {nameof(BoneAttachment3D)} ancestor with the source bone name.");
    }

    private static PhysicsBody3D ResolvePhysicsBodyAncestor(Node start)
    {
        for (Node? current = start.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is PhysicsBody3D physicsBody)
            {
                return physicsBody;
            }
        }

        throw new InvalidOperationException(
            $"Unable to find a {nameof(PhysicsBody3D)} ancestor for source shape '{start.Name}'.");
    }

    private static string ResolveSourceIdentifier(BoneAttachment3D sourceBoneAttachment)
    {
        string sourceBoneName = sourceBoneAttachment.BoneName.ToString();
        return string.IsNullOrWhiteSpace(sourceBoneName) ? sourceBoneAttachment.Name : sourceBoneName;
    }
}
