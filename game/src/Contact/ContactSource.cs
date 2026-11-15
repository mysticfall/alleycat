using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AlleyCat.Core.Logging;
using AlleyCat.Interaction.Physical;
using Godot;
using Microsoft.Extensions.Logging;
using NumericsVector3 = System.Numerics.Vector3;

namespace AlleyCat.Contact;

/// <summary>
/// A contact sweeper: the engine-mechanics component that discovers receiver-eligible bodies for one
/// contacting counterpart, such as a player XR hand or an NPC hand (INTR-005 TR2, TR13). Each physics tick
/// the sweeper moves its query shapes — the tracked body's collision shapes plus any explicit
/// <see cref="QueryShapes" /> — through the physics space, coalesces every hit against one receiver into a
/// single observation (TR18b), and delivers it to the receiver itself: generated character body parts
/// receive the observation directly and forward it to their character's classifier hub, and world
/// receivers feed the monitor they host on their own body (TR11). The sweeper owns no delivery or payload
/// semantics: it measures contact geometry and the counterpart's velocity only; receiver-side velocity
/// sampling belongs to the monitor (TR18f).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Query shapes.</strong> The tracked body's own <see cref="CollisionShape3D" /> children and shape
/// owners are swept by default; <see cref="QueryShapes" /> adds explicit query-only geometry for tracked
/// bodies that carry no physical shapes of their own — the resolved NPC hand sweeper mechanics (TR18e):
/// profile-backed query shapes on the NPC IK hand target, swept each physics tick without registering any
/// collider on that target.
/// </para>
/// <para>
/// <strong>Receiver eligibility.</strong> A body is receiver-eligible when it is in the
/// <see cref="ContactReceiverGroup" /> group (TR10); the collision mask only narrows the physics query and is
/// an implementation detail, so it defaults to every layer.
/// </para>
/// <para>
/// <strong>Self-pair handling (INTR-005 TR12).</strong> The sweeper never reports a receiver excluded via
/// <see cref="ExcludedReceivers" /> — the RIG-001 self-collision exception set of the tracked limb. Every
/// other receiver on the same character reports through the same path as external contact, so self-contact
/// is sensed wherever the RIG-001 topology lets the pair physically co-exist.
/// </para>
/// <para>
/// <strong>Penetration depth.</strong> Because kinematic receivers report no contacts, the sweeper measures
/// depth per hit with a retraction probe: the query shape is retracted along the hit normal by up to
/// <see cref="PenetrationProbeMetres" />, and a bisection of overlap checks finds the largest retraction at
/// which the receiver still overlaps — the penetration past first touch along the normal. Depths saturate at
/// the probe length.
/// </para>
/// <para>
/// <strong>INTR-004 source capability (INTR-005 TR14).</strong> The sweeper exposes
/// <see cref="IPhysicalInteractionSource" /> and
/// <see cref="IImpactPhysicalInteractionSource" /> as read-only capability:
/// <see cref="Tags" /> attribute the contacting counterpart and <see cref="Velocity" /> reports the
/// counterpart velocity measured on the last completed sweep. Actor-end interaction initiation stays with
/// INTR-004-owned components, which may pass the sweeper as the interaction source; this sweeper itself
/// performs no INTR-004 delivery.
/// </para>
/// </remarks>
[GlobalClass]
public partial class ContactSource : Node3D, IImpactPhysicalInteractionSource
{
    private const float Epsilon = 1e-5f;
    private const float QueryMargin = 0.001f;
    private const int MaximumContactsPerShape = 16;
    private const int ProbeIterationCount = 5;

    /// <inheritdoc/>
    [ExportGroup("Identity")]
    [Export]
    public string PartyDisplayName { get; set; } = string.Empty;

    /// <summary>Owning character's name for counterpart attribution; empty when the counterpart is unowned.</summary>
    [Export]
    public string OwnerName { get; set; } = string.Empty;

    /// <inheritdoc/>
    [ExportGroup("Query")]
    [Export]
    public PhysicsBody3D? TrackedBody
    {
        get; set;
    }

