using AlleyCat.Actor;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Ai;

[GlobalClass]
public abstract partial class MindFactory : NodeFactory<IMind>
{
    [Export] public bool Active { get; set; } = true;

    [Export] public ActorFactory? Actor { get; set; }

    protected override Eff<IEnv, IMind> CreateService(ILoggerFactory loggerFactory) =>
        from actor in Actor
            .Require("Actor is not set.")
            .Bind(x => x.TypedService)
        from mind in CreateService(actor, loggerFactory)
        select mind;

    protected abstract Eff<IEnv, IMind> CreateService(
        IActor actor,
        ILoggerFactory loggerFactory
    );
}