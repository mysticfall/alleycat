using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Optional head-provider capability evaluated by CharacterIK at Begin, before head-target processing.
/// The sequence belongs to that CharacterIK instance; pose-only completion signals are not evaluations.
/// </summary>
public interface IIKBeginEvaluable
{
    /// <summary>Captures and evaluates intent once for the supplied skeleton evaluation.</summary>
    void EvaluateAtBegin(Skeleton3D skeleton, double delta, ulong sequence);
}
