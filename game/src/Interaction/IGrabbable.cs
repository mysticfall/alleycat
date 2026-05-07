using AlleyCat.Common;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Contract for objects that can be discovered and grabbed by a character hand.
/// </summary>
public interface IGrabbable
{
    /// <summary>
    /// Queries the closest eligible grab point for the supplied hand transform and side.
    /// </summary>
    /// <param name="handTransform">The global transform of the querying hand.</param>
    /// <param name="handSide">The hand side performing the query.</param>
    /// <returns>
    /// The closest eligible grab target and animation for the supplied hand information, or <see langword="null" />
    /// when distance, angle, ownership, state, or other implementation-specific constraints reject every grab point.
    /// </returns>
    GrabPoint? GetGrabPoint(Transform3D handTransform, LimbSide handSide);

    /// <summary>
    /// Attempts to complete a grab using information from a prior successful query.
    /// </summary>
    /// <param name="grabPoint">The grab information returned by <see cref="GetGrabPoint" />.</param>
    /// <returns>
    /// <see langword="true" /> when the grab completes, or <see langword="false" /> when the object state or hand
    /// conditions have drifted since the query.
    /// </returns>
    bool Grab(GrabPoint grabPoint);
}
