namespace AlleyCat.Contact;

/// <summary>
/// The five contact classes emitted by the classification stage (INTR-005 TR6). Grab is deliberately absent:
/// grab is intentional by definition, expressed through the grab systems, and is never inferred by physics
/// heuristics (INTR-005 TR7).
/// </summary>
public enum ContactClass
{
    /// <summary>
    /// Impact/Hit: a strike, classified at contact Begin from approach kinematics — the approach normal speed
    /// at or above the policy's impact threshold.
    /// </summary>
    Impact,

    /// <summary>Touch: the gentle-begin default classification.</summary>
    Touch,

    /// <summary>Press: sustained or increasing normal pressure, confirmed after a press window.</summary>
    Press,

    /// <summary>
    /// Caress/Stroke: tangential motion confirmed after a tangential-motion window; may revise an earlier
    /// Touch classification, emitted as one revision transition.
    /// </summary>
    Caress,

    /// <summary>Rest/Lean: prolonged settled contact.</summary>
    Rest,
}
