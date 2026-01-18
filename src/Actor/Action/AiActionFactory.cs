using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Action;

[GlobalClass]
public abstract partial class AiActionFactory : ActionFactory
{
    [Export] public string? Name { get; set; }

    protected override Eff<IEnv, IAction> CreateService(ILoggerFactory loggerFactory) =>
        from name in AiFunctionName.Create(Name?.Trim()).ToEff(identity)
        from action in CreateService(name, loggerFactory)
        select (IAction)action;

    protected abstract Eff<IEnv, IAiAction> CreateService(
        AiFunctionName name,
        ILoggerFactory loggerFactory
    );
}