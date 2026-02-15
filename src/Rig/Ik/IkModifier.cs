using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public interface IIkModifier : IRunnable, ILoggable
{
    IObservable<Duration> OnIkProcess { get; }

    Eff<IEnv, Unit> Process(Duration delta);

    Eff<IEnv, IDisposable> IRunnable.Run() =>
        from env in runtime<IEnv>()
        from disposable in IO.lift(() =>
            OnIkProcess.Subscribe(delta =>
            {
                Process(delta).Run(env).IfFail(e =>
                {
                    Logger.LogError(e, "Failed to run IK process.");
                });
            })
        )
        select disposable;
}