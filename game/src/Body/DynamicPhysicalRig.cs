using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Builds a bone-attached proxy collision rig from a collider profile.
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
    /// Reusable collider descriptor profile shared by runtime body systems.
    /// </summary>
    [Export]
    public BodyColliderProfile? ColliderProfile
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
        BodyColliderProfile colliderProfile = ResolveColliderProfile();
        string sourcePath = ResolveColliderSourcePath(colliderProfile);

        try
        {
            IReadOnlyList<BodyColliderShapeDescriptor> sourceShapeDescriptors = colliderProfile.QueryShapeDescriptors();

            Dictionary<int, List<PhysicsBody3D>> generatedBodiesByBone = [];
            Node? persistedOwner = ResolveGeneratedOwner();

            foreach (BodyColliderShapeDescriptor sourceShapeDescriptor in sourceShapeDescriptors)
            {
                if (!TryResolveTargetBone(skeleton, sourceShapeDescriptor, out string targetBoneName, out int targetBoneIndex))
                {
                    continue;
                }

                BoneAttachment3D attachment = CreateAttachment(sourceShapeDescriptor.SourceIdentifier, targetBoneName, targetBoneIndex);
                skeleton.AddChild(attachment);
                AssignOwnerIfNeeded(attachment, persistedOwner);

                Transform3D localProxyTransform = sourceShapeDescriptor.LocalTransform;

                AnimatableBody3D proxyBody = CreateProxyBody(localProxyTransform);
                proxyBody.CollisionLayer = ProxyCollisionLayer;
                proxyBody.CollisionMask = ProxyCollisionMask;
                TagGeneratedNode(proxyBody, sourceShapeDescriptor.SourceIdentifier);
                attachment.AddChild(proxyBody);
                AssignOwnerIfNeeded(proxyBody, persistedOwner);
                proxyBody.Transform = localProxyTransform;
                proxyBody.TopLevel = true;
                _proxyBindings.Add(new ProxyBinding(attachment, proxyBody, localProxyTransform));

                CollisionShape3D proxyShape = CreateProxyShape(sourceShapeDescriptor);
                TagGeneratedNode(proxyShape, sourceShapeDescriptor.SourceShapeName);
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
                    $"{nameof(DynamicPhysicalRig)} '{Name}' could not generate any proxy bodies from '{sourcePath}'.");
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

    private BodyColliderProfile ResolveColliderProfile()
        => ColliderProfile
           ?? throw new InvalidOperationException(
               $"{nameof(DynamicPhysicalRig)} '{Name}' requires a configured collider profile.");

    private static string ResolveColliderSourcePath(BodyColliderProfile colliderProfile)
        => colliderProfile.SourceScene?.ResourcePath ?? colliderProfile.ResourcePath;

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

    private static CollisionShape3D CreateProxyShape(BodyColliderShapeDescriptor sourceShapeDescriptor)
    {
        CollisionShape3D proxyShape = new()
        {
            Name = sourceShapeDescriptor.SourceShapeName,
            Shape = sourceShapeDescriptor.Shape,
            Disabled = sourceShapeDescriptor.Disabled,
            Transform = Transform3D.Identity,
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

    private bool TryResolveTargetBone(
        Skeleton3D skeleton,
        BodyColliderShapeDescriptor sourceShapeDescriptor,
        out string targetBoneName,
        out int targetBoneIndex)
    {
        targetBoneName = sourceShapeDescriptor.SourceBoneName.ToString();
        targetBoneIndex = string.IsNullOrWhiteSpace(targetBoneName) ? -1 : skeleton.FindBone(targetBoneName);
        if (targetBoneIndex >= 0)
        {
            return true;
        }

        SkippedSourceShapeCount += 1;
        GD.PushWarning(
            $"{nameof(DynamicPhysicalRig)} '{Name}' skipped source shape '{sourceShapeDescriptor.SourceShapeName}' from source bone attachment " +
            $"'{sourceShapeDescriptor.SourceIdentifier}' because BoneName '{targetBoneName}' did not resolve to a bone on skeleton '{skeleton.Name}'.");
        return false;
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
