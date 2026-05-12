namespace AlleyCat.Interaction;

/// <summary>
/// Defines whether a grabbable object follows the hand or constrains the hand while held.
/// </summary>
public enum GrabbableMobility
{
    /// <summary>
    /// Object can be carried and moved freely by the hand.
    /// </summary>
    Movable = 0,

    /// <summary>
    /// Object is fixed in place and constrains the hand to the grab point while held.
    /// </summary>
    Immovable = 1,
}