    /// <summary>Collision mask narrowing the sweep query; eligibility itself rides the receiver group (TR10).</summary>
    [Export(PropertyHint.Layers3DPhysics)]
    public uint CollisionMask { get; set; } = uint.MaxValue;

    /// <summary>
    /// Explicit sweep query shapes swept in addition to the tracked body's own collision shapes (INTR-005
    /// TR13, TR18b): pure query geometry that registers no collider on the tracked body. Profile-backed NPC
    /// hand sweepers carry their hand bone's authored collider shapes here (TR18e — the resolved NPC hand
    /// sweeper mechanics), so sensing the hand never changes what it physically collides with. Shape
    /// resources resolve live each sweep; array membership changes rebuild the query set.
    /// </summary>
    [Export]
    public ContactQueryShape[] QueryShapes { get; set; } = [];

    /// <summary>
    /// Bodies this sweeper must never deliver as receivers: the RIG-001 self-collision exception set of its
    /// tracked limb (INTR-005 TR12). A hand sweeper carries its own hand, lower-arm, and same-side finger
    /// proxies — exactly the bodies the rig's adjacent-bone and same-side exceptions (RIG-001 TR12, TR13) and
    /// the hand actuator's own exceptions (RIG-001 TR15) keep out of physical contact — so self pairs that
    /// cannot physically co-exist in contact never report, while co-existing self pairs such as the hand
    /// against its own chest still deliver as self-contact. Shape queries do not consult physics collision
    /// exceptions, so the set is supplied explicitly by the wiring that resolves the limb's proxies. Freed
    /// entries are ignored; the owning wiring re-supplies the set after a rig rebuild, mirroring how physics
    /// exceptions are re-applied on regeneration.
    /// </summary>
    [Export]
    public PhysicsBody3D[] ExcludedReceivers { get; set; } = [];

    /// <summary>Retraction distance the penetration probe sweeps along each hit normal, in metres.</summary>
    [Export]
    public float PenetrationProbeMetres { get; set; } = 0.05f;

    private ILogger<ContactSource>? _logger;

    private QueryShape[] _queryShapes = [];

    private ShapeCast3D[] _shapeCasts = [];

    private ulong[] _cachedChildShapeIds = [];

    private ulong[] _cachedOwnerSignatures = [];

    private ContactQueryShape[] _cachedExportedQueryShapes = [];

    private Vector3 _previousOrigin;

    private ulong _previousOriginPhysicsFrame;

    private bool _hasPreviousOrigin;

    private readonly Dictionary<ulong, ReceiverHits> _receiverHits = [];

    private readonly List<ReceiverHits> _activeReceivers = [];

    private readonly List<PhysicsBody3D> _excludedBodiesScratch = [];

    private readonly List<NumericsVector3> _breadthPointsScratch = [];

    private ContactParty? _sourceParty;

    private IReadOnlySet<string>? _sourceTags;

    private Vector3 _counterpartVelocity;

    /// <summary>Number of query shapes the sweeper last swept — a testing and diagnostics surface.</summary>
    public int QueryShapeCountForTesting => _queryShapes.Length;

    /// <summary>Number of receiver-eligible bodies the sweeper discovered on its last completed sweep.</summary>
    public int LastSweepReceiverCountForTesting
    {
        get;
        private set;
    }

    /// <summary>
    /// Source tags attributing the contacting counterpart for INTR-004 actor-end interaction (INTR-005
    /// TR14): the impact-source marker plus the sweeper's authored party display name.
    /// </summary>
    public IReadOnlySet<string> Tags => ResolveSourceTags();

    /// <summary>
    /// The counterpart velocity measured on the last completed sweep — the finite difference of the tracked
    /// body's world origin (INTR-005 TR14). Zero before the first sweep and on the first sweep after a gap
    /// in physics processing, where the stale origin sample is re-based instead of differenced.
    /// </summary>
    public Vector3 Velocity => _counterpartVelocity;

