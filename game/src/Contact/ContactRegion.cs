using Godot;

namespace AlleyCat.Contact;

/// <summary>
/// An authored sub-part contact region marker (INTR-005 TR16, TR18c): a named, tagged annotation volume placed
/// in a body-collider source scene under the <see cref="BoneAttachment3D" /> naming its anchor bone — for
/// example a "Right Cheek" box on the head attachment. The marker is annotation data only: it generates no
/// physics body, no collision shape, and no collision layer or mask presence; <see cref="ContactRegionProfile" />
/// reads these nodes into pure <see cref="ContactRegionDefinition" />s.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Volume authoring.</strong> The node's position and rotation place an oriented box in the anchor
/// bone's local space; <see cref="HalfExtents" /> sizes it in the node's own frame, and the node's scale
/// multiplies the extents componentwise (absolute value). Nothing about the volume participates in physics.
/// </para>
/// <para>
/// Authoring is validated when the profile queries the scene: blank names, non-positive extents, or a missing
/// <see cref="BoneAttachment3D" /> ancestor fail fast as scene-authoring bugs.
/// </para>
/// </remarks>
[GlobalClass]
public partial class ContactRegion : Node3D
{
    /// <summary>Authored region name reported on matches, such as "Right Cheek"; non-blank.</summary>
    [ExportGroup("Identity")]
    [Export]
    public string RegionName { get; set; } = string.Empty;

    /// <summary>
    /// Authored tags carried verbatim on region matches; blank entries are dropped at query time, matching the
    /// receiver-tag precedent on <c>PhysicalBodyPart3D</c>.
    /// </summary>
    [Export]
    public string[] Tags { get; set; } = [];

    /// <summary>Authored priority; the highest-priority matching region is the primary (INTR-005 TR18d).</summary>
    [ExportGroup("Volume")]
    [Export]
    public int Priority
    {
        get; set;
    }

    /// <summary>
    /// Half extents of the annotation box in this node's local space, in metres; every component positive.
    /// The node's scale multiplies these componentwise.
    /// </summary>
    [Export]
    public Vector3 HalfExtents { get; set; } = new(0.05f, 0.05f, 0.05f);
}
