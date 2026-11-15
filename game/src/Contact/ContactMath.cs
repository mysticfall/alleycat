using System.Numerics;

namespace AlleyCat.Contact;

/// <summary>
/// Pure measurement helpers shared by the raw contact stream and its sweeper-side callers (INTR-005 TR3,
/// TR18b). All functions are objective vector maths; no classification logic lives here.
/// </summary>
public static class ContactMath
{
    /// <summary>
    /// Returns the vector scaled to unit length, or <see cref="Vector3.Zero" /> when the input has no length.
    /// </summary>
    public static Vector3 Normalised(Vector3 vector)
    {
        float length = vector.Length();
        return length > 0f ? vector / length : Vector3.Zero;
    }

    /// <summary>
    /// Returns the normal closing speed of the counterpart against the receiver: the relative velocity
    /// projected onto the receiver-surface outward contact normal, negated so that approaching contact is
    /// positive, clamped at zero for separating contact.
    /// </summary>
    public static float NormalApproachSpeed(Vector3 relativeVelocity, Vector3 contactNormal) =>
        MathF.Max(0f, -Vector3.Dot(relativeVelocity, Normalised(contactNormal)));

    /// <summary>
    /// Returns the tangential slip speed: the magnitude of the relative velocity with its normal component
    /// removed — how fast the counterpart slides along the receiver's surface.
    /// </summary>
    public static float TangentialSpeed(Vector3 relativeVelocity, Vector3 contactNormal)
    {
        Vector3 normal = Normalised(contactNormal);
        Vector3 tangential = relativeVelocity - (Vector3.Dot(relativeVelocity, normal) * normal);
        return tangential.Length();
    }

    /// <summary>
    /// Aggregates the contact breadth of one coalesced contact from its contributing query-shape contact
    /// points: the maximum pairwise distance between any two points — the spatial extent diameter of the
    /// contact (INTR-005 TR18b). Fewer than two points yield zero breadth.
    /// </summary>
    public static float AggregateContactBreadth(ReadOnlySpan<Vector3> contactPoints)
    {
        float breadth = 0f;
        for (int i = 0; i < contactPoints.Length; i++)
        {
            for (int j = i + 1; j < contactPoints.Length; j++)
            {
                breadth = MathF.Max(breadth, Vector3.Distance(contactPoints[i], contactPoints[j]));
            }
        }

        return breadth;
    }
}
