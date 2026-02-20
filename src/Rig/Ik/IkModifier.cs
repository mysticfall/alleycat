using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public interface IIkModifier : IRunnable, ILoggable;

public abstract class IkModifier<TBone, TContext> : IIkModifier
    where TBone : struct, Enum where TContext : IIkContext
{
    protected IRig<TBone> Rig { get; }

    public Eff<IEnv, IDisposable> Run { get; }

    public ILoggerFactory LoggerFactory { get; }

    public ILogger Logger { get; }

    protected IkModifier(
        IRig<TBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory? loggerFactory = null
    )
    {
        Rig = rig;

        LoggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Logger = loggerFactory.GetLogger(GetType());

        Run =
            from env in runtime<IEnv>()
            from toSkeleton in Rig.GlobalTransform
            let fromSkeleton = toSkeleton.Inverse()
            from context in CreateContext(toSkeleton, fromSkeleton)
            from disposable in IO.lift(() => onIkProcess
                .Subscribe(duration =>
                {
                    Process(context, duration).Run(env).IfFail(e =>
                    {
                        Logger.LogError(
                            e, "Failed to run IK process.");
                    });
                })
            )
            select disposable;
    }

    protected abstract Eff<IEnv, TContext> CreateContext(
        Transform3D toSkeleton,
        Transform3D fromSkeleton
    );

    protected abstract Eff<IEnv, Unit> Process(TContext context, Duration duration);
}