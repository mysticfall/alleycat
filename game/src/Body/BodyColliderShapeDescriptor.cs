using Godot;

namespace AlleyCat.Body;

/// <summary>
/// Immutable query descriptor for a collision shape authored under a source bone attachment.
/// </summary>
public sealed class BodyColliderShapeDescriptor(
    string sourceShapeName,
    StringName sourceBoneName,
    string sourceIdentifier,
    string sourcePhysicsBodyName,
    Shape3D shape,
    bool disabled,
    Transform3D localTransform,
    Transform3D sourceShapeFrameTransform)
{
    /// <summary>
    /// Name of the source <see cref="CollisionShape3D"/> node.
    /// </summary>
    public string SourceShapeName { get; } = sourceShapeName;

    /// <summary>
    /// Bone name read from the closest source <see cref="BoneAttachment3D"/> ancestor.
    /// </summary>
    public StringName SourceBoneName { get; } = sourceBoneName;

    /// <summary>
    /// Stable authoring identifier used for generated-node metadata.
    /// </summary>
    public string SourceIdentifier { get; } = sourceIdentifier;

    /// <summary>
    /// Name of the closest source physics body ancestor, retained for diagnostics and authoring audits.
    /// </summary>
    public string SourcePhysicsBodyName { get; } = sourcePhysicsBodyName;

    /// <summary>
    /// Original geometry resource referenced by the source shape. Query consumers must not mutate it in place.
    /// </summary>
    public Shape3D Shape { get; } = shape;

    /// <summary>
    /// Whether the source shape was disabled in the authoring scene.
    /// </summary>
    public bool Disabled { get; } = disabled;

    /// <summary>
    /// Shape transform relative to the closest source <see cref="BoneAttachment3D"/> ancestor.
    /// Retained for non-rig query consumers that need the authored hand/body local shape offset.
    /// </summary>
    public Transform3D LocalTransform { get; } = localTransform;

    /// <summary>
    /// Shape transform in the source skeleton/model frame retained for diagnostics and authoring audits.
    /// </summary>
    public Transform3D SourceShapeFrameTransform { get; } = sourceShapeFrameTransform;
}
