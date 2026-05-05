using AlleyCat.Common;
using Godot;

namespace AlleyCat.Items;

/// <summary>
/// Builds a reusable articulated physics chain from repeated chain-link scenes.
/// </summary>
[Tool]
[GlobalClass]
public partial class PhysicsChain : Node3D
{
    private const string ChainLinkScenePath = "res://assets/items/chain/chain_link.tscn";
    private const string LinksContainerPath = "Links";
    private const string JointsContainerPath = "Joints";
    private const string AttachmentJointsContainerPath = "AttachmentJoints";
    private const string StartAttachmentPointName = "StartAttachmentPoint";
    private const string EndAttachmentPointName = "EndAttachmentPoint";
    private const float MinimumLinkMass = 0.001f;
    private const int MinimumLinkCount = 2;

    private bool _isRebuildQueued;

    private Node3D? _linksContainer;
    private Node3D? _jointsContainer;
    private Node3D? _attachmentJointsContainer;
    private Marker3D? _startAttachmentPoint;
    private Marker3D? _endAttachmentPoint;
    private Joint3D? _startAttachmentJoint;
    private Joint3D? _endAttachmentJoint;
    private readonly List<RigidBody3D> _linkBodies = [];
    private readonly List<Joint3D> _linkJoints = [];
    private LinkMetrics? _linkMetrics;

    /// <summary>
    /// Gets or sets the number of chain links to build.
    /// </summary>
    [Export(PropertyHint.Range, "2,64,1,or_greater")]
    public int LinkCount
    {
        get;
        set
        {
            int clamped = Math.Max(value, MinimumLinkCount);
            if (field == clamped)
            {
                return;
            }

            field = clamped;
            QueueRebuild();
        }
    } = 8;

    /// <summary>
    /// Gets or sets additional offset applied along the chain axis between
    /// consecutive links.
    /// </summary>
    [Export(PropertyHint.Range, "-0.02,0.02,0.0005")]
    public float LinkGapAdjustment
    {
        get;
        set
        {
            if (Mathf.IsEqualApprox(field, value))
            {
                return;
            }

            field = value;
            QueueRebuild();
        }
    } = -0.015f;

    /// <summary>
    /// Gets or sets the per-link mass used for authored instances.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,5.0,0.01,or_greater")]
    public float LinkMass
    {
        get;
        set
        {
            float clamped = Math.Max(value, MinimumLinkMass);
            if (Mathf.IsEqualApprox(field, clamped))
            {
                return;
            }

            field = clamped;
            QueueRebuild();
        }
    } = 0.15f;

    /// <summary>
    /// Gets or sets the damping used by pin joints in the chain.
    /// </summary>
    [Export(PropertyHint.Range, "0.01,5.0,0.01")]
    public float JointDamping
    {
        get;
        set
        {
            if (Mathf.IsEqualApprox(field, value))
            {
                return;
            }

            field = value;
            QueueRebuild();
        }
    } = 1.0f;

