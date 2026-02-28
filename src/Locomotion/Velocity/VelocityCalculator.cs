using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Velocity;

public readonly record struct MoveRequest(Vector2 Input, Duration TimeDelta);

public interface IVelocityCalculator
{
    IObservable<Vector3> ObserveRequests(IObservable<MoveRequest> request);
}