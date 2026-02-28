using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Locomotion.Rotation;
using AlleyCat.Locomotion.Velocity;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Locomotion;

public interface ILocomotion
{
    IO<bool> IsMoving { get; }

    IO<Unit> Move(Vector2 input);

    IO<Unit> Turn(Vector2 input);
}

public abstract class Locomotion : ILocomotion, IRunnable
{
    public abstract IO<bool> IsMoving { get; }

    public IO<Unit> Move(Vector2 input) => IO.lift(() => _moveInput.OnNext(input));

    public IO<Unit> Turn(Vector2 input) => IO.lift(() => _turnInput.OnNext(input));

    public Eff<IEnv, IDisposable> Run { get; }

    private readonly Subject<Vector2> _moveInput = new();

    private readonly Subject<Vector2> _turnInput = new();

    protected Locomotion(
        IVelocityCalculator velocityCalculator,
        IRotationCalculator rotationCalculator,
        IObservable<Duration> onProcess,
        ILoggerFactory? loggerFactory = null
    )
    {
        var moveRequests = onProcess
            .WithLatestFrom(_moveInput)
            .Select(x => new MoveRequest(x.Second, x.First));
        var turnRequests = onProcess
            .WithLatestFrom(_turnInput)
            .Select(x => new TurnRequest(x.Second, x.First));

        var movements = velocityCalculator.ObserveRequests(moveRequests);
        var turns = rotationCalculator.ObserveRequests(turnRequests);

        var locomotion = movements.Zip(turns);

        var logger = loggerFactory.GetLogger(GetType());

        Run = IO.lift(() =>
            locomotion.Subscribe(x =>
            {
                var velocity = x.First;
                var rotation = x.Second;

                if (logger.IsEnabled(LogLevel.Trace))
                {
                    logger.LogTrace(
                        "Velocity: {velocity}, Rotation: {rotation:F2}",
                        velocity,
                        Mathf.RadToDeg(rotation.GetEuler().Y)
                    );
                }

                Process(velocity, rotation).Run();
            })
        );
    }

    protected abstract IO<Unit> Process(Vector3 velocity, Quaternion rotation);
}

public interface ILocomotive
{
    ILocomotion Locomotion { get; }
}

public static class LocomotiveExtensions
{
    extension(ILocomotive locomotive)
    {
        public IO<bool> IsMoving => locomotive.Locomotion.IsMoving;

        public IO<Unit> Move(Vector2 input) => locomotive.Locomotion.Move(input);

        public IO<Unit> Turn(Vector2 input) => locomotive.Locomotion.Turn(input);
    }
}