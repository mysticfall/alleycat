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
    private const float FallbackFingerSegmentLength = 0.025f;
    private const float MinimumFingerCapsuleLength = 0.018f;
    private const float MinimumFingerCapsuleRadius = 0.0075f;
    private const float MaximumFingerCapsuleRadius = 0.018f;

    private bool _regenerationQueued;
    private readonly Dictionary<StringName, List<PhysicsBody3D>> _generatedBodiesByBoneName = [];
    private readonly Dictionary<FingerSide, List<PhysicsBody3D>> _generatedFingerBodiesBySide = [];
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
    } = 15;

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
    /// Number of generated finger proxy bodies added from target skeleton rest data during the most recent build.
    /// </summary>
    public int GeneratedFingerProxyCount
    {
        get;
        private set;
    }

    /// <summary>
    /// Number of same-side finger self-filter collision exception pairs applied during the most recent build.
    /// </summary>
    public int FingerSideExceptionPairCount
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

    /// <summary>
    /// Returns generated finger proxy bodies belonging to the same side as the requested hand bone.
    /// </summary>
    public IReadOnlyList<PhysicsBody3D> GetGeneratedFingerProxyBodiesForHand(StringName handBoneName)
        => TryResolveFingerSide(handBoneName.ToString(), out FingerSide side) && _generatedFingerBodiesBySide.TryGetValue(side, out List<PhysicsBody3D>? bodies)
            ? bodies
            : [];

    /// <summary>
    /// Returns generated finger proxy collision shapes belonging to the same side as the requested hand bone.
    /// </summary>
    public IReadOnlyList<GeneratedProxyCollisionShape> GetGeneratedFingerProxyCollisionShapesForHand(StringName handBoneName)
    {
        IReadOnlyList<PhysicsBody3D> fingerBodies = GetGeneratedFingerProxyBodiesForHand(handBoneName);
        if (fingerBodies.Count == 0)
        {
            return [];
        }

        List<GeneratedProxyCollisionShape> collisionShapes = [];
        foreach (PhysicsBody3D fingerBody in fingerBodies)
        {
            CollectGeneratedCollisionShapes(fingerBody, collisionShapes);
        }

        return collisionShapes;
    }

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
            Dictionary<int, List<PhysicsBody3D>> nonFingerGeneratedBodiesByBone = [];
            Node? persistedOwner = ResolveGeneratedOwner();

            foreach (BodyColliderShapeDescriptor sourceShapeDescriptor in sourceShapeDescriptors)
            {
                if (!TryResolveTargetBone(skeleton, sourceShapeDescriptor, out string targetBoneName, out int targetBoneIndex))
                {
                    continue;
                }

                bool isFingerBone = IsFingerBone(skeleton, targetBoneIndex, out FingerSide fingerSide);
                if (isFingerBone && generatedBodiesByBone.ContainsKey(targetBoneIndex))
                {
                    continue;
                }

                BoneAttachment3D attachment = CreateAttachment(sourceShapeDescriptor.SourceIdentifier, targetBoneName, targetBoneIndex);
                skeleton.AddChild(attachment);
                AssignOwnerIfNeeded(attachment, persistedOwner);

                FingerProxyGeometry fingerGeometry = isFingerBone
                    ? BuildFingerProxyGeometry(skeleton, targetBoneIndex)
                    : default;
                Transform3D localProxyTransform = isFingerBone ? fingerGeometry.LocalTransform : sourceShapeDescriptor.LocalTransform;

                AnimatableBody3D proxyBody = CreateProxyBody(localProxyTransform);
                proxyBody.CollisionLayer = ProxyCollisionLayer;
                proxyBody.CollisionMask = ProxyCollisionMask;
                TagGeneratedNode(proxyBody, sourceShapeDescriptor.SourceIdentifier);
                attachment.AddChild(proxyBody);
                AssignOwnerIfNeeded(proxyBody, persistedOwner);
                proxyBody.Transform = localProxyTransform;
                proxyBody.TopLevel = true;
                _proxyBindings.Add(new ProxyBinding(attachment, proxyBody, localProxyTransform));

                CollisionShape3D proxyShape = isFingerBone
                    ? CreateFingerProxyShape(fingerGeometry)
                    : CreateProxyShape(sourceShapeDescriptor);
                TagGeneratedNode(proxyShape, sourceShapeDescriptor.SourceShapeName);
                proxyBody.AddChild(proxyShape);
                AssignOwnerIfNeeded(proxyShape, persistedOwner);

                if (!generatedBodiesByBone.TryGetValue(targetBoneIndex, out List<PhysicsBody3D>? bodies))
                {
                    bodies = [];
                    generatedBodiesByBone[targetBoneIndex] = bodies;
                }

                bodies.Add(proxyBody);
                if (isFingerBone)
                {
                    if (!_generatedFingerBodiesBySide.TryGetValue(fingerSide, out List<PhysicsBody3D>? fingerBodies))
                    {
                        fingerBodies = [];
                        _generatedFingerBodiesBySide[fingerSide] = fingerBodies;
                    }

                    fingerBodies.Add(proxyBody);
                    GeneratedFingerProxyCount += 1;
                }
                else
                {
                    if (!nonFingerGeneratedBodiesByBone.TryGetValue(targetBoneIndex, out List<PhysicsBody3D>? nonFingerBodies))
                    {
                        nonFingerBodies = [];
                        nonFingerGeneratedBodiesByBone[targetBoneIndex] = nonFingerBodies;
                    }

                    nonFingerBodies.Add(proxyBody);
                }
                AddGeneratedBodyForBone(targetBoneName, proxyBody);
                GeneratedProxyCount += 1;
            }

            GenerateFingerProxyBodies(
                skeleton,
                generatedBodiesByBone,
                _generatedFingerBodiesBySide,
                persistedOwner);

            ApplyAdjacentBoneCollisionExceptions(skeleton, nonFingerGeneratedBodiesByBone);
            ApplyFingerSelfCollisionExceptions(skeleton, generatedBodiesByBone, _generatedFingerBodiesBySide);

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
        GeneratedFingerProxyCount = 0;
        FingerSideExceptionPairCount = 0;
        SkippedSourceShapeCount = 0;
        PhysicsProxySyncTickCount = 0;
        _generatedBodiesByBoneName.Clear();
        _generatedFingerBodiesBySide.Clear();
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

    private static void CollectGeneratedCollisionShapes(Node root, List<GeneratedProxyCollisionShape> collisionShapes)
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is CollisionShape3D { Shape: not null } collisionShape)
            {
                collisionShapes.Add(new GeneratedProxyCollisionShape(collisionShape, collisionShape.Shape, collisionShape.Disabled));
            }

            CollectGeneratedCollisionShapes(child, collisionShapes);
        }
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

    private static CollisionShape3D CreateFingerProxyShape(FingerProxyGeometry geometry)
        => new()
        {
            Name = "GeneratedFingerCapsule",
            Shape = new CapsuleShape3D
            {
                Radius = geometry.Radius,
                Height = geometry.Height,
            },
            Disabled = false,
            Transform = Transform3D.Identity,
        };

    private void GenerateFingerProxyBodies(
        Skeleton3D skeleton,
        Dictionary<int, List<PhysicsBody3D>> generatedBodiesByBone,
        Dictionary<FingerSide, List<PhysicsBody3D>> generatedFingerBodiesBySide,
        Node? persistedOwner)
    {
        int boneCount = skeleton.GetBoneCount();
        for (int boneIndex = 0; boneIndex < boneCount; boneIndex += 1)
        {
            if (generatedBodiesByBone.ContainsKey(boneIndex) || !IsFingerBone(skeleton, boneIndex, out FingerSide side))
            {
                continue;
            }

            string boneName = skeleton.GetBoneName(boneIndex).ToString();
            FingerProxyGeometry geometry = BuildFingerProxyGeometry(skeleton, boneIndex);
            BoneAttachment3D attachment = CreateAttachment($"GeneratedFinger:{boneName}", boneName, boneIndex);
            skeleton.AddChild(attachment);
            AssignOwnerIfNeeded(attachment, persistedOwner);

            AnimatableBody3D proxyBody = CreateProxyBody(geometry.LocalTransform);
            proxyBody.CollisionLayer = ProxyCollisionLayer;
            proxyBody.CollisionMask = ProxyCollisionMask;
            TagGeneratedNode(proxyBody, $"GeneratedFinger:{boneName}");
            attachment.AddChild(proxyBody);
            AssignOwnerIfNeeded(proxyBody, persistedOwner);
            proxyBody.Transform = geometry.LocalTransform;
            proxyBody.TopLevel = true;
            _proxyBindings.Add(new ProxyBinding(attachment, proxyBody, geometry.LocalTransform));

            CollisionShape3D proxyShape = CreateFingerProxyShape(geometry);
            TagGeneratedNode(proxyShape, $"GeneratedFingerShape:{boneName}");
            proxyBody.AddChild(proxyShape);
            AssignOwnerIfNeeded(proxyShape, persistedOwner);

            generatedBodiesByBone[boneIndex] = [proxyBody];
            if (!generatedFingerBodiesBySide.TryGetValue(side, out List<PhysicsBody3D>? fingerBodies))
            {
                fingerBodies = [];
                generatedFingerBodiesBySide[side] = fingerBodies;
            }

            fingerBodies.Add(proxyBody);
            AddGeneratedBodyForBone(boneName, proxyBody);
            GeneratedProxyCount += 1;
            GeneratedFingerProxyCount += 1;
        }
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

    private void ApplyFingerSelfCollisionExceptions(
        Skeleton3D skeleton,
        IReadOnlyDictionary<int, List<PhysicsBody3D>> generatedBodiesByBone,
        IReadOnlyDictionary<FingerSide, List<PhysicsBody3D>> generatedFingerBodiesBySide)
    {
        foreach ((FingerSide side, List<PhysicsBody3D> fingerBodies) in generatedFingerBodiesBySide)
        {
            List<PhysicsBody3D> sameSideLimbBodies = [];
            int boneCount = skeleton.GetBoneCount();
            for (int boneIndex = 0; boneIndex < boneCount; boneIndex += 1)
            {
                if (!IsSameSideFingerSelfCollisionBone(skeleton.GetBoneName(boneIndex).ToString(), side) ||
                    !generatedBodiesByBone.TryGetValue(boneIndex, out List<PhysicsBody3D>? limbBodies))
                {
                    continue;
                }

                sameSideLimbBodies.AddRange(limbBodies);
            }

            foreach (PhysicsBody3D fingerBody in fingerBodies)
            {
                foreach (PhysicsBody3D limbBody in sameSideLimbBodies)
                {
                    AddBidirectionalCollisionException(fingerBody, limbBody);
                    FingerSideExceptionPairCount += 1;
                }
            }

            for (int sourceIndex = 0; sourceIndex < fingerBodies.Count; sourceIndex += 1)
            {
                for (int otherIndex = sourceIndex + 1; otherIndex < fingerBodies.Count; otherIndex += 1)
                {
                    AddBidirectionalCollisionException(fingerBodies[sourceIndex], fingerBodies[otherIndex]);
                    FingerSideExceptionPairCount += 1;
                }
            }
        }
    }

    private static FingerProxyGeometry BuildFingerProxyGeometry(Skeleton3D skeleton, int boneIndex)
    {
        Transform3D boneRest = skeleton.GetBoneGlobalRest(boneIndex);
        Vector3 localEndOffset = ResolveFingerLocalEndOffset(skeleton, boneIndex, boneRest);
        float measuredLength = Mathf.Max(localEndOffset.Length(), MinimumFingerCapsuleLength);
        Vector3 direction = localEndOffset.LengthSquared() > 0.000001f ? localEndOffset.Normalized() : Vector3.Up;
        float height = measuredLength;
        float radius = Mathf.Clamp(measuredLength * 0.18f, MinimumFingerCapsuleRadius, MaximumFingerCapsuleRadius);
        radius = Mathf.Min(radius, (height * 0.5f) - 0.001f);
        Basis basis = BuildBasisWithLocalY(direction);

        return new FingerProxyGeometry(
            new Transform3D(basis, direction * (height * 0.5f)),
            radius,
            height);
    }

    private static Vector3 ResolveFingerLocalEndOffset(Skeleton3D skeleton, int boneIndex, Transform3D boneRest)
    {
        if (TryGetPrimaryFingerChildRestOffset(skeleton, boneIndex, boneRest, out Vector3 childOffset))
        {
            return childOffset;
        }

        int parentBoneIndex = skeleton.GetBoneParent(boneIndex);
        if (parentBoneIndex >= 0)
        {
            Transform3D parentRest = skeleton.GetBoneGlobalRest(parentBoneIndex);
            Vector3 skeletonLocalDirection = boneRest.Origin - parentRest.Origin;
            if (skeletonLocalDirection.LengthSquared() > 0.000001f)
            {
                float inferredLength = TryMeasureSiblingFingerSegmentLength(skeleton, boneIndex, parentBoneIndex, out float siblingLength)
                    ? siblingLength
                    : skeletonLocalDirection.Length();
                inferredLength = Mathf.Max(inferredLength, MinimumFingerCapsuleLength);
                Vector3 skeletonLocalEnd = boneRest.Origin + (skeletonLocalDirection.Normalized() * inferredLength);
                return boneRest.AffineInverse() * skeletonLocalEnd;
            }
        }

        return Vector3.Up * FallbackFingerSegmentLength;
    }

    private static bool TryGetPrimaryFingerChildRestOffset(
        Skeleton3D skeleton,
        int boneIndex,
        Transform3D boneRest,
        out Vector3 childOffset)
    {
        childOffset = Vector3.Zero;
        float closestDistanceSquared = float.PositiveInfinity;
        int boneCount = skeleton.GetBoneCount();

        for (int childIndex = 0; childIndex < boneCount; childIndex += 1)
        {
            if (skeleton.GetBoneParent(childIndex) != boneIndex || !IsFingerBone(skeleton, childIndex, out _))
            {
                continue;
            }

            Vector3 candidateOffset = boneRest.AffineInverse() * skeleton.GetBoneGlobalRest(childIndex).Origin;
            float distanceSquared = candidateOffset.LengthSquared();
            if (distanceSquared <= 0.000001f || distanceSquared >= closestDistanceSquared)
            {
                continue;
            }

            childOffset = candidateOffset;
            closestDistanceSquared = distanceSquared;
        }

        return closestDistanceSquared < float.PositiveInfinity;
    }

    private static bool TryMeasureSiblingFingerSegmentLength(
        Skeleton3D skeleton,
        int boneIndex,
        int parentBoneIndex,
        out float length)
    {
        length = 0.0f;
        int measuredSiblingCount = 0;
        int boneCount = skeleton.GetBoneCount();

        for (int siblingIndex = 0; siblingIndex < boneCount; siblingIndex += 1)
        {
            if (siblingIndex == boneIndex ||
                skeleton.GetBoneParent(siblingIndex) != parentBoneIndex ||
                !IsFingerBone(skeleton, siblingIndex, out _))
            {
                continue;
            }

            Transform3D siblingRest = skeleton.GetBoneGlobalRest(siblingIndex);
            if (!TryGetPrimaryFingerChildRestOffset(skeleton, siblingIndex, siblingRest, out Vector3 siblingChildOffset))
            {
                continue;
            }

            length += siblingChildOffset.Length();
            measuredSiblingCount += 1;
        }

        if (measuredSiblingCount == 0)
        {
            return false;
        }

        length /= measuredSiblingCount;
        return length > 0.000001f;
    }

    private static Basis BuildBasisWithLocalY(Vector3 localY)
    {
        Vector3 y = localY.Normalized();
        Vector3 fallback = Mathf.Abs(y.Dot(Vector3.Forward)) < 0.95f ? Vector3.Forward : Vector3.Right;
        Vector3 x = fallback.Cross(y).Normalized();
        Vector3 z = x.Cross(y).Normalized();
        return new Basis(x, y, z);
    }

    private static bool IsFingerBone(Skeleton3D skeleton, int boneIndex, out FingerSide side)
    {
        string boneName = skeleton.GetBoneName(boneIndex).ToString();
        if (!TryResolveFingerSide(boneName, out side) || !ContainsFingerToken(boneName) || ContainsToeToken(boneName))
        {
            side = FingerSide.None;
            return false;
        }

        return HasSameSideHandAncestor(skeleton, boneIndex, side);
    }

    private static bool HasSameSideHandAncestor(Skeleton3D skeleton, int boneIndex, FingerSide side)
    {
        for (int ancestorIndex = skeleton.GetBoneParent(boneIndex); ancestorIndex >= 0; ancestorIndex = skeleton.GetBoneParent(ancestorIndex))
        {
            string ancestorName = skeleton.GetBoneName(ancestorIndex).ToString();
            if (IsHandBone(ancestorName, side))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHandBone(string boneName, FingerSide side)
        => TryResolveFingerSide(boneName, out FingerSide resolvedSide) &&
           resolvedSide == side &&
           ContainsToken(boneName, "hand") &&
           !ContainsFingerToken(boneName) &&
           !ContainsToeToken(boneName);

    private static bool IsSameSideFingerSelfCollisionBone(string boneName, FingerSide side)
        => TryResolveFingerSide(boneName, out FingerSide resolvedSide) &&
           resolvedSide == side &&
           IsFingerSelfCollisionLimbToken(boneName) &&
           !ContainsFingerToken(boneName) &&
           !ContainsToeToken(boneName);

    private static bool IsFingerSelfCollisionLimbToken(string boneName)
        => ContainsToken(boneName, "hand");

    private static bool ContainsFingerToken(string boneName)
        => ContainsToken(boneName, "thumb") ||
           ContainsToken(boneName, "index") ||
           ContainsToken(boneName, "middle") ||
           ContainsToken(boneName, "ring") ||
           ContainsToken(boneName, "pinky") ||
           ContainsToken(boneName, "little");

    private static bool ContainsToeToken(string boneName)
        => ContainsToken(boneName, "toe") || ContainsToken(boneName, "toes");

    private static bool ContainsToken(string boneName, string token)
        => boneName.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveFingerSide(string boneName, out FingerSide side)
    {
        if (boneName.Contains("left", StringComparison.OrdinalIgnoreCase))
        {
            side = FingerSide.Left;
            return true;
        }

        if (boneName.Contains("right", StringComparison.OrdinalIgnoreCase))
        {
            side = FingerSide.Right;
            return true;
        }

        if (HasCompactSideMarker(boneName, 'L'))
        {
            side = FingerSide.Left;
            return true;
        }

        if (HasCompactSideMarker(boneName, 'R'))
        {
            side = FingerSide.Right;
            return true;
        }

        side = FingerSide.None;
        return false;
    }

    private static bool HasCompactSideMarker(string boneName, char sideMarker)
    {
        if ((sideMarker == 'L' && boneName.StartsWith("little", StringComparison.OrdinalIgnoreCase)) ||
            (sideMarker == 'R' && boneName.StartsWith("ring", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        for (int index = 0; index < boneName.Length; index += 1)
        {
            if (char.ToUpperInvariant(boneName[index]) != sideMarker)
            {
                continue;
            }

            bool hasPreviousBoundary = index == 0 || !char.IsLetterOrDigit(boneName[index - 1]);
            bool hasNextBoundary = index == boneName.Length - 1 || !char.IsLetterOrDigit(boneName[index + 1]);
            bool startsCamelToken = index == 0 && index + 1 < boneName.Length && char.IsUpper(boneName[index + 1]);
            if (hasPreviousBoundary || hasNextBoundary || startsCamelToken)
            {
                return true;
            }
        }

        return false;
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

    private readonly record struct FingerProxyGeometry(
        Transform3D LocalTransform,
        float Radius,
        float Height);

    private enum FingerSide
    {
        None,
        Left,
        Right,
    }

}

/// <summary>
/// Runtime collision-shape data generated by <see cref="DynamicPhysicalRig"/> for IK-side mirroring.
/// </summary>
public readonly record struct GeneratedProxyCollisionShape(
    CollisionShape3D SourceShape,
    Shape3D Shape,
    bool Disabled);