    /// <inheritdoc />
    public override void _Ready()
    {
        _logger = GameLoggerResolver.ResolveRequired<ContactSource>();
        _logger.LogDebug("Contact source '{SourceName}' ready.", Name);
    }

    /// <inheritdoc />
    public override void _ExitTree() => DestroyShapeCasts();

    /// <inheritdoc />
    public override void _PhysicsProcess(double delta) => Sweep(delta);

    private void Sweep(double delta)
    {
        PhysicsBody3D? body = ResolveTrackedBody();
        if (body is null)
        {
            _hasPreviousOrigin = false;
            _activeReceivers.Clear();
            PruneReceiverCachesWhenIdle();
            return;
        }

        RefreshQueryShapesIfStale(body);

        float deltaSeconds = (float)delta;
        Vector3 origin = body.GlobalPosition;
        ulong physicsFrame = Engine.GetPhysicsFrames();

        // The first sweep after a gap in physics processing — a stopped sweeper whose tracked body moved —
        // must not difference the stale previous-origin sample over one tick: the spike could cross the
        // impact threshold and classify a spurious Impact on resume. The first sweep after a gap re-bases
        // the sample and reports no velocity, which the stream tolerates as a zero-velocity frame.
        bool previousOriginIsConsecutive = _hasPreviousOrigin && physicsFrame - _previousOriginPhysicsFrame == 1;

        Vector3 counterpartVelocity = Vector3.Zero;
        if (previousOriginIsConsecutive && deltaSeconds > Epsilon)
        {
            counterpartVelocity = (origin - _previousOrigin) / deltaSeconds;
        }

        _previousOrigin = origin;
        _previousOriginPhysicsFrame = physicsFrame;
        _hasPreviousOrigin = true;
        _counterpartVelocity = counterpartVelocity;

        if (CollisionMask == 0 || _queryShapes.Length == 0 || !body.IsInsideTree())
        {
            _activeReceivers.Clear();
            PruneReceiverCachesWhenIdle();
            return;
        }

        RefreshExcludedBodies();
        CollectReceiverHits();
        DeliverObservations(body, counterpartVelocity);
        LastSweepReceiverCountForTesting = _activeReceivers.Count;
        PruneReceiverCachesWhenIdle();
    }

    private PhysicsBody3D? ResolveTrackedBody()
    {
        return TrackedBody is not null
            ? IsInstanceValid(TrackedBody) ? TrackedBody : null
            : GetParent() is PhysicsBody3D parent && IsInstanceValid(parent) ? parent : null;
    }

    /// <summary>
    /// Refreshes the reusable exclusion buffer from <see cref="ExcludedReceivers" />, dropping freed bodies so
    /// a rig rebuild cannot leave phantom exclusions behind. Rebuilt per sweep without allocation; empty for
    /// sweepers without self-pair exclusions.
    /// </summary>
    private void RefreshExcludedBodies()
    {
        _excludedBodiesScratch.Clear();
        foreach (PhysicsBody3D excluded in ExcludedReceivers)
        {
            if (IsInstanceValid(excluded))
            {
                _excludedBodiesScratch.Add(excluded);
            }
        }
    }

    private bool IsExcludedReceiver(PhysicsBody3D receiver)
    {
        foreach (PhysicsBody3D excluded in _excludedBodiesScratch)
        {
            if (excluded == receiver)
            {
                return true;
            }
        }

        return false;
    }

    private void AddExcludedReceiverExceptions(ShapeCast3D shapeCast)
    {
        foreach (PhysicsBody3D excluded in _excludedBodiesScratch)
        {
            shapeCast.AddException(excluded);
        }
    }

