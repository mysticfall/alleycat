using Godot;

namespace AlleyCat.Contact;

/// <summary>
/// Bone-anchored receiver metadata for receiver-framed contact delivery (INTR-005 TR5, TR8, TR10): a receiver
/// body whose host anchors it to a skeleton bone exposes the bone frame so the monitor can re-express contact
/// direction in the bone-local space of the contacted body part, and names the body part for receiver contact
/// parties. Receiver bodies without a bone anchor fall back to the body's own frame and name.
/// </summary>
public interface IBoneAnchoredContactReceiver
{
    /// <summary>Skeleton whose bone this receiver body follows; <see langword="null" /> when unanchored.</summary>
    Skeleton3D? ContactSkeleton
    {
        get;
    }

    /// <summary>Skeleton bone index this receiver body follows; negative when unanchored.</summary>
    int ContactBoneIndex
    {
        get;
    }

    /// <summary>Human-readable body-part name carried by receiver contact parties.</summary>
    string ContactDisplayName
    {
        get;
    }
}
