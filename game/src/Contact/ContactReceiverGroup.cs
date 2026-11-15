namespace AlleyCat.Contact;

/// <summary>
/// The scene-tree group marking physics bodies eligible to receive contact detection (INTR-005 TR1, TR10):
/// generated character body parts join through their generating rig, and world surfaces opt in by joining the
/// group directly. Any dedicated collision-layer arrangement behind sweep queries is an implementation detail;
/// the group is the normative receiver-eligibility contract and character and world receivers share one delivery
/// path through it (TR11).
/// </summary>
public static class ContactReceiverGroup
{
    /// <summary>The scene-tree group name identifying receiver-eligible physics bodies.</summary>
    public const string Name = "contact_receiver";
}
