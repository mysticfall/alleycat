using AlleyCat.Interaction.Physical;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Applies explicit capped hand interaction forces to overlapping dynamic rigid bodies.
/// </summary>
public sealed class HandDynamicBodyInteractionController(
    AnimatableBody3D handBody,
    IReadOnlyList<HandDynamicInteractionShape>? profileQueryShapes = null)
    : IImpactPhysicalInteractionSource
{
    private const float Epsilon = 1e-5f;
    private const float QueryMargin = 0.001f;
    private const int MaximumContactsPerShape = 8;
    private const string RuntimeShapeOwnerRevisionMetaKey = "alleycat_runtime_shape_owner_revision";
    // TODO: Profile and generated movement shapes may duplicate query sources; consider deduplicating.

    /// <summary>
    /// Default minimum approach speed before the impact channel may fire.
    /// </summary>
    public const float DefaultImpactApproachSpeedThreshold = 0.9f;

    /// <summary>
    /// Default impact impulse gain applied per metre-per-second of approach speed.
    /// </summary>
    public const float DefaultImpactImpulsePerSpeed = 0.08f;

    /// <summary>
    /// Default maximum impact impulse magnitude.
    /// </summary>
    public const float DefaultImpactImpulseCap = 0.30f;

    /// <summary>
    /// Default minimum pressing speed before the sustained push channel may fire.
    /// </summary>
    public const float DefaultSustainedPushSpeedThreshold = 0.08f;

    /// <summary>
    /// Default sustained push-force gain applied per metre-per-second of pressing speed.
    /// </summary>
    public const float DefaultSustainedForcePerSpeed = 8.0f;

    /// <summary>
    /// Default maximum sustained push-force magnitude.
    /// </summary>
    public const float DefaultSustainedForceCap = 4.5f;

    /// <summary>
    /// Scene-tree group used by dynamic rigid bodies that should receive explicit hand interaction.
    /// </summary>
    public const string DynamicInteractionGroupName = "hand_dynamic_interaction_body";

    /// <summary>
    /// Scene-tree group marking dynamic rigid bodies that are the pending grab candidate of an approaching
    /// hand. While protected, the explicit interaction channel must not push them: the authorised approach
    /// itself may not displace the item it is trying to acquire (INTR-002 pending approach protection).
    /// </summary>
    public const string ApproachProtectedBodyGroupName = "hand_approach_protected_body";

    private readonly Dictionary<ulong, ContactState> _contacts = [];

    private Vector3 _previousTargetOrigin = Vector3.Zero;
    private bool _hasPreviousTargetOrigin;
    private Vector3 _velocity = Vector3.Zero;

    /// <summary>
    /// Initialises explicit dynamic-body interaction handling for a hand actuator body.
    /// </summary>
    private readonly AnimatableBody3D _handBody = handBody;
    private QueryShapeSource[] _queryShapeSources = CollectQueryShapeSources(handBody, profileQueryShapes);
    private ShapeCast3D[] _shapeCasts = CreateQueryShapeCasts(
        handBody,
        CollectQueryShapeSources(handBody, profileQueryShapes));
    private int _runtimeShapeOwnerRevision = ReadRuntimeShapeOwnerRevision(handBody);

    /// <inheritdoc />
    public IReadOnlySet<string> Tags { get; } = BuildSourceTags(handBody);

    /// <summary>
    /// Collision mask queried for explicit hand-to-dynamic-body interaction.
    /// </summary>
    public uint CollisionMask
    {
        get; set;
    } = 2;

    /// <summary>
    /// Minimum approach speed before the impact channel may fire.
    /// </summary>
    public float ImpactApproachSpeedThreshold
    {
        get; set;
    } = DefaultImpactApproachSpeedThreshold;

    /// <summary>
    /// Impact impulse gain applied per metre-per-second of approach speed.
    /// </summary>
    public float ImpactImpulsePerSpeed
    {
        get; set;
    } = DefaultImpactImpulsePerSpeed;

    /// <summary>
    /// Maximum impact impulse magnitude.
    /// </summary>
    public float ImpactImpulseCap
    {
        get; set;
    } = DefaultImpactImpulseCap;

    /// <summary>
    /// Minimum pressing speed before the sustained push channel may fire.
    /// </summary>
    public float SustainedPushSpeedThreshold
    {
        get; set;
    } = DefaultSustainedPushSpeedThreshold;

    /// <summary>
    /// Sustained push-force gain applied per metre-per-second of pressing speed.
    /// </summary>
    public float SustainedForcePerSpeed
    {
        get; set;
    } = DefaultSustainedForcePerSpeed;

    /// <summary>
    /// Maximum sustained push-force magnitude.
    /// </summary>
    public float SustainedForceCap
    {
        get; set;
    } = DefaultSustainedForceCap;

    /// <inheritdoc />
    public Vector3 Velocity => _velocity;

    /// <summary>
    /// Updates dynamic-body interactions for the current hand pose.
    /// </summary>
    public void Update(Transform3D targetTransform, double delta)
    {
        RefreshRuntimeShapeOwnersIfChanged();
        float deltaSeconds = (float)delta;
        Vector3 targetVelocity = BuildTargetVelocity(targetTransform.Origin, deltaSeconds);

        if (deltaSeconds <= Epsilon || CollisionMask == 0 || _queryShapeSources.Length == 0 || !_handBody.IsInsideTree())
        {
            _contacts.Clear();
            return;
        }

        if (_handBody.GetTree() is null)
        {
            _contacts.Clear();
            return;
        }

        Dictionary<ulong, ActiveContact> activeContacts = QueryActiveContacts(targetVelocity);

        foreach ((ulong contactId, ActiveContact contact) in activeContacts)
        {
            ApplyInteraction(contact.Body, contactId, targetVelocity, contact.Normal, contact.Point);
        }

        ClearEndedContacts([.. activeContacts.Keys]);
    }

    /// <summary>
    /// Marks runtime shape-owner changes so dynamic interaction query shapes are rebuilt once at the ownership boundary.
    /// </summary>
    internal static void NotifyRuntimeShapeOwnersChanged(CollisionObject3D body)
    {
        int revision = ReadRuntimeShapeOwnerRevision(body);
        body.SetMeta(RuntimeShapeOwnerRevisionMetaKey, revision + 1);
    }

    private void RefreshRuntimeShapeOwnersIfChanged()
    {
        int revision = ReadRuntimeShapeOwnerRevision(_handBody);
        if (revision == _runtimeShapeOwnerRevision)
        {
            return;
        }

        _runtimeShapeOwnerRevision = revision;
        foreach (ShapeCast3D shapeCast in _shapeCasts)
        {
            if (GodotObject.IsInstanceValid(shapeCast))
            {
                shapeCast.QueueFree();
            }
        }

        _queryShapeSources = CollectQueryShapeSources(_handBody, profileQueryShapes);
        _shapeCasts = CreateQueryShapeCasts(_handBody, _queryShapeSources);
        _contacts.Clear();
    }

    private static int ReadRuntimeShapeOwnerRevision(CollisionObject3D body)
        => body.HasMeta(RuntimeShapeOwnerRevisionMetaKey)
            ? body.GetMeta(RuntimeShapeOwnerRevisionMetaKey).AsInt32()
            : 0;

    /// <summary>
    /// Computes the capped impact impulse magnitude for an explicit hand contact.
    /// </summary>
    public static float ComputeImpactImpulseMagnitude(
        float approachSpeed,
        bool hadContact,
        float impactApproachSpeedThreshold,
        float impactImpulsePerSpeed,
        float impactImpulseCap)
        => hadContact || approachSpeed < impactApproachSpeedThreshold
            ? 0.0f
            : Mathf.Min(impactImpulseCap, impactImpulsePerSpeed * approachSpeed);

    /// <summary>
    /// Computes the capped sustained push-force magnitude for an explicit hand contact.
    /// </summary>
    public static float ComputeSustainedForceMagnitude(
        float approachSpeed,
        float sustainedPushSpeedThreshold,
        float sustainedForcePerSpeed,
        float sustainedForceCap)
        => approachSpeed < sustainedPushSpeedThreshold
            ? 0.0f
            : Mathf.Min(sustainedForceCap, sustainedForcePerSpeed * approachSpeed);

    /// <summary>
    /// Resolves the positive pressing speed into a contacted body along the contact normal.
    /// </summary>
    public static float ComputePressSpeed(Vector3 targetVelocity, Vector3 contactNormal)
    {
        if (targetVelocity.LengthSquared() <= Epsilon * Epsilon || contactNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return 0.0f;
        }

        Vector3 contactNormalUnit = contactNormal.Normalized();
        return Mathf.Max(0.0f, -targetVelocity.Dot(contactNormalUnit));
    }

    private Dictionary<ulong, ActiveContact> QueryActiveContacts(Vector3 targetVelocity)
    {
        Dictionary<ulong, ActiveContact> activeContacts = [];

        for (int shapeIndex = 0; shapeIndex < _queryShapeSources.Length; shapeIndex += 1)
        {
            foreach (QueryContact contact in QueryContactsForShape(shapeIndex))
            {
                ulong contactId = contact.Body.GetInstanceId();
                float pressSpeed = ComputePressSpeed(targetVelocity, contact.Normal);
                if (!activeContacts.TryGetValue(contactId, out ActiveContact existingContact)
                    || pressSpeed > ComputePressSpeed(targetVelocity, existingContact.Normal))
                {
                    activeContacts[contactId] = new ActiveContact(contact.Body, contact.Normal, contact.Point);
                }
            }
        }

        return activeContacts;
    }

    private IEnumerable<QueryContact> QueryContactsForShape(int shapeIndex)
    {
        if (!_queryShapeSources[shapeIndex].TryResolve(out Shape3D? shape, out Transform3D transform))
        {
            yield break;
        }

        ShapeCast3D shapeCast = _shapeCasts[shapeIndex];
        if (!GodotObject.IsInstanceValid(shapeCast))
        {
            yield break;
        }

        shapeCast.CollisionMask = CollisionMask;
        shapeCast.Shape = shape;
        shapeCast.Transform = transform;
        shapeCast.Margin = QueryMargin;
        shapeCast.TargetPosition = Vector3.Zero;
        shapeCast.ForceShapecastUpdate();

        int collisionCount = Mathf.Min(shapeCast.GetCollisionCount(), MaximumContactsPerShape);
        for (int contactIndex = 0; contactIndex < collisionCount; contactIndex += 1)
        {
            if (TryBuildQueryContact(shapeCast, contactIndex, CollisionMask, out QueryContact contact))
            {
                yield return contact;
            }
        }
    }

    private void ApplyInteraction(RigidBody3D rigidBody, ulong contactId, Vector3 targetVelocity, Vector3 contactNormal, Vector3 contactPoint)
    {
        float pressSpeed = ComputePressSpeed(targetVelocity, contactNormal);
        bool hadContact = _contacts.TryGetValue(contactId, out ContactState state) && state.IsTouching;

        float impactImpulseMagnitude = ComputeImpactImpulseMagnitude(
            pressSpeed,
            hadContact,
            ImpactApproachSpeedThreshold,
            ImpactImpulsePerSpeed,
            ImpactImpulseCap);

        if (impactImpulseMagnitude > Epsilon)
        {
            rigidBody.Sleeping = false;
            rigidBody.ApplyCentralImpulse((-contactNormal).Normalized() * impactImpulseMagnitude);
            InteractWithReceiver(rigidBody, targetVelocity, contactPoint);
        }

        float sustainedForceMagnitude = ComputeSustainedForceMagnitude(
            pressSpeed,
            SustainedPushSpeedThreshold,
            SustainedForcePerSpeed,
            SustainedForceCap);

        if (sustainedForceMagnitude > Epsilon)
        {
            rigidBody.Sleeping = false;
            rigidBody.ApplyCentralForce((-contactNormal).Normalized() * sustainedForceMagnitude);
        }

        _contacts[contactId] = new ContactState(true);
    }

    private void InteractWithReceiver(RigidBody3D rigidBody, Vector3 targetVelocity, Vector3 contactPoint)
    {
        if (FindReceiver(rigidBody) is not { } receiver)
        {
            return;
        }

        _velocity = targetVelocity;
        if (receiver is RigidBodyImpactInteractionReceiver3D rigidBodyReceiver)
        {
            _ = rigidBodyReceiver.InteractWith(this, contactPoint);
            return;
        }

        _ = receiver.InteractWith(this);
    }

    private static IReadOnlySet<string> BuildSourceTags(AnimatableBody3D handBody)
    {
        SortedSet<string> tags = new(StringComparer.Ordinal) { "ImpactSource" };
        string handName = handBody.Name.ToString();
        if (!string.IsNullOrWhiteSpace(handName))
        {
            _ = tags.Add(handName);
        }

        return tags;
    }

    private static bool TryBuildQueryContact(
        ShapeCast3D shapeCast,
        int contactIndex,
        uint collisionMask,
        out QueryContact contact)
    {
        contact = default;

        if (shapeCast.GetCollider(contactIndex) is not RigidBody3D rigidBody
            || !GodotObject.IsInstanceValid(rigidBody)
            || rigidBody.Freeze
            || rigidBody.IsInGroup(ApproachProtectedBodyGroupName)
            || !rigidBody.IsInGroup(DynamicInteractionGroupName)
            || (rigidBody.CollisionLayer & collisionMask) == 0)
        {
            return false;
        }

        Vector3 contactNormal = shapeCast.GetCollisionNormal(contactIndex);
        if (contactNormal.LengthSquared() <= Epsilon * Epsilon)
        {
            return false;
        }

        contact = new QueryContact(rigidBody, contactNormal.Normalized(), shapeCast.GetCollisionPoint(contactIndex));
        return true;
    }

    private static IPhysicalInteractionReceiver? FindReceiver(Node node)
    {
        if (node is IPhysicalInteractionReceiver receiver)
        {
            return receiver;
        }

        foreach (Node child in node.GetChildren())
        {
            if (FindReceiver(child) is { } childReceiver)
            {
                return childReceiver;
            }
        }

        Node? parent = node.GetParent();
        return parent is IPhysicalInteractionReceiver parentReceiver ? parentReceiver : null;
    }

    private Vector3 BuildTargetVelocity(Vector3 targetOrigin, float deltaSeconds)
    {
        Vector3 targetVelocity = Vector3.Zero;
        if (_hasPreviousTargetOrigin && deltaSeconds > Epsilon)
        {
            targetVelocity = (targetOrigin - _previousTargetOrigin) / deltaSeconds;
        }

        _previousTargetOrigin = targetOrigin;
        _hasPreviousTargetOrigin = true;
        return targetVelocity;
    }

    private void ClearEndedContacts(HashSet<ulong> activeContacts)
    {
        if (_contacts.Count == 0)
        {
            return;
        }

        List<ulong> endedContactIds = [];
        foreach ((ulong contactId, _) in _contacts)
        {
            if (!activeContacts.Contains(contactId))
            {
                endedContactIds.Add(contactId);
            }
        }

        foreach (ulong contactId in endedContactIds)
        {
            _ = _contacts.Remove(contactId);
        }
    }

    private static QueryShapeSource[] CollectQueryShapeSources(
        AnimatableBody3D handBody,
        IReadOnlyList<HandDynamicInteractionShape>? profileQueryShapes)
    {
        List<QueryShapeSource> queryShapeSources = [];

        foreach (Node child in handBody.GetChildren())
        {
            if (child is CollisionShape3D collisionShape)
            {
                queryShapeSources.Add(new QueryShapeSource(collisionShape));
            }
        }

        foreach (int rawOwnerId in handBody.GetShapeOwners())
        {
            uint ownerId = (uint)rawOwnerId;
            GodotObject? owner = handBody.ShapeOwnerGetOwner(ownerId);
            if (owner is CollisionShape3D collisionShape && ReferenceEquals(collisionShape.GetParent(), handBody))
            {
                continue;
            }

            int shapeCount = handBody.ShapeOwnerGetShapeCount(ownerId);
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex += 1)
            {
                queryShapeSources.Add(new QueryShapeSource(handBody, ownerId, shapeIndex));
            }
        }

        if (profileQueryShapes is not null)
        {
            foreach (HandDynamicInteractionShape queryShape in profileQueryShapes)
            {
                queryShapeSources.Add(new QueryShapeSource(queryShape));
            }
        }

        return [.. queryShapeSources];
    }

    private static ShapeCast3D[] CreateQueryShapeCasts(AnimatableBody3D handBody, IReadOnlyList<QueryShapeSource> queryShapeSources)
    {
        List<ShapeCast3D> shapeCasts = [];

        for (int shapeIndex = 0; shapeIndex < queryShapeSources.Count; shapeIndex += 1)
        {
            ShapeCast3D shapeCast = new()
            {
                Name = $"DynamicInteractionShapeCast_{shapeIndex}",
                Enabled = false,
                ExcludeParent = true,
                CollideWithAreas = false,
                CollideWithBodies = true,
                TargetPosition = Vector3.Zero,
            };

            handBody.AddChild(shapeCast);
            shapeCasts.Add(shapeCast);
        }

        return [.. shapeCasts];
    }

    private readonly record struct ContactState(bool IsTouching);

    private readonly struct QueryShapeSource
    {
        private readonly CollisionShape3D? _collisionShape;
        private readonly CollisionObject3D? _shapeOwnerBody;
        private readonly uint _shapeOwnerId;
        private readonly int _shapeIndex;
        private readonly HandDynamicInteractionShape _queryShape;

        public QueryShapeSource(CollisionShape3D collisionShape)
        {
            _collisionShape = collisionShape;
            _shapeOwnerBody = null;
            _shapeOwnerId = 0;
            _shapeIndex = 0;
            _queryShape = default;
        }

        public QueryShapeSource(HandDynamicInteractionShape queryShape)
        {
            _collisionShape = null;
            _shapeOwnerBody = null;
            _shapeOwnerId = 0;
            _shapeIndex = 0;
            _queryShape = queryShape;
        }

        public QueryShapeSource(CollisionObject3D shapeOwnerBody, uint shapeOwnerId, int shapeIndex)
        {
            _collisionShape = null;
            _shapeOwnerBody = shapeOwnerBody;
            _shapeOwnerId = shapeOwnerId;
            _shapeIndex = shapeIndex;
            _queryShape = default;
        }

        public bool TryResolve(out Shape3D? shape, out Transform3D transform)
        {
            if (_collisionShape is not null)
            {
                shape = GodotObject.IsInstanceValid(_collisionShape) && !_collisionShape.Disabled
                    ? _collisionShape.Shape
                    : null;
                transform = _collisionShape.Transform;
                return shape is not null;
            }

            if (_shapeOwnerBody is not null)
            {
                bool valid = GodotObject.IsInstanceValid(_shapeOwnerBody)
                    && _shapeIndex < _shapeOwnerBody.ShapeOwnerGetShapeCount(_shapeOwnerId);
                shape = valid ? _shapeOwnerBody.ShapeOwnerGetShape(_shapeOwnerId, _shapeIndex) : null;
                transform = valid ? _shapeOwnerBody.ShapeOwnerGetTransform(_shapeOwnerId) : Transform3D.Identity;
                return shape is not null;
            }

            shape = _queryShape.Disabled ? null : _queryShape.Shape;
            transform = _queryShape.Transform;
            return shape is not null;
        }
    }

    private readonly record struct QueryContact(RigidBody3D Body, Vector3 Normal, Vector3 Point);

    private readonly record struct ActiveContact(RigidBody3D Body, Vector3 Normal, Vector3 Point);
}
