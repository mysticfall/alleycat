using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Applies explicit capped hand interaction forces to overlapping dynamic rigid bodies.
/// </summary>
public sealed class HandDynamicBodyInteractionController(AnimatableBody3D handBody)
{
    private const float Epsilon = 1e-5f;
    private const float QueryMargin = 0.001f;
    private const int MaximumContactsPerShape = 8;

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

    private readonly Dictionary<ulong, ContactState> _contacts = [];

    private Vector3 _previousTargetOrigin = Vector3.Zero;
    private bool _hasPreviousTargetOrigin;

    /// <summary>
    /// Initialises explicit dynamic-body interaction handling for a hand follower body.
    /// </summary>
    private readonly AnimatableBody3D _handBody = handBody;
    private readonly CollisionShape3D[] _collisionShapes = CollectCollisionShapes(handBody);
    private readonly ShapeCast3D[] _shapeCasts = CreateQueryShapeCasts(handBody, CollectCollisionShapes(handBody));

    /// <summary>
    /// Collision mask queried for explicit hand-to-dynamic-body interaction.
    /// </summary>
    public uint CollisionMask { get; set; } = 2;

    /// <summary>
    /// Minimum approach speed before the impact channel may fire.
    /// </summary>
    public float ImpactApproachSpeedThreshold { get; set; } = DefaultImpactApproachSpeedThreshold;

    /// <summary>
    /// Impact impulse gain applied per metre-per-second of approach speed.
    /// </summary>
    public float ImpactImpulsePerSpeed { get; set; } = DefaultImpactImpulsePerSpeed;

    /// <summary>
    /// Maximum impact impulse magnitude.
    /// </summary>
    public float ImpactImpulseCap { get; set; } = DefaultImpactImpulseCap;

    /// <summary>
    /// Minimum pressing speed before the sustained push channel may fire.
    /// </summary>
    public float SustainedPushSpeedThreshold { get; set; } = DefaultSustainedPushSpeedThreshold;

    /// <summary>
    /// Sustained push-force gain applied per metre-per-second of pressing speed.
    /// </summary>
    public float SustainedForcePerSpeed { get; set; } = DefaultSustainedForcePerSpeed;

    /// <summary>
    /// Maximum sustained push-force magnitude.
    /// </summary>
    public float SustainedForceCap { get; set; } = DefaultSustainedForceCap;

    /// <summary>
    /// Updates dynamic-body interactions for the current hand pose.
    /// </summary>
    public void Update(Transform3D targetTransform, double delta)
    {
        float deltaSeconds = (float)delta;
        Vector3 targetVelocity = BuildTargetVelocity(targetTransform.Origin, deltaSeconds);

        if (deltaSeconds <= Epsilon || CollisionMask == 0 || _collisionShapes.Length == 0 || !_handBody.IsInsideTree())
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
            ApplyInteraction(contact.Body, contactId, targetVelocity, contact.Normal);
        }

        ClearEndedContacts([.. activeContacts.Keys]);
    }

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

        for (int shapeIndex = 0; shapeIndex < _collisionShapes.Length; shapeIndex += 1)
        {
            foreach (QueryContact contact in QueryContactsForShape(shapeIndex))
            {
                ulong contactId = contact.Body.GetInstanceId();
                float pressSpeed = ComputePressSpeed(targetVelocity, contact.Normal);
                if (!activeContacts.TryGetValue(contactId, out ActiveContact existingContact)
                    || pressSpeed > ComputePressSpeed(targetVelocity, existingContact.Normal))
                {
                    activeContacts[contactId] = new ActiveContact(contact.Body, contact.Normal);
                }
            }
        }

        return activeContacts;
    }

    private IEnumerable<QueryContact> QueryContactsForShape(int shapeIndex)
    {
        CollisionShape3D collisionShape = _collisionShapes[shapeIndex];
        if (!GodotObject.IsInstanceValid(collisionShape) || collisionShape.Disabled || collisionShape.Shape is null)
        {
            yield break;
        }

        ShapeCast3D shapeCast = _shapeCasts[shapeIndex];
        if (!GodotObject.IsInstanceValid(shapeCast))
        {
            yield break;
        }

        shapeCast.CollisionMask = CollisionMask;
        shapeCast.Shape = collisionShape.Shape;
        shapeCast.Transform = collisionShape.Transform;
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

    private void ApplyInteraction(RigidBody3D rigidBody, ulong contactId, Vector3 targetVelocity, Vector3 contactNormal)
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

        contact = new QueryContact(rigidBody, contactNormal.Normalized());
        return true;
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

    private static CollisionShape3D[] CollectCollisionShapes(AnimatableBody3D handBody)
    {
        List<CollisionShape3D> collisionShapes = [];

        foreach (Node child in handBody.GetChildren())
        {
            if (child is CollisionShape3D collisionShape)
            {
                collisionShapes.Add(collisionShape);
            }
        }

        return [.. collisionShapes];
    }

    private static ShapeCast3D[] CreateQueryShapeCasts(AnimatableBody3D handBody, IReadOnlyList<CollisionShape3D> collisionShapes)
    {
        List<ShapeCast3D> shapeCasts = [];

        for (int shapeIndex = 0; shapeIndex < collisionShapes.Count; shapeIndex += 1)
        {
            CollisionShape3D collisionShape = collisionShapes[shapeIndex];
            ShapeCast3D shapeCast = new()
            {
                Name = $"DynamicInteractionShapeCast_{shapeIndex}",
                Enabled = false,
                ExcludeParent = true,
                CollideWithAreas = false,
                CollideWithBodies = true,
                TargetPosition = Vector3.Zero,
                Shape = collisionShape.Shape,
                Transform = collisionShape.Transform,
            };

            handBody.AddChild(shapeCast);
            shapeCasts.Add(shapeCast);
        }

        return [.. shapeCasts];
    }

    private readonly record struct ContactState(bool IsTouching);

    private readonly record struct QueryContact(RigidBody3D Body, Vector3 Normal);

    private readonly record struct ActiveContact(RigidBody3D Body, Vector3 Normal);
}
