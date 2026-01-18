using AlleyCat.Audio;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Speech.Transcriber;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Speech.Voice;

[GlobalClass]
public partial class PlayerVoiceFactory : VoiceFactory
{
    [Export] public string BusName { get; set; } = "Record";

    [Export] public TranscriberFactory? Transcriber { get; set; }

    [Export] public TriggerFactory? Trigger { get; set; }

    protected override Eff<IEnv, IVoice> CreateService(
        VoiceId id,
        IO<Transform3D> globalTransform,
        ILoggerFactory loggerFactory
    ) =>
        from recorder in AudioServerApi.GetRecordBus(BusName)
        from transcriber in Transcriber
            .Require("Transcriber is not set.")
            .Bind(x => x.TypedService)
        from trigger in Trigger
            .Require("Trigger is not set.")
            .Bind(x => x.TypedService)
        select (IVoice)new PlayerVoice(
            id,
            recorder,
            transcriber,
            trigger,
            globalTransform,
            loggerFactory
        );
}