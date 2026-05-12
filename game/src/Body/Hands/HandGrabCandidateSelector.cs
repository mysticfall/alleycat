using AlleyCat.Interaction;
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

        float rangeSquared = discoveryRangeMetres * discoveryRangeMetres;
        HandGrabSelection? bestSelection = null;
        float bestDistanceSquared = float.PositiveInfinity;

        foreach (IGrabbable grabbable in grabbables)
        {
            GrabPointCandidate? candidate = grabbable.GetGrabPoint(side, handTransform);
            if (candidate is null)
            {
                continue;
            }

            float distanceSquared = handTransform.Origin.DistanceSquaredTo(candidate.HandTarget.Origin);
            if (distanceSquared > rangeSquared || distanceSquared >= bestDistanceSquared)
            {
                continue;
            }

            bestSelection = new HandGrabSelection(grabbable, candidate);
            bestDistanceSquared = distanceSquared;
        }

        return bestSelection;
    }
}

internal sealed record HandGrabSelection(IGrabbable Grabbable, GrabPointCandidate Candidate);