    private void CollectReceiverHits()
    {
        foreach (ReceiverHits receiver in _activeReceivers)
        {
            receiver.Points.Clear();
            receiver.Normals.Clear();
            receiver.ShapeIndices.Clear();
        }

        _activeReceivers.Clear();

        for (int shapeIndex = 0; shapeIndex < _queryShapes.Length; shapeIndex += 1)
        {
            if (!_queryShapes[shapeIndex].TryResolve(out Shape3D? shape, out Transform3D localTransform))
            {
                continue;
            }

            ShapeCast3D? shapeCast = ResolveShapeCast(shapeIndex);
            if (shapeCast is null)
            {
                continue;
            }

            shapeCast.CollisionMask = CollisionMask;
            shapeCast.Shape = shape;
            shapeCast.Transform = localTransform;
            shapeCast.Margin = QueryMargin;
            shapeCast.TargetPosition = Vector3.Zero;
            shapeCast.ClearExceptions();
            AddExcludedReceiverExceptions(shapeCast);
            shapeCast.ForceShapecastUpdate();

            int collisionCount = Mathf.Min(shapeCast.GetCollisionCount(), MaximumContactsPerShape);
            for (int collisionIndex = 0; collisionIndex < collisionCount; collisionIndex += 1)
            {
                if (shapeCast.GetCollider(collisionIndex) is not PhysicsBody3D receiver
                    || !IsInstanceValid(receiver)
                    || !receiver.IsInGroup(ContactReceiverGroup.Name)
                    || IsExcludedReceiver(receiver))
                {
                    continue;
                }

                Vector3 normal = shapeCast.GetCollisionNormal(collisionIndex);
                if (normal.LengthSquared() <= Epsilon * Epsilon)
                {
                    continue;
                }

                ReceiverHits hits = GetOrCreateReceiverHits(receiver);
                hits.Points.Add(shapeCast.GetCollisionPoint(collisionIndex));
                hits.Normals.Add(normal.Normalized());
                hits.ShapeIndices.Add(shapeIndex);
            }
        }
    }

    private void DeliverObservations(PhysicsBody3D sweepBody, Vector3 counterpartVelocity)
    {
        ContactParty sourceParty = ResolveSourceParty();

        foreach (ReceiverHits receiver in _activeReceivers)
        {
            if (receiver.Body is IContactObservationReceiver part)
            {
                // Character body parts are the raw-event origin (INTR-005 TR10, TR11): the observation goes
                // to the receiver part itself, which forwards it to its character's classifier hub — the
                // sweeper never searches for a monitor.
                ContactSweepObservation observation = BuildObservation(sweepBody, receiver, counterpartVelocity);
                part.ObserveContact(sourceParty, in observation);
                continue;
            }

            // World receivers host their own monitor on the opted-in body (TR10, TR11): the body is its own
            // raw-event origin feeding the monitor it hosts. Receiver-eligible bodies that neither forward
            // through the part seam nor host a monitor — for example the migrated mirror-room props — are
            // not contact-delivered this tick.
            ContactMonitor? monitor = FindSelfHostedMonitor(receiver.Body);
            if (monitor is null)
            {
                continue;
            }

            ContactSweepObservation worldObservation = BuildObservation(sweepBody, receiver, counterpartVelocity);
            monitor.ObserveContact(sourceParty, receiver.Body, null, in worldObservation);
        }
    }

    /// <summary>
    /// Finds the monitor a world receiver hosts on its own body: a direct <see cref="ContactMonitor" />
    /// child of the opted-in receiver body (INTR-005 TR10, TR11). Character body parts never reach this
    /// path — they forward through <see cref="IContactObservationReceiver" />.
    /// </summary>
    private static ContactMonitor? FindSelfHostedMonitor(PhysicsBody3D receiverBody)
    {
        int childCount = receiverBody.GetChildCount();
        for (int childIndex = 0; childIndex < childCount; childIndex += 1)
        {
            if (receiverBody.GetChild(childIndex) is ContactMonitor monitor)
            {
                return monitor;
            }
        }

        return null;
    }

