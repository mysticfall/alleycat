namespace AlleyCat.Mind.Observation;

/// <summary>
/// Ground-plane reciprocal direction classification. Values classify where one participant lies relative to the
/// other's facing on the horizontal plane, so the same classification serves both the subject-to-observer and
/// observer-to-subject directions of one relative-position observation.
/// </summary>
public enum RelativeDirection
{
    /// <summary>The other participant lies directly ahead of the reference facing.</summary>
    Front,

    /// <summary>The other participant lies directly behind the reference facing.</summary>
    Back,

    /// <summary>The other participant lies to the left of the reference facing.</summary>
    Left,

    /// <summary>The other participant lies to the right of the reference facing.</summary>
    Right,
}
