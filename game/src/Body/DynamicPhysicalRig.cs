using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Builds a bone-attached proxy collision rig from a source authoring scene.
/// </summary>
[Tool]
[GlobalClass]
public partial class DynamicPhysicalRig : Node
{
    private const string GeneratedNodeMetaKey = "alleycat_generated_physical_rig";
    private const string GeneratedOwnerPathMetaKey = "alleycat_generated_physical_rig_owner_path";

    private bool _regenerationQueued;
    private readonly Dictionary<StringName, List<PhysicsBody3D>> _generatedBodiesByBoneName = [];
    private readonly List<ProxyBinding> _proxyBindings = [];

    /// <summary>
    /// Source authoring scene that contains the collider shapes to duplicate.
    /// </summary>
    [Export]
    public PackedScene? SourceScene
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            QueueRigRefresh();
        }
    }

    /// <summary>
    /// Optional direct skeleton reference. When unset, the parent <see cref="Skeleton3D"/> is used.
    /// </summary>
    [Export]
    public Skeleton3D? TargetSkeleton
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            QueueRigRefresh();
        }
    }

    /// <summary>
    /// Enables runtime generation for this rig.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            QueueRigRefresh();
        }
    } = true;

    /// <summary>
    /// When enabled, generates the rig while running in the editor so the result can be inspected.
    /// </summary>
    [Export]
    public bool GenerateInEditor
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            QueueRigRefresh();
        }
    }

    /// <summary>
    /// Collision layer applied to generated proxy bodies.
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint ProxyCollisionLayer
    {
        get;
        set;
    } = 4;

    /// <summary>
    /// Collision mask applied to generated proxy bodies.
    /// </summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint ProxyCollisionMask
    {
        get;
        set;
    } = 11;

    /// <summary>
    /// Number of proxy bodies generated during the most recent build.
    /// </summary>
    public int GeneratedProxyCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of adjacent body-pair collision exceptions applied during the most recent build.
    /// </summary>
    public int AdjacentBoneExceptionPairCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of unresolved source shapes skipped during the most recent build attempt.
    /// </summary>
    public int SkippedSourceShapeCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of manual generated-proxy synchronisation passes run since the latest clear.
    /// </summary>
    public ulong PhysicsProxySyncTickCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Returns the generated proxy bodies currently bound to the requested skeleton bone.
    /// </summary>
    public IReadOnlyList<PhysicsBody3D> GetGeneratedProxyBodiesForBone(StringName boneName)
        => _generatedBodiesByBoneName.TryGetValue(boneName, out List<PhysicsBody3D>? bodies)
            ? bodies
            : [];

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        SetPhysicsProcess(_proxyBindings.Count > 0);
        QueueRigRefresh();
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        SyncProxyBodiesToPhysics();
    }

    /// <summary>
    /// Clears any previously generated rig and rebuilds it when generation is currently enabled.
    /// </summary>
    public void RegenerateNow()
        => RefreshRig();

    private void QueueRigRefresh()
    {
        if (!IsInsideTree() || _regenerationQueued)
        {
            return;
        }

        _regenerationQueued = true;
        _ = CallDeferred(nameof(RefreshRig));
    }

    private void RefreshRig()
    {
        _regenerationQueued = false;

        if (!IsInsideTree())
        {
            return;
        }

        ClearGeneratedRig();

        if (!ShouldGenerateRig())
        {
            return;
        }

        BuildGeneratedRig();
    }

    private bool ShouldGenerateRig()
        => Enabled && (!Engine.IsEditorHint() || GenerateInEditor);

    private void BuildGeneratedRig()
    {
        Skeleton3D skeleton = ResolveTargetSkeleton();
        PackedScene sourceScene = SourceScene
                                   ?? throw new InvalidOperationException(
                                       $"{nameof(DynamicPhysicalRig)} '{Name}' requires a configured source scene.");

        Node sourceRoot = sourceScene.Instantiate();
        try
        {
            try
            {
                List<CollisionShape3D> sourceShapes = CollectSourceShapes(sourceRoot);
                if (sourceShapes.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Source scene '{sourceScene.ResourcePath}' does not contain any {nameof(CollisionShape3D)} nodes.");
                }

                Dictionary<int, List<PhysicsBody3D>> generatedBodiesByBone = [];
                Node? persistedOwner = ResolveGeneratedOwner();

                foreach (CollisionShape3D sourceShape in sourceShapes)
                {
                    BoneAttachment3D sourceBoneAttachment = ResolveSourceBoneAttachment(sourceShape);
                    _ = FindPhysicsBodyAncestor(sourceShape);

                    if (!TryResolveTargetBone(skeleton, sourceShape, sourceBoneAttachment, out string targetBoneName, out int targetBoneIndex))
                    {
                        continue;
                    }

                    string sourceIdentifier = ResolveSourceIdentifier(sourceBoneAttachment);
                    BoneAttachment3D attachment = CreateAttachment(sourceIdentifier, targetBoneName, targetBoneIndex);
                    skeleton.AddChild(attachment);
                    AssignOwnerIfNeeded(attachment, persistedOwner);

                    Transform3D sourceShapeSkeletonTransform = ResolveSourceShapeSkeletonTransform(sourceShape, sourceBoneAttachment);
                    Transform3D sourceAttachmentSkeletonTransform = ResolveSourceAttachmentSkeletonTransform(sourceBoneAttachment);
                    Transform3D localProxyTransform = sourceAttachmentSkeletonTransform.AffineInverse() * sourceShapeSkeletonTransform;

                    AnimatableBody3D proxyBody = CreateProxyBody(localProxyTransform);
                    proxyBody.CollisionLayer = ProxyCollisionLayer;
                    proxyBody.CollisionMask = ProxyCollisionMask;
                    TagGeneratedNode(proxyBody, sourceIdentifier);
                    attachment.AddChild(proxyBody);
                    AssignOwnerIfNeeded(proxyBody, persistedOwner);
                    proxyBody.Transform = localProxyTransform;
                    proxyBody.TopLevel = true;
                    _proxyBindings.Add(new ProxyBinding(attachment, proxyBody, localProxyTransform));

                    CollisionShape3D proxyShape = CreateProxyShape(sourceShape);
                    TagGeneratedNode(proxyShape, sourceShape.Name);
                    proxyBody.AddChild(proxyShape);
                    AssignOwnerIfNeeded(proxyShape, persistedOwner);

                    if (!generatedBodiesByBone.TryGetValue(targetBoneIndex, out List<PhysicsBody3D>? bodies))
                    {
                        bodies = [];
                        generatedBodiesByBone[targetBoneIndex] = bodies;
                    }

                    bodies.Add(proxyBody);
                    AddGeneratedBodyForBone(targetBoneName, proxyBody);
                    GeneratedProxyCount += 1;
                }

                ApplyAdjacentBoneCollisionExceptions(skeleton, generatedBodiesByBone);

                if (GeneratedProxyCount == 0)
                {
                    throw new InvalidOperationException(
                        $"{nameof(DynamicPhysicalRig)} '{Name}' could not generate any proxy bodies from '{sourceScene.ResourcePath}'.");
                }

                SyncProxyBodiesToPhysics();
                SetPhysicsProcess(_proxyBindings.Count > 0);
            }
            catch
            {
                int unresolvedSourceShapeCount = SkippedSourceShapeCount;
                ClearGeneratedRig();
                SkippedSourceShapeCount = unresolvedSourceShapeCount;
                throw;
            }
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private Skeleton3D ResolveTargetSkeleton()
        => TargetSkeleton
           ?? (GetParent() is Skeleton3D parentSkeleton
            ? parentSkeleton
            : throw new System.InvalidOperationException(
                $"{nameof(DynamicPhysicalRig)} '{Name}' requires either a parent {nameof(Skeleton3D)} or an explicit target skeleton."));

    private void ClearGeneratedRig()
    {
        SetPhysicsProcess(false);
        Skeleton3D skeleton = ResolveTargetSkeleton();
        List<Node> generatedChildren = [];

        foreach (Node child in skeleton.GetChildren())
        {
            if (!IsNodeGeneratedByThisComponent(child))
            {
                continue;
            }

            generatedChildren.Add(child);
        }

        foreach (Node generatedChild in generatedChildren)
        {
            skeleton.RemoveChild(generatedChild);
            generatedChild.QueueFree();
        }

        GeneratedProxyCount = 0;
        AdjacentBoneExceptionPairCount = 0;
        SkippedSourceShapeCount = 0;
        PhysicsProxySyncTickCount = 0;
        _generatedBodiesByBoneName.Clear();
        _proxyBindings.Clear();
    }

    /// <summary>
    /// Manually synchronises generated top-level proxy bodies to their generated bone attachments.
    /// </summary>
    public void SyncProxyBodiesToPhysics()
    {
        if (_proxyBindings.Count == 0)
        {
            return;
        }

        foreach (ProxyBinding binding in _proxyBindings)
        {
            Transform3D globalProxyTransform = ResolveNodeGlobalTransform(binding.Attachment) * binding.LocalProxyTransform;
            binding.ProxyBody.GlobalTransform = globalProxyTransform;
            binding.ProxyBody.Transform = globalProxyTransform;
            if (binding.ProxyBody.IsInsideTree())
            {
                binding.ProxyBody.ForceUpdateTransform();
            }
        }

        PhysicsProxySyncTickCount += 1;
    }

    private static Transform3D ResolveNodeGlobalTransform(Node3D node)
        => node.GetParent() is Node3D parent ? parent.GlobalTransform * node.Transform : node.GlobalTransform;

    private void AddGeneratedBodyForBone(StringName boneName, PhysicsBody3D body)
    {
        if (!_generatedBodiesByBoneName.TryGetValue(boneName, out List<PhysicsBody3D>? bodies))
        {
            bodies = [];
            _generatedBodiesByBoneName[boneName] = bodies;
        }

        bodies.Add(body);
    }

    private bool IsNodeGeneratedByThisComponent(Node node)
    {
        if (!node.HasMeta(GeneratedNodeMetaKey))
        {
            return false;
        }

        Variant ownerPath = node.GetMeta(GeneratedOwnerPathMetaKey, Variant.From(string.Empty));
        return ownerPath.AsString() == GetPath().ToString();
    }

    private BoneAttachment3D CreateAttachment(string sourceBoneIdentifier, string targetBoneName, int targetBoneIndex)
    {
        BoneAttachment3D attachment = new()
        {
            Name = $"Collider_{targetBoneName}_{GeneratedProxyCount:D2}",
            BoneName = targetBoneName,
            BoneIdx = targetBoneIndex,
        };

        TagGeneratedNode(attachment, sourceBoneIdentifier);
        return attachment;
    }

    private static AnimatableBody3D CreateProxyBody(Transform3D localProxyTransform)
    {
        AnimatableBody3D proxyBody = new()
        {
            Name = "ProxyBody",
            Transform = localProxyTransform,
            SyncToPhysics = false,
        };

        return proxyBody;
    }

    private static CollisionShape3D CreateProxyShape(CollisionShape3D sourceShape)
    {
        Shape3D sourceShapeResource = sourceShape.Shape
                                     ?? throw new InvalidOperationException(
                                         $"Source collision shape '{sourceShape.Name}' requires a {nameof(Shape3D)} resource.");

        CollisionShape3D proxyShape = new()
        {
            Name = sourceShape.Name,
            Shape = (Shape3D)sourceShapeResource.Duplicate(true),
            Disabled = sourceShape.Disabled,
            Transform = Transform3D.Identity,
        };

        return proxyShape;
    }

    private static Transform3D ResolveSourceShapeSkeletonTransform(CollisionShape3D sourceShape, BoneAttachment3D sourceBoneAttachment)
    {
        Node? sourceFrameRoot = FindAncestor<Skeleton3D>(sourceBoneAttachment) ?? sourceBoneAttachment.GetParent();
        return ComposeTransformRelativeToAncestor(sourceShape, sourceFrameRoot);
    }

    private static Transform3D ResolveSourceAttachmentSkeletonTransform(BoneAttachment3D sourceBoneAttachment)
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

    private void ApplyAdjacentBoneCollisionExceptions(
        Skeleton3D skeleton,
        IReadOnlyDictionary<int, List<PhysicsBody3D>> generatedBodiesByBone)
    {
        foreach ((int boneIndex, List<PhysicsBody3D> bodies) in generatedBodiesByBone)
        {
            int parentBoneIndex = skeleton.GetBoneParent(boneIndex);
            if (parentBoneIndex < 0 || !generatedBodiesByBone.TryGetValue(parentBoneIndex, out List<PhysicsBody3D>? parentBodies))
            {
                continue;
            }

            foreach (PhysicsBody3D body in bodies)
            {
                foreach (PhysicsBody3D parentBody in parentBodies)
                {
                    AddBidirectionalCollisionException(body, parentBody);
                    AdjacentBoneExceptionPairCount += 1;
                }
            }
        }
    }

    private bool TryResolveTargetBone(
        Skeleton3D skeleton,
        CollisionShape3D sourceShape,
        BoneAttachment3D sourceBoneAttachment,
        out string targetBoneName,
        out int targetBoneIndex)
    {
        targetBoneName = sourceBoneAttachment.BoneName.ToString();
        targetBoneIndex = string.IsNullOrWhiteSpace(targetBoneName) ? -1 : skeleton.FindBone(targetBoneName);
        if (targetBoneIndex >= 0)
        {
            return true;
        }

        SkippedSourceShapeCount += 1;
        string sourceIdentifier = ResolveSourceIdentifier(sourceBoneAttachment);
        GD.PushWarning(
            $"{nameof(DynamicPhysicalRig)} '{Name}' skipped source shape '{sourceShape.Name}' from source bone attachment " +
            $"'{sourceIdentifier}' because BoneName '{targetBoneName}' did not resolve to a bone on skeleton '{skeleton.Name}'.");
        return false;
    }

    private static List<CollisionShape3D> CollectSourceShapes(Node root)
    {
        List<CollisionShape3D> shapes = [];
        Stack<Node> pending = new([root]);

        while (pending.Count > 0)
        {
            Node current = pending.Pop();
            if (current is CollisionShape3D collisionShape)
            {
                shapes.Add(collisionShape);
            }

            foreach (Node child in current.GetChildren())
            {
                pending.Push(child);
            }
        }

        return shapes;
    }

    private BoneAttachment3D ResolveSourceBoneAttachment(CollisionShape3D sourceShape)
    {
        for (Node? current = sourceShape.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D sourceBoneAttachment)
            {
                return sourceBoneAttachment;
            }
        }

        throw new InvalidOperationException(
            $"{nameof(DynamicPhysicalRig)} '{Name}' could not resolve source shape '{sourceShape.Name}' because it does not " +
            $"have a {nameof(BoneAttachment3D)} ancestor with the source bone name.");
    }

    private static string ResolveSourceIdentifier(BoneAttachment3D sourceBoneAttachment)
    {
        string sourceBoneName = sourceBoneAttachment.BoneName.ToString();
        return string.IsNullOrWhiteSpace(sourceBoneName) ? sourceBoneAttachment.Name : sourceBoneName;
    }

    private static PhysicsBody3D FindPhysicsBodyAncestor(Node start)
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

    private void TagGeneratedNode(Node node, string sourceIdentifier)
    {
        node.SetMeta(GeneratedNodeMetaKey, true);
        node.SetMeta(GeneratedOwnerPathMetaKey, GetPath().ToString());
        node.SetMeta("alleycat_generated_physical_rig_source", sourceIdentifier);
    }

    private Node? ResolveGeneratedOwner()
    {
        if (!Engine.IsEditorHint())
        {
            return null;
        }

        SceneTree? tree = GetTree();
        return tree?.EditedSceneRoot ?? Owner;
    }

    private static void AssignOwnerIfNeeded(Node node, Node? owner)
    {
        if (owner is null)
        {
            return;
        }

        node.Owner = owner;
    }

    private static void AddBidirectionalCollisionException(PhysicsBody3D source, PhysicsBody3D other)
    {
        if (source == other)
        {
            return;
        }

        source.AddCollisionExceptionWith(other);
        other.AddCollisionExceptionWith(source);
    }

    private readonly record struct ProxyBinding(
        BoneAttachment3D Attachment,
        AnimatableBody3D ProxyBody,
        Transform3D LocalProxyTransform);

}
