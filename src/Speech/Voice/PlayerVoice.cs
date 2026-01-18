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

public class PlayerVoice(
    VoiceId id,
    AudioEffectRecord recorder,
    ITranscriber transcriber,
    ITrigger trigger,
    IO<Transform3D> globalTransform,
    ILoggerFactory? loggerFactory = null
) : IVoice, IInteractiveRecorder, IRunnable
{
    public VoiceId Id => id;

    public IO<bool> IsSpeaking => ((IAudioRecorder)this)
        .IsRecording
        .Map(x => x || _transcribing);

    public AudioEffectRecord Recorder => recorder;

    public ITrigger Trigger => trigger;

    public IO<Transform3D> GlobalTransform => globalTransform;

    public ILogger Logger { get; } = loggerFactory.GetLogger<PlayerVoice>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    //FIXME: Need a more robust way to track transcription state.
    private bool _transcribing;

    public Eff<IEnv, IDisposable> Run() =>
        from env in runtime<IEnv>()
        from onRecord in ((IInteractiveRecorder)this).OnRecord
        from dispose in liftEff(() =>
        {
            var onSpeech = onRecord
                .Select(audio => (
                    from _1 in liftEff(() => { _transcribing = true; })
                    from dialogue in transcriber.Transcribe(audio)
                    from _2 in callDeferred(((IVoice)this).Speak(dialogue))
                    select unit
                ).RunIO(env));

            return onSpeech
                .Do(_ => { _transcribing = false; },
                    e =>
                    {
                        _transcribing = false;

                        Logger.LogError(e, "Failed to process the player's speech.");
                    })
                .Retry()
                .Subscribe();
        })
        select dispose;
}