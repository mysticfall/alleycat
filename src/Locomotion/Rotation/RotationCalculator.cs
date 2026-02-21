using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Rotation;

public interface IRotationCalculator
{
    IO<Quaternion> CalculateRotation(Vector2 input, Duration duration);
}