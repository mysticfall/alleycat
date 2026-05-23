using AlleyCat.Body;
using AlleyCat.Core;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Component capability for an object-specific grab point that can evaluate and produce grab candidates.
/// </summary>
public interface IGrabPoint : IComponent
{
    /// <summary>
    /// Evaluates this grab point for the supplied hand and returns a candidate when reach, angle, and state allow it.
    /// </summary>
    /// <param name="handSide">The hand side performing the query.</param>
    /// <param name="handTransform">The global transform of the querying hand.</param>
    /// <returns>An eligible candidate, or <see langword="null" /> when this point cannot be used.</returns>
    GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform);

    /// <summary>
    /// Evaluates this grab point with an additional acquisition tolerance for already-pending grab refreshes.
    /// </summary>
    /// <param name="handSide">The hand side performing the query.</param>
    /// <param name="handTransform">The global transform of the querying hand.</param>
    /// <param name="acquisitionToleranceMetres">Additional reach tolerance in metres.</param>
    /// <returns>An eligible candidate, or <see langword="null" /> when this point cannot be used.</returns>
    GrabPointCandidate? GetGrabPoint(
        LimbSide handSide,
        Transform3D handTransform,
        float acquisitionToleranceMetres)
        => GetGrabPoint(handSide, handTransform);
}
