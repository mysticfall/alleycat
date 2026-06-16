using AlleyCat.Core;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Interaction;

/// <summary>
/// Holder trait for objects that can be discovered and grabbed by a character hand through owned grab-point components.
/// </summary>
public interface IGrabbable : IComponentHolder
{
    /// <summary>
    /// Gets whether this grabbable follows the hand or constrains the hand while held.
    /// </summary>
    GrabbableMobility Mobility
    {
        get;
    }

    /// <summary>
    /// Queries owned grab-point components in deterministic holder order and returns the closest eligible candidate.
    /// </summary>
    /// <param name="handSide">The hand side performing the query.</param>
    /// <param name="handTransform">The global transform of the querying hand.</param>
    /// <returns>
    /// The closest eligible grab target and animation for the supplied hand information, or <see langword="null" />
    /// when distance, angle, ownership, state, or other implementation-specific constraints reject every grab point.
    /// </returns>
    GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform)
        => GetGrabPoint(handSide, handTransform, 0.0f);

    /// <summary>
    /// Queries owned grab-point components with additional acquisition tolerance for an already-pending grab.
    /// </summary>
    /// <param name="handSide">The hand side performing the query.</param>
    /// <param name="handTransform">The global transform of the querying hand.</param>
    /// <param name="acquisitionToleranceMetres">Additional reach tolerance in metres.</param>
    /// <returns>
    /// The closest eligible grab target and animation for the supplied hand information, or <see langword="null" />
    /// when distance, angle, ownership, state, or other implementation-specific constraints reject every grab point.
    /// </returns>
    GrabPointCandidate? GetGrabPoint(LimbSide handSide, Transform3D handTransform, float acquisitionToleranceMetres)
    {
        GrabPointCandidate? bestCandidate = null;
        float bestAcquisitionDistance = float.PositiveInfinity;
        float tolerance = Mathf.Max(0.0f, acquisitionToleranceMetres);

        foreach (IGrabPoint grabPoint in this.GetComponents<IGrabPoint>())
        {
            GrabPointCandidate? candidate = grabPoint.GetGrabPoint(handSide, handTransform, tolerance);
            if (candidate is null)
            {
                continue;
            }

            if (candidate.AcquisitionDistance < bestAcquisitionDistance)
            {
                bestCandidate = candidate;
                bestAcquisitionDistance = candidate.AcquisitionDistance;
            }
        }

        return bestCandidate;
    }

    /// <summary>
    /// Attempts to complete a grab using information from a prior successful query.
    /// </summary>
    /// <param name="grabPoint">The grab information returned by a grab-point query.</param>
    /// <returns>
    /// <see langword="true" /> when the grab completes, or <see langword="false" /> when the candidate source is no
    /// longer owned or valid, or the object state or hand conditions have drifted since the query.
    /// </returns>
    bool Grab(GrabPointCandidate grabPoint);
}
