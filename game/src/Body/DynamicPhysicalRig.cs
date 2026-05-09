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
    private readonly List<ProxyBinding> _proxyBindings = [];
    private readonly Dictionary<StringName, List<PhysicsBody3D>> _generatedBodiesByBoneName = [];

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
    /// Number of unresolved source shapes encountered during the most recent build attempt before the rig failed fast.
    /// </summary>
    public int SkippedSourceShapeCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of physics-frame proxy synchronisation ticks executed since startup.
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
        SetPhysicsProcess(true);
        QueueRigRefresh();
    }

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta)
    {
        if (delta <= 0d || _proxyBindings.Count == 0)
        {
            return;
        }

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
                    Node3D sourceBoneMarker = FindExactNode3DAncestor(sourceShape);
                    PhysicsBody3D sourceBody = FindPhysicsBodyAncestor(sourceShape);
                    Shape3D duplicatedShape = DuplicateShape(sourceShape);

                    string targetBoneName = ResolveTargetBoneName(sourceBoneMarker);
                    int targetBoneIndex = ResolveTargetBoneIndex(skeleton, sourceShape, sourceBoneMarker, targetBoneName);

                    BoneAttachment3D attachment = CreateAttachment(sourceBoneMarker.Name, targetBoneName, targetBoneIndex);
                    skeleton.AddChild(attachment);
                    AssignOwnerIfNeeded(attachment, persistedOwner);

                    AnimatableBody3D proxyBody = CreateProxyBody(sourceBody);
                    proxyBody.CollisionLayer = ProxyCollisionLayer;
                    proxyBody.CollisionMask = ProxyCollisionMask;
                    TagGeneratedNode(proxyBody, sourceBoneMarker.Name);
                    attachment.AddChild(proxyBody);
                    AssignOwnerIfNeeded(proxyBody, persistedOwner);

                    CollisionShape3D proxyShape = CreateProxyShape(sourceShape, duplicatedShape);
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
                    _proxyBindings.Add(new ProxyBinding(attachment, proxyBody, sourceBody.Transform));
                    GeneratedProxyCount += 1;
                }

                ApplyAdjacentBoneCollisionExceptions(skeleton, generatedBodiesByBone);
                SyncProxyBodiesToPhysics();

                if (GeneratedProxyCount == 0)
                {
                    throw new InvalidOperationException(
                        $"{nameof(DynamicPhysicalRig)} '{Name}' could not generate any proxy bodies from '{sourceScene.ResourcePath}'.");
                }
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
        _proxyBindings.Clear();
        _generatedBodiesByBoneName.Clear();
    }

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
            Name = $"GeneratedCollider_{targetBoneName}_{GeneratedProxyCount:D2}",
            BoneName = targetBoneName,
            BoneIdx = targetBoneIndex,
        };

        TagGeneratedNode(attachment, sourceBoneIdentifier);
        return attachment;
    }

    private static AnimatableBody3D CreateProxyBody(PhysicsBody3D sourceBody)
    {
        AnimatableBody3D proxyBody = new()
        {
            Name = "ProxyBody",
            Transform = sourceBody.Transform,
            TopLevel = true,
            SyncToPhysics = true,
        };

        return proxyBody;
    }

    private void SyncProxyBodiesToPhysics()
    {
        if (_proxyBindings.Count == 0)
        {
            return;
        }

        PhysicsProxySyncTickCount += 1;

        foreach (ProxyBinding binding in _proxyBindings)
        {
            if (!IsInstanceValid(binding.Attachment) || !IsInstanceValid(binding.ProxyBody))
            {
                continue;
            }

            binding.ProxyBody.GlobalTransform = binding.Attachment.GlobalTransform * binding.LocalBodyTransform;
        }
    }

    private static CollisionShape3D CreateProxyShape(CollisionShape3D sourceShape, Shape3D duplicatedShape)
    {
        CollisionShape3D proxyShape = new()
        {
            Name = sourceShape.Name,
            Shape = duplicatedShape,
            Disabled = sourceShape.Disabled,
            Transform = sourceShape.Transform,
        };

        return proxyShape;
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

    private static string ResolveTargetBoneName(Node3D sourceBoneMarker)
        => sourceBoneMarker.Name;

    private int ResolveTargetBoneIndex(
        Skeleton3D skeleton,
        CollisionShape3D sourceShape,
        Node3D sourceBoneMarker,
        string targetBoneName)
    {
        int targetBoneIndex = skeleton.FindBone(targetBoneName);
        if (targetBoneIndex >= 0)
        {
            return targetBoneIndex;
        }

        SkippedSourceShapeCount += 1;
        throw new InvalidOperationException(
            $"{nameof(DynamicPhysicalRig)} '{Name}' could not resolve source shape '{sourceShape.Name}' because nearest exact " +
            $"{nameof(Node3D)} marker '{sourceBoneMarker.Name}' does not match any bone on skeleton '{skeleton.Name}'. " +
            "The direct-name contract requires every source marker name to resolve exactly.");
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

    private static Node3D FindExactNode3DAncestor(Node start)
    {
        for (Node? current = start.GetParent(); current is not null; current = current.GetParent())
        {
            if (current.GetType() == typeof(Node3D))
            {
                return (Node3D)current;
            }
        }

        throw new InvalidOperationException(
            $"Unable to find an exact {nameof(Node3D)} ancestor for source shape '{start.Name}'.");
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

    private static Shape3D DuplicateShape(CollisionShape3D sourceShape)
    {
        Shape3D source = sourceShape.Shape
                         ?? throw new InvalidOperationException(
                             $"Source shape '{sourceShape.Name}' does not reference a {nameof(Shape3D)} resource.");

        return (Shape3D)source.Duplicate();
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

    private sealed record ProxyBinding(BoneAttachment3D Attachment, AnimatableBody3D ProxyBody, Transform3D LocalBodyTransform);
}
