using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public interface IIkModifier : IRunnable;

public abstract class IkModifier<TBone> : IIkModifier
    where TBone : struct, Enum
{
    protected IRig<TBone> Rig { get; }

    public Eff<IEnv, IDisposable> Run { get; }

    protected IkModifier(
        IRig<TBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory? loggerFactory = null
    )
    {
        Rig = rig;

        var logger = loggerFactory.GetLogger(GetType());

        Run =
            from env in runtime<IEnv>()
            from disposable in IO.lift(() => onIkProcess
                .Subscribe(duration =>
                {
                    Process(duration).Run(env).IfFail(e =>
                    {
                        logger.LogError(e, "Failed to run IK process.");
                    });
                })
            )
            select disposable;
    }

    protected abstract Eff<IEnv, Unit> Process(Duration timeDelta);
}