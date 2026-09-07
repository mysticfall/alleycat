using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Value-only instruction returned by an <see cref="IKTargetIntentProvider" />.
/// </summary>
/// <param name="Intent">The requested world-space target transform and modifier influence.</param>
/// <param name="ShouldApplyTargetTransform">Whether the resolver should apply <paramref name="Intent" /> to its target.</param>
public readonly record struct IKTargetIntentResolution(
    IKTargetIntent Intent,
    bool ShouldApplyTargetTransform)
{
    /// <summary>
    /// Whether every numeric value supplied by this resolution is finite.
    /// </summary>
    public bool IsFinite => float.IsFinite(Intent.DesiredInfluence)
                            && IsFiniteVector(Intent.WorldTransform.Origin)
                            && IsFiniteVector(Intent.WorldTransform.Basis.Column0)
                            && IsFiniteVector(Intent.WorldTransform.Basis.Column1)
                            && IsFiniteVector(Intent.WorldTransform.Basis.Column2);

    private static bool IsFiniteVector(Vector3 value)
        => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
