using Godot;

namespace AlleyCat.Contact;

/// <summary>
/// One authored sweep query shape (INTR-005 TR13, TR18e): pure query geometry — a shape resource with its
/// local transform — that a <see cref="ContactSource" /> sweeps through the physics space without
/// registering any collider on the tracked body. Profile-backed NPC hand sweepers carry the hand bone's
/// authored collider shapes this way, so sensing a hand never changes what the hand physically collides
/// with.
/// </summary>
/// <remarks>
/// <para>
/// Shapes resolve live at sweep time: swapping the <see cref="Shape" /> resource or moving
/// <see cref="Transform" /> takes effect on the next physics tick without rebuilding the sweeper, matching
/// how the sweeper treats <see cref="CollisionShape3D" /> children of its tracked body.
/// </para>
/// <para>
/// Multiple query shapes of one sweeper against one receiver coalesce into a single contact whose breadth
/// aggregates the contributing shapes (INTR-005 TR18b).
/// </para>
/// </remarks>
[GlobalClass]
public partial class ContactQueryShape : Resource
{
    /// <summary>The swept shape geometry, in the tracked body's local space.</summary>
    [Export]
    public Shape3D? Shape
    {
        get;
        set;
    }

    /// <summary>Local transform of <see cref="Shape" /> relative to the tracked body.</summary>
    [Export]
    public Transform3D Transform
    {
        get;
        set;
    } = Transform3D.Identity;

    /// <summary>When set, this query shape is skipped by the sweep without rebuilding the sweeper.</summary>
    [Export]
    public bool Disabled
    {
        get;
        set;
    }
}
