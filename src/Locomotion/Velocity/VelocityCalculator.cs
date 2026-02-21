using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Velocity;

public interface IVelocityCalculator
{
    IO<Vector3> CalculateVelocity(Vector2 input, Duration duration);
}