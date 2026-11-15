using Godot;

namespace AlleyCat.Contact;

/// <summary>
/// Reusable resource that queries authored sub-part contact regions from a source scene (INTR-005 TR16).
/// </summary>
/// <remarks>
/// <para>
/// <strong>INTR-005 TR18c — final choice.</strong> Regions are authored through a sibling resource alongside
/// the game's <c>BodyColliderProfile</c> (AlleyCat.Rigging.Physics), sharing the same source scene and
/// bone-resolution workflow rather than extending the collider profile itself: this profile carries its own
/// <see cref="SourceScene" /> export, and authors point it at the same scene the collider profile uses. Each
/// <see cref="ContactRegion" /> marker resolves its anchor bone through its closest ancestor
/// <see cref="BoneAttachment3D" /> (RIG-001 TR6), exactly as collider shapes do, and its authored volume is
/// read in that attachment's frame — the bone-local space the <see cref="ContactMonitor" /> resolves contact
/// points into.
/// </para>
/// <para>
/// <strong>Annotation only.</strong> Querying instantiates the source scene transiently and frees it; no
/// physics body, collision shape, or collision layer/mask presence is ever generated for a region. Unlike the
/// collider profile's shape query, a scene with no region markers is not an error — it is the legitimate "no
/// sub-part refinement" authoring, and contacts stay at body-part granularity. Malformed markers — a missing
/// bone attachment, a blank bone name or region name, or non-positive extents — fail fast as scene-authoring
/// bugs.
/// </para>
/// </remarks>
[GlobalClass]
public partial class ContactRegionProfile : Resource
{
    /// <summary>
    /// Source authoring scene containing <see cref="ContactRegion" /> markers under bone attachments; authored
    /// alongside — typically the same scene as — the collider profile's source scene (INTR-005 TR18c).
    /// </summary>
    [Export]
    public PackedScene? SourceScene
    {
        get;
        set;
    }

    /// <summary>
    /// Instantiates the source scene and returns pure descriptors for every authored contact region.
    /// </summary>
    public IReadOnlyList<ContactRegionDefinition> QueryRegionDefinitions()
    {
        PackedScene sourceScene = SourceScene
                                   ?? throw new InvalidOperationException(
                                       $"{nameof(ContactRegionProfile)} '{ResourcePath}' requires a configured source scene.");

        Node sourceRoot = sourceScene.Instantiate();
        try
        {
            List<ContactRegionDefinition> definitions = [];
            CollectRegionDefinitions(sourceRoot, definitions);
            return definitions;
        }
        finally
        {
            sourceRoot.Free();
        }
    }

    private static void CollectRegionDefinitions(Node node, List<ContactRegionDefinition> definitions)
    {
        if (node is ContactRegion region)
        {
            definitions.Add(CreateDefinition(region));
        }

        foreach (Node child in node.GetChildren())
        {
            CollectRegionDefinitions(child, definitions);
        }
    }

    private static ContactRegionDefinition CreateDefinition(ContactRegion region)
    {
        BoneAttachment3D sourceBoneAttachment = ResolveSourceBoneAttachment(region);
        string boneName = sourceBoneAttachment.BoneName.ToString();
        if (string.IsNullOrWhiteSpace(boneName))
        {
            throw new InvalidOperationException(
                $"Contact region '{region.Name}' at '{region.GetPath()}' requires a {nameof(BoneAttachment3D)} " +
                "ancestor with a non-blank bone name.");
        }

        Transform3D boneLocalFrame = ComposeRegionFrameRelativeToAttachment(region, sourceBoneAttachment);
        Vector3 halfExtents = region.Scale.Abs() * region.HalfExtents;

        return new ContactRegionDefinition(
            region.RegionName,
            boneName,
            ContactVectors.ToNumerics(boneLocalFrame.Origin),
            ContactVectors.ToNumerics(halfExtents),
            ContactVectors.ToNumerics(boneLocalFrame.Basis.Orthonormalized().GetRotationQuaternion()),
            region.Priority,
            CollectTags(region));
    }

    private static IReadOnlyList<string> CollectTags(ContactRegion region)
    {
        List<string> tags = [];
        foreach (string tag in region.Tags)
        {
            if (!string.IsNullOrWhiteSpace(tag))
            {
                tags.Add(tag);
            }
        }

        return tags;
    }

    /// <summary>
    /// Composes the marker's frame relative to its bone attachment with the marker's own scale removed — scale
    /// folds into the half extents instead — so the result carries the bone-local centre and orientation.
    /// </summary>
    private static Transform3D ComposeRegionFrameRelativeToAttachment(ContactRegion region, BoneAttachment3D attachment)
    {
        Basis rotationOnly = new(region.Transform.Basis.GetRotationQuaternion());
        Transform3D transform = new(rotationOnly, region.Transform.Origin);

        for (Node? current = region.GetParent(); current is not null && current != attachment; current = current.GetParent())
        {
            if (current is Node3D current3D)
            {
                transform = current3D.Transform * transform;
            }
        }

        return transform;
    }

    private static BoneAttachment3D ResolveSourceBoneAttachment(ContactRegion region)
    {
        for (Node? current = region.GetParent(); current is not null; current = current.GetParent())
        {
            if (current is BoneAttachment3D sourceBoneAttachment)
            {
                return sourceBoneAttachment;
            }
        }

        throw new InvalidOperationException(
            $"Could not resolve contact region '{region.Name}' at '{region.GetPath()}' because it does not have a " +
            $"{nameof(BoneAttachment3D)} ancestor naming the anchor bone.");
    }
}
