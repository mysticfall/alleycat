using AlleyCat.Animation.BlendShape;
using AlleyCat.Env;
using AlleyCat.Speech.Generator;
using AlleyCat.Speech.LipSync;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Voice;

[GlobalClass]
public partial class NpcVoiceFactory : VoiceFactory
{
    [Export] public AudioStreamPlayer3D? AudioPlayer { get; set; }

    [Export] public SpeechGeneratorFactory? SpeechGenerator { get; set; }

    [Export] public LipSyncGeneratorFactory? LipSync { get; set; }

    [Export] public BlendShapePlayerFactory? BlendShapePlayer { get; set; }

    protected override Eff<IEnv, IVoice> CreateService(
        VoiceId id,
        IO<Transform3D> globalTransform,
        ILoggerFactory loggerFactory
    ) =>
        from speechGenerator in SpeechGenerator
            .Require("SpeechGenerator is not set.")
            .Bind(x => x.TypedService)
        from audioPlayer in AudioPlayer.Require("AudioPlayer is not set.")
        from lipSyncGenerator in Optional(LipSync)
            .Traverse(x => x.TypedService)
        from libSyncPlayer in Optional(BlendShapePlayer)
            .Traverse(x => x.TypedService)
        select (IVoice)new NpcVoice(
            id: id,
            speechGenerator: speechGenerator,
            audioPlayer: audioPlayer,
            globalTransform: globalTransform,
            lipSyncGenerator: lipSyncGenerator,
            lipSyncPlayer: libSyncPlayer,
            loggerFactory
        );
}