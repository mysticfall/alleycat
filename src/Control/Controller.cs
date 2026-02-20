using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Control;

public interface IController<T> where T : IControllable
{
    IObservable<T> Target { get; }

    IObservable<Seq<IControl>> Controls => Target.Select(x => x.Controls);

    IO<Unit> Control(T target);
}

public class Controller<T> : IController<T>, IRunnable, IDisposable where T : IControllable
{
    public IObservable<T> Target => _target.AsObservable();

    public IO<Unit> Control(T target) => IO.lift(() => _target.OnNext(target));

    public Eff<IEnv, IDisposable> Run { get; }

    private readonly Subject<T> _target = new();

    public Controller(
        ILoggerFactory? loggerFactory
    )
    {
        var logger = loggerFactory.GetLogger<Controller<T>>();

        var onTargetChange = _target.Do(x =>
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Controlling target: {target}.", x);
            }
        });

        var onControlsChange = onTargetChange.Select(x => x.Controls);

        Run =
            from env in runtime<IEnv>()
            from cleanup in liftEff(() => onControlsChange
                .Append(Seq<IControl>())
                .Scan(
                    new CompositeDisposable(),
                    (disposable, controls) =>
                    {
                        if (disposable.Count > 0)
                        {
                            disposable.Dispose();
                        }

                        var switchControl = controls
                            .Traverse(control => control.Run.Match(
                                    Seq,
                                    e =>
                                    {
                                        if (logger.IsEnabled(LogLevel.Error))
                                        {
                                            logger.LogError(
                                                e,
                                                "Failed to initialise a control: {control}.",
                                                control
                                            );
                                        }

                                        return Seq<IDisposable>();
                                    }
                                )
                            )
                            .As()
                            .Map(x => x.Flatten())
                            .Run(env);

                        return switchControl.Match(
                            x => new CompositeDisposable(x),
                            e =>
                            {
                                logger.LogError(e, "Failed to initialise controls.");

                                return new CompositeDisposable();
                            });
                    }
                ).Subscribe()
            )
            select (IDisposable)cleanup;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        _target.Dispose();
    }
}