namespace AlleyCat.Animations;

/// <summary>
/// A skeleton modifier that presents the animated base pose, such as a native catalogue idle (ANIM-003 TR13). It is
/// placed first among the skeleton's modifiers; IK and other installed modifiers keep their order after it and must
/// not be inserted ahead of it.
/// </summary>
public interface IBasePoseModifier;
