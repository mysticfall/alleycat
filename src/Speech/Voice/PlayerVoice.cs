using System.Reactive.Linq;
using AlleyCat.Audio;
using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Speech.Transcriber;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Voice;

public class PlayerVoice : IVoice, IInteractiveRecorder, IRunnable
{
    public VoiceId Id { get; }

    public IO<bool> IsSpeaking { get; }

    public AudioEffectRecord Recorder { get; }

    public ITrigger Trigger { get; }

    public IO<Transform3D> GlobalTransform { get; }

    public ILogger Logger { get; }

    public ILoggerFactory? LoggerFactory { get; }

    public Eff<IEnv, IDisposable> Run { get; }

    public PlayerVoice(
        VoiceId id,
        AudioEffectRecord recorder,
        ITranscriber transcriber,
        ITrigger trigger,
        IO<Transform3D> globalTransform,
        ILoggerFactory? loggerFactory = null
    )
    {
        Id = id;
        Recorder = recorder;
        Trigger = trigger;
        GlobalTransform = globalTransform;

        LoggerFactory = loggerFactory;
        Logger = loggerFactory.GetLogger<PlayerVoice>();

        //FIXME: Need a more robust way to track transcription state.
        var transcribing = false;

        IsSpeaking = ((IAudioRecorder)this)
            .IsRecording
            .Map(x => x || transcribing);

        Run =
            from env in runtime<IEnv>()
            from onRecord in ((IInteractiveRecorder)this).OnRecord
            from dispose in IO.lift(() =>
            {
                var onSpeech = onRecord
                    .Select(audio => (
                        from _1 in IO.lift(() => { transcribing = true; })
                        from dialogue in transcriber.Transcribe(audio)
                        from _2 in callDeferred(((IVoice)this).Speak(dialogue))
                        select unit
                    ).RunIO(env));

                return onSpeech
                    .Do(_ => { transcribing = false; },
                        e =>
                        {
                            transcribing = false;

                            Logger.LogError(e, "Failed to process the player's speech.");
                        })
                    .Retry()
                    .Subscribe();
            })
            select dispose;
    }
}