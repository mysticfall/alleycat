using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Rotation;

public readonly record struct TurnRequest(Vector2 Input, Duration TimeDelta);

public interface IRotationCalculator
{
    IObservable<Quaternion> ObserveRequests(IObservable<TurnRequest> request);
}