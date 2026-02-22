using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public abstract class ContextAwareIkModifier<TBone, TContext> : IIkModifier
    where TBone : struct, Enum
{
    protected IRig<TBone> Rig { get; }

    public Eff<IEnv, IDisposable> Run { get; }

    protected ContextAwareIkModifier(
        IRig<TBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory? loggerFactory = null
    )
    {
        Rig = rig;

        var logger = loggerFactory.GetLogger(GetType());

        Run =
            from env in runtime<IEnv>()
            from context in CreateContext()
            from disposable in IO.lift(() => onIkProcess
                .Subscribe(duration =>
                {
                    Process(context, duration).Run(env).IfFail(e =>
                    {
                        logger.LogError(e, "Failed to run IK process.");
                    });
                })
            )
            select disposable;
    }

    protected abstract Eff<IEnv, TContext> CreateContext();

    protected abstract Eff<IEnv, Unit> Process(TContext context, Duration duration);
}