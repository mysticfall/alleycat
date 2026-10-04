using AlleyCat.Control.Locomotion;
using Godot;

namespace AlleyCat.Navigation;

/// <summary>Authored NPC AnimationTree state names and their catalogue capabilities.</summary>
public static class NPCAnimationProgramme
{
    /// <summary>The standing Idle state.</summary>
    public static readonly StringName IdleState = new("NPCIdle");

    /// <summary>The forward walking cycle.</summary>
    public static readonly StringName WalkForwardState = new("NPCWalkF");

    /// <summary>The backward walking cycle.</summary>
    public static readonly StringName WalkBackwardState = new("NPCWalkB");

    /// <summary>Independent native pivot-seek slots; canonical role mapping remains in <see cref="StateCapabilities"/>.</summary>
    public static readonly IReadOnlyDictionary<StringName, StandingLocomotionCapability> PivotContinuationStates =
        new Dictionary<StringName, StandingLocomotionCapability>
        {
            [new("NPCPivotLContinuation")] = StandingLocomotionCapability.ConfinedPivotLeft,
            [new("NPCPivotRContinuation")] = StandingLocomotionCapability.ConfinedPivotRight,
        };

    /// <summary>One discrete native-speed state per locomotion capability. Idle has no locomotion capability.</summary>
    public static readonly IReadOnlyDictionary<StringName, StandingLocomotionCapability> StateCapabilities =
        new Dictionary<StringName, StandingLocomotionCapability>
        {
            [WalkForwardState] = StandingLocomotionCapability.CyclicForwardPrimary,
            [WalkBackwardState] = StandingLocomotionCapability.CyclicBackwardPrimary,
            [new("NPCArcLL")] = StandingLocomotionCapability.CyclicArcLeftLeftLane,
            [new("NPCArcLR")] = StandingLocomotionCapability.CyclicArcLeftRightLane,
            [new("NPCArcRL")] = StandingLocomotionCapability.CyclicArcRightLeftLane,
            [new("NPCArcRR")] = StandingLocomotionCapability.CyclicArcRightRightLane,
            [new("NPCI2FL")] = StandingLocomotionCapability.EntryForwardLeft,
            [new("NPCI2FR")] = StandingLocomotionCapability.EntryForwardRight,
            [new("NPCI2BL")] = StandingLocomotionCapability.EntryBackwardLeft,
            [new("NPCI2BR")] = StandingLocomotionCapability.EntryBackwardRight,
            [new("NPCF2IL")] = StandingLocomotionCapability.ExitForwardLeft,
            [new("NPCF2IR")] = StandingLocomotionCapability.ExitForwardRight,
            [new("NPCB2IL")] = StandingLocomotionCapability.ExitBackwardLeft,
            [new("NPCB2IR")] = StandingLocomotionCapability.ExitBackwardRight,
            [new("NPCPivotL")] = StandingLocomotionCapability.ConfinedPivotLeft,
            [new("NPCPivotR")] = StandingLocomotionCapability.ConfinedPivotRight,
            [new("NPCSideStepL")] = StandingLocomotionCapability.ConfinedSideStepLeft,
            [new("NPCSideStepR")] = StandingLocomotionCapability.ConfinedSideStepRight,
        };

    /// <summary>States every NPC AnimationTree must author.</summary>
    public static readonly StringName[] RequiredStates = [IdleState, .. StateCapabilities.Keys, .. PivotContinuationStates.Keys];
}
