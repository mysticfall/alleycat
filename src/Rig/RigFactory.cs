using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig;

public abstract partial class RigFactory<TBone> : NodeFactory<IRig<TBone>>, IServiceFactory
    where TBone : struct, Enum
{
    [Export] public Skeleton3D? Skeleton { get; set; }

    //FIXME: Temporary workaround until a bug with EagerSingleton gets fixed:
    InstantiationOption IServiceFactory.Instantiation => InstantiationOption.Singleton;

    protected override Eff<IEnv, IRig<TBone>> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from skeleton in Skeleton.Require("Skeleton is not set.")
        from _ in IO.lift(() => skeleton.ShowRestOnly = false)
        from rig in CreateService(skeleton, loggerFactory)
        select rig;

    protected abstract Eff<IEnv, IRig<TBone>> CreateService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    );
}