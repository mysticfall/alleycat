using Godot;

#pragma warning disable CS1591

namespace AlleyCat.Navigation;

/// <summary>One physics-tick accounting record for NPC planar movement composition.</summary>
public readonly record struct NPCMovementCompositionFrame(
    ulong PhysicsFrame,
    Vector3 RawRootDelta,
    Vector3 SemanticProjectedRootWorldDelta,
    Vector3 AuthoredRootWorldDelta,
    Vector3 ProgrammeDesiredWorldDelta,
    Vector3 ProgrammeDesiredContribution,
    Vector3 SyntheticWorldDelta,
    Vector3 ContactWorldDelta,
    Vector3 RequestedVelocity,
    Vector3 RequestedWorldDelta,
    Vector3 CollisionTravel,
    Vector3 CollisionRemainder,
    Vector3 RealisedWorldDelta,
    bool Collided)
{
    public Vector3 CompositionError => RequestedWorldDelta
        - AuthoredRootWorldDelta
        - ProgrammeDesiredContribution
        - SyntheticWorldDelta
        - ContactWorldDelta;

    public Vector3 RealisationError => RealisedWorldDelta - RequestedWorldDelta;
}

/// <summary>Pure inclusive-start/exclusive-end accumulator for identical-window movement accounting.</summary>
public readonly record struct NPCMovementCompositionTotals(
    int TickCount,
    Vector3 RawRoot,
    Vector3 SemanticProjectedRoot,
    Vector3 AuthoredRoot,
    Vector3 ProgrammeDesiredInput,
    Vector3 ProgrammeDesiredContribution,
    Vector3 Synthetic,
    Vector3 Contact,
    Vector3 Requested,
    Vector3 CollisionTravel,
    Vector3 CollisionRemainder,
    Vector3 Realised)
{
    public NPCMovementCompositionTotals Add(in NPCMovementCompositionFrame frame)
        => new(
            TickCount + 1,
            RawRoot + frame.RawRootDelta,
            SemanticProjectedRoot + frame.SemanticProjectedRootWorldDelta,
            AuthoredRoot + frame.AuthoredRootWorldDelta,
            ProgrammeDesiredInput + frame.ProgrammeDesiredWorldDelta,
            ProgrammeDesiredContribution + frame.ProgrammeDesiredContribution,
            Synthetic + frame.SyntheticWorldDelta,
            Contact + frame.ContactWorldDelta,
            Requested + frame.RequestedWorldDelta,
            CollisionTravel + frame.CollisionTravel,
            CollisionRemainder + frame.CollisionRemainder,
            Realised + frame.RealisedWorldDelta);

    public Vector3 CompositionError => Requested
        - AuthoredRoot
        - ProgrammeDesiredContribution
        - Synthetic
        - Contact;

    public Vector3 RealisationError => Realised - Requested;
}
