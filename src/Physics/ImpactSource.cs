using System.Reactive.Linq;
using AlleyCat.Audio;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Physics;

public interface IImpactSource : IAudioSource, IRunnable, ILoggable
{
    Duration SilentPeriod { get; }

    protected Area3D Area { get; }

    protected SoundSet SoundSet { get; }

    Eff<IEnv, IDisposable> IRunnable.Run => IO.lift(() =>
    {
        var onAreaEntered = Observable
            .FromEvent<Area3D.AreaEnteredEventHandler, Area3D>(
                handler => new Area3D.AreaEnteredEventHandler(handler),
                add => Area.AreaEntered += add,
                remove => Area.AreaEntered -= remove)
            .Select(_ => unit);

        var onBodyEntered = Observable
            .FromEvent<Area3D.BodyEnteredEventHandler, Node3D>(
                handler => new Area3D.BodyEnteredEventHandler(handler),
                add => Area.BodyEntered += add,
                remove => Area.BodyEntered -= remove)
            .Select(_ => unit);

        return onAreaEntered
            .Merge(onBodyEntered)
            .Throttle((TimeSpan)SilentPeriod)
            .Do(_ => { },
                e => Logger.LogError(e, "Failed to emit an impact sound.")
            )
            .Retry()
            .Subscribe(_ => SoundSet.Play(AudioPlayer));
    });
}

public class ImpactSource(
    Area3D area,
    SoundSet soundSet,
    AudioStreamPlayer3D audioPlayer,
    Duration silentPeriod,
    ILoggerFactory? loggerFactory = null
) : IImpactSource
{
    public Area3D Area => area;

    public SoundSet SoundSet => soundSet;

    public AudioStreamPlayer3D AudioPlayer => audioPlayer;

    public Duration SilentPeriod => silentPeriod;

    public ILogger Logger { get; } = loggerFactory.GetLogger<ImpactSource>();

    public ILoggerFactory? LoggerFactory => loggerFactory;
}