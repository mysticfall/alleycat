namespace AlleyCat.Animations;

/// <summary>What the body presents when a new bridge is installed.</summary>
public enum BridgeSource
{
    /// <summary>A native catalogue idle clip owns the body.</summary>
    NativeIdle,

    /// <summary>A bridge between idles, with the actor held in place, is still playing.</summary>
    StationaryBridge,

    /// <summary>A bridge into or out of locomotion, with the actor travelling, is still playing.</summary>
    TravellingBridge,

    /// <summary>Ordinary locomotion, or a held pose after a NAV interruption.</summary>
    Locomotion
}

/// <summary>How a bridge is played (ANIM-003 TR18–TR20).</summary>
/// <param name="EaseIn">Apply the settle ease-in at the bridge start (bridges starting from a native idle).</param>
/// <param name="EaseOut">Apply the settle ease-out into the target idle's frame 0.</param>
/// <param name="Stationary">Hold the actor in place and present the generated hips in full.</param>
/// <param name="VelocityBlend">Blend from the still-moving outgoing presentation over a few frames.</param>
/// <param name="Kind">The category pair the bridge connects.</param>
public readonly record struct IdleBridgePlan(
    bool EaseIn,
    bool EaseOut,
    bool Stationary,
    bool VelocityBlend,
    AnimationTransitionKind Kind);

/// <summary>
/// Decides how a MotionBricks bridge into or out of a catalogue idle is played. Settle easing is limited to
/// catalogue-idle bridges (TR18); interruptions blend from the current presentation (TR20).
/// </summary>
public static class IdleBridgePlanner
{
    /// <summary>Plans a bridge.</summary>
    /// <param name="source">The presentation the bridge replaces.</param>
    /// <param name="from">The category the body was presenting or heading into.</param>
    /// <param name="to">The bridge target category.</param>
    /// <param name="travellingTarget">
    /// The idle target is placed away from the actor (a NAV arrival), so the bridge must carry the actor there even
    /// when it starts from an idle. Only an in-place idle change holds the actor.
    /// </param>
    public static IdleBridgePlan Plan(
        BridgeSource source,
        AnimationCategory from,
        AnimationCategory to,
        bool travellingTarget = false)
    {
        bool idleSource = source is BridgeSource.NativeIdle or BridgeSource.StationaryBridge;
        AnimationCategory origin = idleSource ? AnimationCategory.Idle : from;
        AnimationTransitionKind kind = (origin, to) switch
        {
            (AnimationCategory.Idle, AnimationCategory.Idle) => AnimationTransitionKind.IdleToIdle,
            (AnimationCategory.Idle, _) => AnimationTransitionKind.IdleToLocomotion,
            (_, AnimationCategory.Idle) => AnimationTransitionKind.LocomotionToIdle,
            _ => AnimationTransitionKind.LocomotionToLocomotion
        };
        return new IdleBridgePlan(
            EaseIn: source == BridgeSource.NativeIdle,
            EaseOut: to == AnimationCategory.Idle,
            Stationary: idleSource && to == AnimationCategory.Idle && !travellingTarget,
            VelocityBlend: source != BridgeSource.Locomotion,
            Kind: kind);
    }

    /// <summary>
    /// Whether a starting bridge announces a transition: always, unless it interrupts a bridge to the same target.
    /// </summary>
    public static bool Announces(AnimationEntry? interruptedTarget, AnimationEntry target)
        => interruptedTarget is null || !interruptedTarget.Equals(target);
}