    private ContactSweepObservation BuildObservation(
        PhysicsBody3D sweepBody,
        ReceiverHits receiver,
        Vector3 counterpartVelocity)
    {
        int hitCount = receiver.Points.Count;

        float deepestDepth = 0f;
        int primaryIndex = 0;
        float totalX = 0f;
        float totalY = 0f;
        float totalZ = 0f;

        for (int hitIndex = 0; hitIndex < hitCount; hitIndex += 1)
        {
            float depth = ProbePenetrationDepth(sweepBody, receiver, hitIndex);
            if (depth > deepestDepth)
            {
                deepestDepth = depth;
                primaryIndex = hitIndex;
            }

            Vector3 point = receiver.Points[hitIndex];
            totalX += point.X;
            totalY += point.Y;
            totalZ += point.Z;
        }

        _breadthPointsScratch.Clear();
        for (int hitIndex = 0; hitIndex < hitCount; hitIndex += 1)
        {
            _breadthPointsScratch.Add(ContactVectors.ToNumerics(receiver.Points[hitIndex]));
        }

        Vector3 contactPoint = hitCount > 0
            ? new Vector3(totalX / hitCount, totalY / hitCount, totalZ / hitCount)
            : receiver.Body.GlobalPosition;

        return new ContactSweepObservation(
            ContactPoint: ContactVectors.ToNumerics(contactPoint),
            ContactNormal: ContactVectors.ToNumerics(receiver.Normals[primaryIndex]),
            CounterpartVelocity: ContactVectors.ToNumerics(counterpartVelocity),
            PenetrationDepth: deepestDepth,
            ContactBreadth: ContactMath.AggregateContactBreadth(
                CollectionsMarshal.AsSpan(_breadthPointsScratch)));
    }

