using AlleyCat.Animation.BlendShape;
using AlleyCat.Env;
using AlleyCat.Speech.Generator;
using AlleyCat.Speech.LipSync;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using LanguageExt.Effects;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Voice;

public class NpcVoice(
    VoiceId id,
    ISpeechGenerator speechGenerator,
    AudioStreamPlayer3D audioPlayer,
    IO<Transform3D> globalTransform,
    Option<ILipSyncGenerator> lipSyncGenerator = default,
    Option<IBlendShapePlayer> lipSyncPlayer = default,
    ILoggerFactory? loggerFactory = null
) : IVoice
{
    public VoiceId Id => id;

    public IO<Transform3D> GlobalTransform => globalTransform;

    public IO<bool> IsSpeaking => lift(() => audioPlayer.Playing);

    public ILogger Logger { get; } = loggerFactory.GetLogger<NpcVoice>();

    public ILoggerFactory? LoggerFactory => loggerFactory;

    public Eff<IEnv, Unit> Speak(DialogueText speech) =>
        from env in runtime<IEnv>()
        from data in 
            from audio in speechGenerator.Generate(speech)
            from animation in lipSyncGenerator
                .Traverse(x => x.Generate(audio))
            select (audio, animation)
        from _1 in callDeferred(
            from lipSync in liftEff(() =>
            {
                var (audio, animation) = data;

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.LogDebug(
                        "Generated speech: audio={audio:N} bytes, lipsync={animation}",
                        audio.Data.Length,
                        animation.IsSome
                    );
                }

                var stream = AudioStreamWav.LoadFromBuffer(audio.Data);

                audioPlayer.Stream = stream;
                audioPlayer.Play();

                return
                    from player in lipSyncPlayer
                    from anim in animation
                    select (player, anim);
            })
            from _ in lipSync.Traverse(x =>
            {
                var (player, anim) = x;

                return player.Play(anim);
            }).As()
            select unit
        )
        from _2 in liftIO(async () =>
        {
            Logger.LogDebug("Waiting for the playback to complete.");

            while (audioPlayer.IsPlaying())
            {
                await Task.Delay(10).ConfigureAwait(false);
            }

            Logger.LogDebug("Finished playback of speech.");
        })
        from _3 in callDeferred(((IVoice)this).Speak(speech))
        select unit;
}