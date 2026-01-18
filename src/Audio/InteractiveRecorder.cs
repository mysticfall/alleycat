using System.Reactive.Linq;
using AlleyCat.Control;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Audio;

public interface IInteractiveRecorder : IAudioRecorder
{
    protected ITrigger Trigger { get; }

    Eff<IEnv, IObservable<AudioStreamWav>> OnRecord =>
        from env in runtime<IEnv>()
        from obs in liftEff(() =>
        {
            var onStart = Trigger.OnPress
                .Select(_ =>
                    StartRecording()
                        .MapFail(e => Error.New("Failed to start recording.", e))
                        .RunUnsafe(env)
                );

            var onStop = Trigger.OnRelease
                .Select(_ =>
                    StopRecording()
                        .MapFail(e => Error.New("Failed to stop recording.", e))
                        .RunUnsafe(env)
                );

            return onStart.Select(_ => onStop.Take(1)).Switch();
        })
        select obs;
}