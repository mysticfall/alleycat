using Godot;

namespace AlleyCat.Interaction.Hands;

/// <summary>
/// Read-only handoff state published by the legitimate <see cref="HandPoseController"/> path for one optical grab.
/// </summary>
/// <remarks>
/// This deliberately exposes only the current publisher generation and exact reference, its intended effective
/// weight, and whether the AnimationTree has evaluated that exact state. The modifier must not inspect controller
/// transition internals or infer readiness from its own elapsed blend time.
/// </remarks>
internal readonly record struct HandPoseHandoffReadiness(
    long PublisherGeneration,
    Animation Reference,
    float IntendedEffectiveWeight,
    bool IsEvaluatedReady);
