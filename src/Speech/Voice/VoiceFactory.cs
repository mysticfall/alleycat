using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Voice;

[GlobalClass]
public abstract partial class VoiceFactory : NodeFactory<IVoice>
{
    [Export] public string? Id { get; set; }

    [Export] public Node3D? Origin { get; set; }

    protected override Eff<IEnv, IVoice> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from id in VoiceId.Create(Id).ToEff(identity)
        from origin in Origin.Require("Origin node is not set.")
        from voice in CreateService(id, IO.lift(() => origin.GlobalTransform), loggerFactory)
        select voice;

    protected abstract Eff<IEnv, IVoice> CreateService(
        VoiceId id,
        IO<Transform3D> globalTransform,
        ILoggerFactory loggerFactory
    );
}