using System.Reactive.Linq;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public interface IIkModifier : IRunnable, ILoggable
{
    IObservable<Duration> OnIkProcess { get; }
}

public abstract class IkModifier<TBone, TContext>(
    IRig<TBone> rig,
    IObservable<Duration> onIkProcess,
    ILoggerFactory? loggerFactory = null
) : IIkModifier
    where TBone : struct, Enum where TContext : IIkContext
{
    public IRig<TBone> Rig => rig;

    public IObservable<Duration> OnIkProcess => onIkProcess;

    public ILoggerFactory LoggerFactory => loggerFactory ?? NullLoggerFactory.Instance;

    public ILogger Logger => field ??= LoggerFactory.CreateLogger(GetType());

    protected abstract Eff<IEnv, TContext> CreateContext(
        Transform3D toSkeleton,
        Transform3D fromSkeleton
    );

    protected abstract Eff<IEnv, Unit> Process(TContext context, Duration duration);

    public Eff<IEnv, IDisposable> Run() =>
        from env in runtime<IEnv>()
        from toSkeleton in Rig.GlobalTransform
        let fromSkeleton = toSkeleton.Inverse()
        from context in CreateContext(toSkeleton, fromSkeleton)
        from disposable in IO.lift(() => OnIkProcess
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