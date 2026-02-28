using System.Reactive.Linq;
using AlleyCat.Common;
using Godot;
using LanguageExt;

namespace AlleyCat.Locomotion.Rotation;

public class SnapTurnCalculator(
    AngularVelocity maxTurnRate,
    Duration coolTime
) : IRotationCalculator
{
    public IObservable<Quaternion> ObserveRequests(IObservable<TurnRequest> request) =>
        request.Timestamp().Scan(
            new State(Quaternion.Identity, DateTimeOffset.MinValue),
            (state, x) =>
            {
                var (req, timestamp) = x;
                var input = req.Input.X;

                var lastTurn = state.LastTurn;

                if (timestamp - lastTurn < coolTime || Math.Abs(input) < 0.5)
                {
                    return new State(Quaternion.Identity, lastTurn);
                }

                var rotation = new Quaternion(Vector3.Up, maxTurnRate.Radians * -MathF.Sign(input));

                return new State(rotation, timestamp);
            }
        ).Select(x => x.Rotation);

    private readonly record struct State(Quaternion Rotation, DateTimeOffset LastTurn);
}