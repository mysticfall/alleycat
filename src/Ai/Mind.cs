using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Channels;
using AlleyCat.Actor;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Sense;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai;

public interface IMind : IRunnable, ILoggable
{
    IActor Actor { get; }

    Eff<IEnv, Unit> Think(SourceT<IO, IObservation> observations);

    Option<IObservation> Observe(IPercept percept);

    Eff<IEnv, IDisposable> IRunnable.Run
    {
        get
        {
            var channel = Channel.CreateBounded<IObservation>(new BoundedChannelOptions(100));
            var source = channel.AsSourceT<IO, IObservation>();

            return
                from env in runtime<IEnv>()
                from sensoryInput in IO.pure(
                    Actor.Senses
                        .OfType<IPassiveSense>()
                        .Select(x => x.OnPerceive)
                        .Merge()
                        .Select(Observe)
                        .SelectMany(x => x.ToObservable())
                )
                from d1 in Think(source).IfFail(e =>
                    {
                        Logger.LogError(e, "Failed to think.");

                        return unit;
                    })
                    .ForkIO()
                    .Map(x => x.AsDisposable())
                from d2 in IO.lift(() =>
                    sensoryInput
                        .Do(e =>
                            {
                                if (Logger.IsEnabled(LogLevel.Debug))
                                {
                                    Logger.LogDebug("Observed event: {event}", e);
                                }
                            },
                            e => Logger.LogError(e, "Failed to observe events.")
                        )
                        .Retry()
                        .Subscribe(x =>
                        {
                            if (!channel.Writer.TryWrite(x) && Logger.IsEnabled(LogLevel.Warning))
                            {
                                Logger.LogWarning("Failed to write to channel for event: {event}", x);
                            }
                        })
                )
                select (IDisposable)new CompositeDisposable(d1, d2);
        }
    }
}