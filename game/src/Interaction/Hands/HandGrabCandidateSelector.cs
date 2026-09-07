using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.Interaction.Hands;

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

            // Candidate-content admission is input-source-independent. A controller must not begin an approach for
            // content that optical recognition would reject, and a valid pathless Animation is sampled in place.
            if (!candidate.TryGetValidatedReference(out _, out _))
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
