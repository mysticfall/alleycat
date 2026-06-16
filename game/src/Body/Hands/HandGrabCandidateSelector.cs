using AlleyCat.Interaction;
using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Body.Hands;

/// <summary>
/// Deterministic candidate selection helper for hand grab discovery.
/// </summary>
internal static class HandGrabCandidateSelector
{
    public static HandGrabSelection? Select(
        IEnumerable<IGrabbable> grabbables,
        LimbSide side,
        Transform3D handTransform,
        float discoveryRangeMetres)
    {
        if (discoveryRangeMetres <= 0.0f)
        {
            return null;
        }

        HandGrabSelection? bestSelection = null;
        float bestAcquisitionDistance = float.PositiveInfinity;

        foreach (IGrabbable grabbable in grabbables)
        {
            GrabPointCandidate? candidate = grabbable.GetGrabPoint(side, handTransform);
            if (candidate is null)
            {
                continue;
            }

            if (candidate.AcquisitionDistance > discoveryRangeMetres
                || candidate.AcquisitionDistance >= bestAcquisitionDistance)
            {
                continue;
            }

            bestSelection = new HandGrabSelection(grabbable, candidate);
            bestAcquisitionDistance = candidate.AcquisitionDistance;
        }

        return bestSelection;
    }
}

internal sealed record HandGrabSelection(IGrabbable Grabbable, GrabPointCandidate Candidate);