    /// <summary>
    /// Gets or sets an optional physics body to attach to the first chain link.
    /// </summary>
    [Export]
    public PhysicsBody3D? StartAttachedBody
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            RefreshAttachmentJointsIfReady();
        }
    }

    /// <summary>
    /// Gets or sets an optional anchor node on <see cref="StartAttachedBody"/>.
    /// If omitted, the physics body's origin is used.
    /// </summary>
    [Export]
    public Node3D? StartAttachedBodyAnchor
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            RefreshAttachmentJointsIfReady();
        }
    }

    /// <summary>
    /// Gets or sets an optional physics body to attach to the final chain link.
    /// </summary>
    [Export]
    public PhysicsBody3D? EndAttachedBody
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            RefreshAttachmentJointsIfReady();
        }
    }

    /// <summary>
    /// Gets or sets an optional anchor node on <see cref="EndAttachedBody"/>.
    /// If omitted, the physics body's origin is used.
    /// </summary>
    [Export]
    public Node3D? EndAttachedBodyAnchor
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            RefreshAttachmentJointsIfReady();
        }
    }

    /// <summary>
    /// Gets the generated link bodies in chain order.
    /// </summary>
    public IReadOnlyList<RigidBody3D> LinkBodies => _linkBodies;

    /// <summary>
    /// Gets the generated inter-link joints in chain order.
    /// </summary>
    public IReadOnlyList<Joint3D> LinkJoints => _linkJoints;

    /// <summary>
    /// Gets the attachment point on the first link.
    /// </summary>
    public Marker3D StartAttachmentPoint => _startAttachmentPoint ?? throw new InvalidOperationException("Chain has not been built yet.");

    /// <summary>
    /// Gets the attachment point on the last link.
    /// </summary>
    public Marker3D EndAttachmentPoint => _endAttachmentPoint ?? throw new InvalidOperationException("Chain has not been built yet.");

    /// <summary>
    /// Gets the measured origin-to-origin pitch derived from the source link asset.
    /// </summary>
    public float LinkPitch => _linkMetrics?.Pitch ?? throw new InvalidOperationException("Chain has not been built yet.");

    /// <inheritdoc />
    public override void _Ready()
    {
        ResolveContainers();
        RebuildChainNow();
    }

    /// <summary>
    /// Rebuilds the entire chain immediately using current exported settings.
    /// </summary>
    public void RebuildChainNow()
    {
        _isRebuildQueued = false;

        ResolveContainers();
        ClearGeneratedContent();

        PackedScene linkScene = LoadChainLinkScene();
        LinkMetrics linkMetrics = MeasureLinkMetrics(linkScene);
        _linkMetrics = linkMetrics;

        BuildLinks(linkScene, linkMetrics);
        BuildLinkJoints(linkMetrics);
        BuildAttachmentPoints(linkMetrics);
        RefreshAttachmentJoints();
    }

    /// <summary>
    /// Attaches an external physics body to the first chain link.
    /// </summary>
    /// <param name="body">The physics body to attach.</param>
    /// <param name="bodyAnchor">Optional attachment node under <paramref name="body"/>.</param>
    /// <returns>The created joint.</returns>
    public Joint3D AttachStartBody(PhysicsBody3D body, Node3D? bodyAnchor = null)
    {
        StartAttachedBody = body;
        StartAttachedBodyAnchor = bodyAnchor;
        RefreshAttachmentJoints();

        return _startAttachmentJoint ?? throw new InvalidOperationException("Failed to create the start attachment joint.");
    }

    /// <summary>
    /// Attaches an external physics body to the final chain link.
    /// </summary>
    /// <param name="body">The physics body to attach.</param>
    /// <param name="bodyAnchor">Optional attachment node under <paramref name="body"/>.</param>
    /// <returns>The created joint.</returns>
    public Joint3D AttachEndBody(PhysicsBody3D body, Node3D? bodyAnchor = null)
    {
        EndAttachedBody = body;
        EndAttachedBodyAnchor = bodyAnchor;
        RefreshAttachmentJoints();

        return _endAttachmentJoint ?? throw new InvalidOperationException("Failed to create the end attachment joint.");
    }

    /// <summary>
    /// Removes any external physics body attached to the first chain link.
    /// </summary>
    public void DetachStartBody()
    {
        StartAttachedBody = null;
        StartAttachedBodyAnchor = null;
        RefreshAttachmentJoints();
    }

    /// <summary>
    /// Removes any external physics body attached to the final chain link.
    /// </summary>
    public void DetachEndBody()
    {
        EndAttachedBody = null;
        EndAttachedBodyAnchor = null;
        RefreshAttachmentJoints();
    }

    /// <summary>
    /// Returns the global position of the generated front attachment offset for a link.
    /// </summary>
    public Vector3 GetLinkFrontAttachmentGlobalPosition(int linkIndex)
        => GetLinkBody(linkIndex).ToGlobal(RequireLinkMetrics().FrontAttachmentLocalPosition);

    /// <summary>
    /// Returns the global position of the generated back attachment offset for a link.
    /// </summary>
    public Vector3 GetLinkBackAttachmentGlobalPosition(int linkIndex)
        => GetLinkBody(linkIndex).ToGlobal(RequireLinkMetrics().BackAttachmentLocalPosition);

    private void QueueRebuild()
    {
        if (!IsInsideTree())
        {
            return;
        }

        if (_isRebuildQueued)
        {
            return;
        }

        _isRebuildQueued = true;
        _ = CallDeferred(MethodName.RebuildChainNow);
    }

    private void ResolveContainers()
    {
        _linksContainer = this.RequireNode<Node3D>(LinksContainerPath);
        _jointsContainer = this.RequireNode<Node3D>(JointsContainerPath);
        _attachmentJointsContainer = this.RequireNode<Node3D>(AttachmentJointsContainerPath);
    }

    private void ClearGeneratedContent()
    {
        ClearContainer(_attachmentJointsContainer!);
        ClearContainer(_jointsContainer!);
        ClearContainer(_linksContainer!);

        _startAttachmentPoint = null;
        _endAttachmentPoint = null;
        _startAttachmentJoint = null;
        _endAttachmentJoint = null;

        _linkBodies.Clear();
        _linkJoints.Clear();
        _linkMetrics = null;
    }

    private static void ClearContainer(Node container)
    {
        foreach (Node child in container.GetChildren())
        {
            container.RemoveChild(child);
            child.QueueFree();
        }
    }

    private static PackedScene LoadChainLinkScene()
        => ResourceLoader.Load<PackedScene>(ChainLinkScenePath)
           ?? throw new InvalidOperationException($"Failed to load chain-link scene at '{ChainLinkScenePath}'.");

    private RigidBody3D GetLinkBody(int linkIndex)
        => linkIndex < 0 || linkIndex >= _linkBodies.Count
            ? throw new ArgumentOutOfRangeException(nameof(linkIndex), linkIndex, "Link index is outside the generated chain range.")
            : _linkBodies[linkIndex];

    private LinkMetrics RequireLinkMetrics()
        => _linkMetrics ?? throw new InvalidOperationException("Chain has not been built yet.");

    private void BuildLinks(PackedScene linkScene, LinkMetrics linkMetrics)
    {
        float linkPitch = linkMetrics.Pitch + LinkGapAdjustment;

        for (int linkIndex = 0; linkIndex < LinkCount; linkIndex++)
        {
            RigidBody3D linkBody = linkScene.Instantiate<RigidBody3D>();
            linkBody.Name = $"Link{linkIndex + 1:00}";
            linkBody.Mass = LinkMass;
            linkBody.LinearDamp = 0.05f;
            linkBody.AngularDamp = 0.05f;

            Transform3D localTransform = Transform3D.Identity;
            localTransform.Origin = new Vector3(0f, 0f, -linkPitch * linkIndex);

            if ((linkIndex & 1) == 1)
            {
                localTransform.Basis = localTransform.Basis.Rotated(Vector3.Forward, Mathf.Pi * 0.5f);
            }

            linkBody.Transform = localTransform;

            _linksContainer!.AddChild(linkBody);
            _linkBodies.Add(linkBody);
        }
    }

    private void BuildLinkJoints(LinkMetrics linkMetrics)
    {
        for (int linkIndex = 0; linkIndex < _linkBodies.Count - 1; linkIndex++)
        {
            RigidBody3D currentLink = _linkBodies[linkIndex];
            RigidBody3D nextLink = _linkBodies[linkIndex + 1];

            Vector3 anchorPosition = currentLink.ToGlobal(linkMetrics.BackAttachmentLocalPosition);
            PinJoint3D joint = CreatePinJoint(
                $"Joint{linkIndex + 1:00}_{linkIndex + 2:00}",
                anchorPosition);

            _jointsContainer!.AddChild(joint);
            ConfigureJointBodies(joint, currentLink, nextLink);
            _linkJoints.Add(joint);

            currentLink.AddCollisionExceptionWith(nextLink);
            nextLink.AddCollisionExceptionWith(currentLink);
        }
    }

    private void BuildAttachmentPoints(LinkMetrics linkMetrics)
    {
        RigidBody3D firstLink = _linkBodies[0];
        RigidBody3D lastLink = _linkBodies[^1];

        _startAttachmentPoint = CreateAttachmentPoint(
            firstLink,
            StartAttachmentPointName,
            linkMetrics.FrontAttachmentLocalPosition);

        _endAttachmentPoint = CreateAttachmentPoint(
            lastLink,
            EndAttachmentPointName,
            linkMetrics.BackAttachmentLocalPosition);
    }

    private static Marker3D CreateAttachmentPoint(RigidBody3D parentBody, string name, Vector3 localPosition)
    {
        Marker3D marker = new()
        {
            Name = name,
            Position = localPosition,
        };

        parentBody.AddChild(marker);
        return marker;
    }

    private void RefreshAttachmentJointsIfReady()
    {
        if (!IsInsideTree() || _linkBodies.Count == 0)
        {
            return;
        }

        RefreshAttachmentJoints();
    }

    private void RefreshAttachmentJoints()
    {
        if (_attachmentJointsContainer is null || _linkBodies.Count == 0 || _startAttachmentPoint is null || _endAttachmentPoint is null)
        {
            return;
        }

        ClearContainer(_attachmentJointsContainer);
        _startAttachmentJoint = null;
        _endAttachmentJoint = null;

        if (StartAttachedBody is not null)
        {
            _startAttachmentJoint = CreateAttachmentJoint(
                "StartAttachmentJoint",
                _startAttachmentPoint,
                _linkBodies[0],
                StartAttachedBody,
                StartAttachedBodyAnchor);

            _attachmentJointsContainer.AddChild(_startAttachmentJoint);
            ConfigureJointBodies(_startAttachmentJoint, _linkBodies[0], StartAttachedBody);
        }

        if (EndAttachedBody is not null)
        {
            _endAttachmentJoint = CreateAttachmentJoint(
                "EndAttachmentJoint",
                _endAttachmentPoint,
                _linkBodies[^1],
                EndAttachedBody,
                EndAttachedBodyAnchor);

            _attachmentJointsContainer.AddChild(_endAttachmentJoint);
            ConfigureJointBodies(_endAttachmentJoint, _linkBodies[^1], EndAttachedBody);
        }
    }

    private Joint3D CreateAttachmentJoint(
        string jointName,
        Marker3D chainAttachmentPoint,
        RigidBody3D chainBody,
        PhysicsBody3D externalBody,
        Node3D? externalBodyAnchor)
    {
        Node3D resolvedAnchor = ResolveExternalBodyAnchor(externalBody, externalBodyAnchor);
        SnapBodyAnchorToChainAttachment(externalBody, resolvedAnchor, chainAttachmentPoint.GlobalPosition);

        chainBody.AddCollisionExceptionWith(externalBody);
        externalBody.AddCollisionExceptionWith(chainBody);

        return CreatePinJoint(
            jointName,
            chainAttachmentPoint.GlobalPosition);
    }

    private static Node3D ResolveExternalBodyAnchor(PhysicsBody3D externalBody, Node3D? externalBodyAnchor)
    {
        if (externalBodyAnchor is null)
        {
            return externalBody;
        }

        _ = !externalBody.IsAncestorOf(externalBodyAnchor) && !ReferenceEquals(externalBody, externalBodyAnchor)
            ? throw new InvalidOperationException(
                $"Attachment anchor '{externalBodyAnchor.GetPath()}' must be the target physics body or one of its descendants.")
            : 0;

        return externalBodyAnchor;
    }

    private static void SnapBodyAnchorToChainAttachment(
        PhysicsBody3D externalBody,
        Node3D externalBodyAnchor,
        Vector3 targetAttachmentPosition)
    {
        Vector3 translationOffset = targetAttachmentPosition - externalBodyAnchor.GlobalPosition;
        externalBody.GlobalPosition += translationOffset;
    }

    private PinJoint3D CreatePinJoint(
        string jointName,
        Vector3 anchorPosition)
    {
        PinJoint3D joint = new()
        {
            Name = jointName,
            Position = ToLocal(anchorPosition),
            ExcludeNodesFromCollision = true,
        };

        joint.SetParam(PinJoint3D.Param.Damping, JointDamping);
        joint.SetParam(PinJoint3D.Param.ImpulseClamp, 0f);

        return joint;
    }

    private static void ConfigureJointBodies(Joint3D joint, PhysicsBody3D bodyA, PhysicsBody3D bodyB)
    {
        joint.NodeA = bodyA.IsInsideTree() ? bodyA.GetPath() : joint.GetPathTo(bodyA);
        joint.NodeB = bodyB.IsInsideTree() ? bodyB.GetPath() : joint.GetPathTo(bodyB);
    }

    private static LinkMetrics MeasureLinkMetrics(PackedScene linkScene)
    {
        RigidBody3D prototype = linkScene.Instantiate<RigidBody3D>();

        try
        {
            MeshInstance3D meshInstance = FindFirstMeshInstance(prototype)
                ?? throw new InvalidOperationException($"Chain-link scene '{ChainLinkScenePath}' has no MeshInstance3D to measure.");

            AabbInLinkSpace bounds = MeasureMeshBoundsInLinkSpace(prototype, meshInstance);
            float pitch = bounds.MaxZ - bounds.MinZ;
            _ = pitch <= 0f
                ? throw new InvalidOperationException($"Chain-link scene '{ChainLinkScenePath}' produced a degenerate Z extent.")
                : 0f;

            return new LinkMetrics(
                new Vector3(0f, 0f, bounds.MaxZ),
                new Vector3(0f, 0f, bounds.MinZ),
                pitch);
        }
        finally
        {
            prototype.QueueFree();
        }
    }

    private static MeshInstance3D? FindFirstMeshInstance(Node node)
    {
        if (node is MeshInstance3D meshInstance)
        {
            return meshInstance;
        }

        foreach (Node child in node.GetChildren())
        {
            MeshInstance3D? resolvedChild = FindFirstMeshInstance(child);
            if (resolvedChild is not null)
            {
                return resolvedChild;
            }
        }

        return null;
    }

    private static AabbInLinkSpace MeasureMeshBoundsInLinkSpace(RigidBody3D linkRoot, MeshInstance3D meshInstance)
    {
        if (meshInstance.Mesh is null)
        {
            throw new InvalidOperationException($"Mesh instance '{meshInstance.Name}' in '{ChainLinkScenePath}' has no mesh resource.");
        }

        Aabb meshBounds = meshInstance.Mesh.GetAabb();
        Transform3D meshToLink = ComputeTransformToAncestor(meshInstance, linkRoot);

        float minZ = float.PositiveInfinity;
        float maxZ = float.NegativeInfinity;

        foreach (Vector3 corner in EnumerateAabbCorners(meshBounds))
        {
            Vector3 cornerInLinkSpace = meshToLink * corner;
            minZ = Math.Min(minZ, cornerInLinkSpace.Z);
            maxZ = Math.Max(maxZ, cornerInLinkSpace.Z);
        }

        return new AabbInLinkSpace(minZ, maxZ);
    }

    private static Transform3D ComputeTransformToAncestor(Node3D node, Node3D ancestor)
    {
        Transform3D transformToAncestor = Transform3D.Identity;
        Node3D? current = node;

        while (current is not null && !ReferenceEquals(current, ancestor))
        {
            transformToAncestor = current.Transform * transformToAncestor;
            current = current.GetParent() as Node3D;
        }

        _ = !ReferenceEquals(current, ancestor)
            ? throw new InvalidOperationException(
                $"Node '{node.Name}' is not a descendant of ancestor '{ancestor.Name}' while measuring chain-link bounds.")
            : 0;

        return transformToAncestor;
    }

    private static IEnumerable<Vector3> EnumerateAabbCorners(Aabb bounds)
    {
        Vector3 position = bounds.Position;
        Vector3 size = bounds.Size;

        yield return position;
        yield return position + new Vector3(size.X, 0f, 0f);
        yield return position + new Vector3(0f, size.Y, 0f);
        yield return position + new Vector3(0f, 0f, size.Z);
        yield return position + new Vector3(size.X, size.Y, 0f);
        yield return position + new Vector3(size.X, 0f, size.Z);
        yield return position + new Vector3(0f, size.Y, size.Z);
        yield return position + size;
    }

    private readonly record struct LinkMetrics(
        Vector3 FrontAttachmentLocalPosition,
        Vector3 BackAttachmentLocalPosition,
        float Pitch);

    private readonly record struct AabbInLinkSpace(float MinZ, float MaxZ);
}
