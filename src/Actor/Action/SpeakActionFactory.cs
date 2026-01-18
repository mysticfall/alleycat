using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Action;

[GlobalClass]
public partial class SpeakActionFactory : AiActionFactory
{
    protected override Eff<IEnv, IAiAction> CreateService(
        AiFunctionName name,
        ILoggerFactory loggerFactory
    ) => SuccessEff<IAiAction>(new SpeakAction(name, loggerFactory));
}