    /// <summary>
    /// Measures one hit's penetration depth with a retraction probe: the query shape is retracted along the hit
    /// normal and a bisection of overlap checks finds the largest retraction at which the receiver still
    /// overlaps — the depth past first touch along the normal. Other receivers detected this tick are excluded
    /// so their surfaces cannot answer the probe. Depths saturate at <see cref="PenetrationProbeMetres" />.
    /// </summary>
    private float ProbePenetrationDepth(PhysicsBody3D sweepBody, ReceiverHits receiver, int hitIndex)
    {
        float probeLength = PenetrationProbeMetres;
        if (probeLength <= Epsilon)
        {
            return 0f;
        }

        int shapeIndex = receiver.ShapeIndices[hitIndex];
        if (!_queryShapes[shapeIndex].TryResolve(out Shape3D? shape, out Transform3D localTransform))
        {
            return 0f;
        }

        ShapeCast3D? shapeCast = ResolveShapeCast(shapeIndex);
        if (shapeCast is null)
        {
            return 0f;
        }

        Vector3 hitNormal = receiver.Normals[hitIndex];

        // Bisection invariant: the receiver overlaps at a retraction of zero — the discovery query hit at the
        // current pose — and clears at a retraction of the full probe length.
        float low = 0f;
        float high = probeLength;
        if (ReceiverOverlapsAtRetraction(shapeCast, shape, localTransform, sweepBody, receiver, hitNormal, high))
        {
            return probeLength;
        }

        for (int iteration = 0; iteration < ProbeIterationCount; iteration += 1)
        {
            float middle = (low + high) * 0.5f;
            if (ReceiverOverlapsAtRetraction(shapeCast, shape, localTransform, sweepBody, receiver, hitNormal, middle))
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private bool ReceiverOverlapsAtRetraction(
        ShapeCast3D shapeCast,
        Shape3D shape,
        Transform3D localTransform,
        PhysicsBody3D sweepBody,
        ReceiverHits receiver,
        Vector3 hitNormal,
        float retractionMetres)
    {
        Transform3D shapeWorld = sweepBody.GlobalTransform * localTransform;
        // Retraction moves the shape outward along the receiver-surface normal — away from the receiver.
        Transform3D retractedWorld = new(shapeWorld.Basis, shapeWorld.Origin + (hitNormal * retractionMetres));

        shapeCast.CollisionMask = CollisionMask;
        shapeCast.Shape = shape;
        shapeCast.Transform = sweepBody.GlobalTransform.AffineInverse() * retractedWorld;
        shapeCast.Margin = QueryMargin;
        shapeCast.TargetPosition = Vector3.Zero;
        shapeCast.ClearExceptions();
        AddExcludedReceiverExceptions(shapeCast);
        foreach (ReceiverHits other in _activeReceivers)
        {
            if (other.Body != receiver.Body)
            {
                shapeCast.AddException(other.Body);
            }
        }

        shapeCast.ForceShapecastUpdate();

        int collisionCount = Mathf.Min(shapeCast.GetCollisionCount(), MaximumContactsPerShape);
        for (int collisionIndex = 0; collisionIndex < collisionCount; collisionIndex += 1)
        {
            if (shapeCast.GetCollider(collisionIndex) == receiver.Body)
            {
                return true;
            }
        }

        return false;
    }

    private ContactParty ResolveSourceParty()
    {
        if (_sourceParty is not null)
        {
            return _sourceParty;
        }

        string displayName = !string.IsNullOrWhiteSpace(PartyDisplayName) ? PartyDisplayName : Name.ToString();
        string ownerName = string.IsNullOrWhiteSpace(OwnerName) ? "unowned" : OwnerName;
        _sourceParty = new ContactParty($"{ownerName}:{displayName}:{GetInstanceId()}", displayName,
            string.IsNullOrWhiteSpace(OwnerName) ? null : OwnerName);
        return _sourceParty;
    }

    private IReadOnlySet<string> ResolveSourceTags()
    {
        _sourceTags ??= BuildSourceTags();
        return _sourceTags;
    }

    private IReadOnlySet<string> BuildSourceTags()
    {
        SortedSet<string> tags = new(StringComparer.Ordinal) { "ImpactSource" };
        string displayName = !string.IsNullOrWhiteSpace(PartyDisplayName) ? PartyDisplayName : Name.ToString();
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            _ = tags.Add(displayName);
        }

        return tags;
    }

    private ReceiverHits GetOrCreateReceiverHits(PhysicsBody3D receiver)
    {
        ulong receiverId = receiver.GetInstanceId();
        if (!_receiverHits.TryGetValue(receiverId, out ReceiverHits? hits))
        {
            hits = new ReceiverHits
            {
                Body = receiver,
            };
            _receiverHits[receiverId] = hits;
        }

        if (hits.Points.Count == 0)
        {
            _activeReceivers.Add(hits);
        }

        hits.Body = receiver;
        return hits;
    }

    private ShapeCast3D? ResolveShapeCast(int shapeIndex)
    {
        ShapeCast3D shapeCast = _shapeCasts[shapeIndex];
        return IsInstanceValid(shapeCast) ? shapeCast : null;
    }

    /// <summary>
    /// Rebuilds the query set when its membership changes. The comparison is structural, not a shape count:
    /// each <see cref="CollisionShape3D" /> child is tracked by instance id and each shape owner by owner id
    /// and shape count, so replacing a child node or re-owning shapes at a constant count — realistic for
    /// held-object sweepers whose shapes are swapped at runtime — is caught. Per-shape
    /// <see cref="Shape3D" /> resources resolve live every sweep (see <see cref="QueryShape.TryResolve" />),
    /// so a resource swap on an unchanged node or owner needs no rebuild. The exported
    /// <see cref="QueryShapes" /> array is compared by reference; mutating an entry's fields is picked up
    /// live, replacing entries rebuilds.
    /// </summary>
    private void RefreshQueryShapesIfStale(PhysicsBody3D body)
    {
        if (QueryShapeSourcesMatchCurrent(body))
        {
            return;
        }

        _cachedChildShapeIds = CollectChildShapeIds(body);
        _cachedOwnerSignatures = CollectOwnerSignatures(body);
        _cachedExportedQueryShapes = [.. QueryShapes];
        _queryShapes = CollectQueryShapes(body);
        DestroyShapeCasts();
        _shapeCasts = CreateShapeCasts(body, _queryShapes);
        _logger?.LogDebug(
            "Contact source '{SourceName}' rebuilt its query shapes: {ShapeCount} shapes.",
            Name,
            _queryShapes.Length);
    }

    /// <summary>Compares the tracked body's current query-shape sources against the cached snapshot.</summary>
    private bool QueryShapeSourcesMatchCurrent(PhysicsBody3D body)
    {
        int childIndex = 0;
        int childCount = body.GetChildCount();
        for (int childCandidate = 0; childCandidate < childCount; childCandidate += 1)
        {
            if (body.GetChild(childCandidate) is not CollisionShape3D collisionShape)
            {
                continue;
            }

            if (childIndex >= _cachedChildShapeIds.Length
                || _cachedChildShapeIds[childIndex] != collisionShape.GetInstanceId())
            {
                return false;
            }

            childIndex += 1;
        }

        if (childIndex != _cachedChildShapeIds.Length)
        {
            return false;
        }

        int ownerIndex = 0;
        foreach (int rawOwnerId in body.GetShapeOwners())
        {
            uint ownerId = (uint)rawOwnerId;
            ulong signature = PackOwnerSignature(ownerId, body.ShapeOwnerGetShapeCount(ownerId));
            if (ownerIndex >= _cachedOwnerSignatures.Length || _cachedOwnerSignatures[ownerIndex] != signature)
            {
                return false;
            }

            ownerIndex += 1;
        }

        if (ownerIndex != _cachedOwnerSignatures.Length)
        {
            return false;
        }

        if (QueryShapes.Length != _cachedExportedQueryShapes.Length)
        {
            return false;
        }

        for (int shapeIndex = 0; shapeIndex < QueryShapes.Length; shapeIndex += 1)
        {
            if (!ReferenceEquals(QueryShapes[shapeIndex], _cachedExportedQueryShapes[shapeIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private static ulong[] CollectChildShapeIds(PhysicsBody3D body)
    {
        List<ulong> shapeIds = [];
        int childCount = body.GetChildCount();
        for (int childIndex = 0; childIndex < childCount; childIndex += 1)
        {
            if (body.GetChild(childIndex) is CollisionShape3D collisionShape)
            {
                shapeIds.Add(collisionShape.GetInstanceId());
            }
        }

        return [.. shapeIds];
    }

    private static ulong[] CollectOwnerSignatures(PhysicsBody3D body)
    {
        List<ulong> signatures = [];
        foreach (int rawOwnerId in body.GetShapeOwners())
        {
            uint ownerId = (uint)rawOwnerId;
            signatures.Add(PackOwnerSignature(ownerId, body.ShapeOwnerGetShapeCount(ownerId)));
        }

        return [.. signatures];
    }

    private static ulong PackOwnerSignature(uint ownerId, int shapeCount)
        => ((ulong)ownerId << 32) | (uint)shapeCount;

    private QueryShape[] CollectQueryShapes(PhysicsBody3D body)
    {
        List<QueryShape> queryShapes = [];

        foreach (Node child in body.GetChildren())
        {
            if (child is CollisionShape3D collisionShape)
            {
                queryShapes.Add(new QueryShape(collisionShape));
            }
        }

        foreach (int rawOwnerId in body.GetShapeOwners())
        {
            uint ownerId = (uint)rawOwnerId;
            GodotObject? owner = body.ShapeOwnerGetOwner(ownerId);
            if (owner is CollisionShape3D ownerShape && ReferenceEquals(ownerShape.GetParent(), body))
            {
                continue;
            }

            int shapeCount = body.ShapeOwnerGetShapeCount(ownerId);
            for (int shapeIndex = 0; shapeIndex < shapeCount; shapeIndex += 1)
            {
                queryShapes.Add(new QueryShape(body, ownerId, shapeIndex));
            }
        }

        foreach (ContactQueryShape queryShape in QueryShapes)
        {
            queryShapes.Add(new QueryShape(queryShape));
        }

        return [.. queryShapes];
    }

    private static ShapeCast3D[] CreateShapeCasts(PhysicsBody3D body, IReadOnlyList<QueryShape> queryShapes)
    {
        List<ShapeCast3D> shapeCasts = [];

        for (int shapeIndex = 0; shapeIndex < queryShapes.Count; shapeIndex += 1)
        {
            ShapeCast3D shapeCast = new()
            {
                Name = $"ContactSweepShapeCast_{shapeIndex}",
                Enabled = false,
                ExcludeParent = true,
                CollideWithAreas = false,
                CollideWithBodies = true,
                TargetPosition = Vector3.Zero,
            };

            body.AddChild(shapeCast);
            shapeCasts.Add(shapeCast);
        }

        return [.. shapeCasts];
    }

    private void DestroyShapeCasts()
    {
        foreach (ShapeCast3D shapeCast in _shapeCasts)
        {
            if (IsInstanceValid(shapeCast))
            {
                shapeCast.QueueFree();
            }
        }

        _shapeCasts = [];
    }

    /// <summary>
    /// Releases per-receiver caches once no receiver was discovered this sweep: hit buffers are only
    /// meaningful within a contact's lifetime, and clearing between contacts bounds memory on receivers
    /// that come and go — mirroring <see cref="ContactMonitor" />'s idle pruning.
    /// </summary>
    private void PruneReceiverCachesWhenIdle()
    {
        if (_activeReceivers.Count > 0)
        {
            return;
        }

        if (_receiverHits.Count > 0)
        {
            _receiverHits.Clear();
        }
    }

    private sealed class ReceiverHits
    {
        public PhysicsBody3D Body
        {
            get;
            set;
        } = null!;

        public readonly List<Vector3> Points = [];

        public readonly List<Vector3> Normals = [];

        public readonly List<int> ShapeIndices = [];
    }

    private readonly struct QueryShape
    {
        private readonly CollisionShape3D? _collisionShape;
        private readonly CollisionObject3D? _shapeOwnerBody;
        private readonly ContactQueryShape? _authoredQueryShape;
        private readonly uint _shapeOwnerId;
        private readonly int _shapeIndex;

        public QueryShape(CollisionShape3D collisionShape)
        {
            _collisionShape = collisionShape;
            _shapeOwnerBody = null;
            _authoredQueryShape = null;
            _shapeOwnerId = 0;
            _shapeIndex = 0;
        }

        public QueryShape(CollisionObject3D shapeOwnerBody, uint shapeOwnerId, int shapeIndex)
        {
            _collisionShape = null;
            _shapeOwnerBody = shapeOwnerBody;
            _authoredQueryShape = null;
            _shapeOwnerId = shapeOwnerId;
            _shapeIndex = shapeIndex;
        }

        public QueryShape(ContactQueryShape authoredQueryShape)
        {
            _collisionShape = null;
            _shapeOwnerBody = null;
            _authoredQueryShape = authoredQueryShape;
            _shapeOwnerId = 0;
            _shapeIndex = 0;
        }

        public bool TryResolve([NotNullWhen(true)] out Shape3D? shape, out Transform3D transform)
        {
            if (_collisionShape is not null)
            {
                shape = IsInstanceValid(_collisionShape) && !_collisionShape.Disabled
                    ? _collisionShape.Shape
                    : null;
                transform = _collisionShape.Transform;
                return shape is not null;
            }

            if (_authoredQueryShape is not null)
            {
                shape = IsInstanceValid(_authoredQueryShape) && !_authoredQueryShape.Disabled
                    ? _authoredQueryShape.Shape
                    : null;
                transform = _authoredQueryShape.Transform;
                return shape is not null;
            }

            if (_shapeOwnerBody is not { } ownerBody || !IsInstanceValid(ownerBody))
            {
                shape = null;
                transform = Transform3D.Identity;
                return false;
            }

            bool valid = _shapeIndex < ownerBody.ShapeOwnerGetShapeCount(_shapeOwnerId);
            shape = valid ? ownerBody.ShapeOwnerGetShape(_shapeOwnerId, _shapeIndex) : null;
            transform = valid ? ownerBody.ShapeOwnerGetTransform(_shapeOwnerId) : Transform3D.Identity;
            return shape is not null;
        }
    }
}
