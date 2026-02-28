using System.Reactive.Linq;
using AlleyCat.Common;
using Godot;

namespace AlleyCat.Locomotion.Rotation;

public class SmoothTurnCalculator(
    AngularVelocity maxTurnRate
) : IRotationCalculator
{
    public IObservable<Quaternion> ObserveRequests(IObservable<TurnRequest> request) =>
        request.Select(x =>
        {
            var input = x.Input.X;
            var timeDelta = (float)x.TimeDelta.Seconds;

            var angle = -maxTurnRate.Radians * timeDelta * input;
            var rotation = new Vector3(0, angle, 0);

            return Quaternion.FromEuler(rotation);
        });
}