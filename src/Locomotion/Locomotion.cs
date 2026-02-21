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
using static LanguageExt.Prelude;

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
        var onMoveInput = _moveInput.AsObservable();
        var onTurnInput = _turnInput.AsObservable();

        var onInput = onProcess
            .WithLatestFrom(onMoveInput, (delta, move) => (delta, move))
            .WithLatestFrom(onTurnInput, (prev, turn) =>
                new InputData(prev.move, turn, prev.delta)
            );

        var logger = loggerFactory.GetLogger(GetType());

        Run = IO.lift(() =>
            onInput.Subscribe(x =>
            {
                var process =
                    from rotation in rotationCalculator.CalculateRotation(x.Turn, x.Duration)
                    from velocity in velocityCalculator.CalculateVelocity(x.Movement, x.Duration)
                    from _1 in IO.lift(() =>
                    {
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace(
                                "Velocity: {velocity}, Rotation: {rotation}",
                                velocity,
                                rotation.GetEuler()
                            );
                        }
                    })
                    from _2 in Process(velocity, rotation, x.Duration)
                    select unit;

                process.Run();
            })
        );
    }

    protected abstract IO<Unit> Process(
        Vector3 velocity,
        Quaternion rotation,
        Duration duration
    );

    private readonly record struct InputData(
        Vector2 Movement,
        Vector2 Turn,
        Duration Duration
    );
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