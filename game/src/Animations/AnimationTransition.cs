namespace AlleyCat.Animations;

/// <summary>The category pair a MotionBricks bridge connects (ANIM-003 TR15).</summary>
public enum AnimationTransitionKind
{
    /// <summary>From one catalogue idle to another.</summary>
    IdleToIdle,

    /// <summary>From a catalogue idle into locomotion.</summary>
    IdleToLocomotion,

    /// <summary>From locomotion into a catalogue idle.</summary>
    LocomotionToIdle,

    /// <summary>From one locomotion variant to another.</summary>
    LocomotionToLocomotion
}

/// <summary>
/// A transition that has started: its bridge (or, for a locomotion variant switch, the first segment of the new
/// variant) has begun playing on <paramref name="Subject" />. <paramref name="Category" /> and
/// <paramref name="Entry" /> name the transition target. Raised once per bridge; an interrupting request raises a new
/// event only when its target differs.
/// </summary>
public sealed record AnimationTransition(
    Character.Character Subject,
    AnimationCategory Category,
    AnimationEntry Entry,
    AnimationTransitionKind Kind);

/// <summary>
/// Variant animation control for a character that plays catalogue idles and locomotion (ANIM-003 TR10, TR15–TR22).
/// </summary>
public interface IAnimationVariantControl
{
    /// <summary>The idle currently playing or being bridged into, if any.</summary>
    AnimationEntry? CurrentIdle
    {
        get;
    }

    /// <summary>The locomotion variant that new locomotion windows sample.</summary>
    AnimationEntry? CurrentLocomotion
    {
        get;
    }

    /// <summary>
    /// The category the body is currently displaying (TR15): <see cref="AnimationCategory.Idle" /> during a native idle or
    /// a bridge into an idle, <see cref="AnimationCategory.Locomotion" /> while moving or in a bridge into locomotion, and
    /// <see langword="null" /> when neither applies (no catalogue, or a failed hold). The current entry of that category is
    /// <see cref="CurrentIdle" /> or <see cref="CurrentLocomotion" />.
    /// </summary>
    AnimationCategory? DisplayedCategory
    {
        get;
    }

    /// <summary>Raised when a transition bridge starts.</summary>
    event Action<AnimationTransition>? TransitionStarted;

    /// <summary>
    /// Sets the agent idle preference (<see langword="null" /> clears it). Returns <see langword="false" />, without
    /// changing anything, when the id names no compatible idle. A settled character bridges to the new effective idle;
    /// a moving one adopts it on arrival.
    /// </summary>
    bool SetIdlePreference(string? id);

    /// <summary>
    /// Sets the agent locomotion preference (<see langword="null" /> clears it). Returns <see langword="false" />,
    /// without changing anything, when the id names no compatible locomotion entry.
    /// </summary>
    bool SetLocomotionPreference(string? id);
}
