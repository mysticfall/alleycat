using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig;

[GlobalClass]
public abstract partial class RigFactory : NodeFactory<IRig>, IServiceFactory
{
    [Export] public Skeleton3D? Skeleton { get; set; }

    //FIXME: Temporary workaround until a bug with EagerSingleton gets fixed:
    InstantiationOption IServiceFactory.Instantiation => InstantiationOption.Singleton;

    protected override Eff<IEnv, IRig> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from skeleton in Skeleton.Require("Skeleton is not set.")
        from _ in IO.lift(() => skeleton.ShowRestOnly = false)
        from rig in CreateService(skeleton, loggerFactory)
        select rig;

    protected abstract Eff<IEnv, IRig> CreateService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    );
}

public abstract partial class TypedRigFactory<TBone> : RigFactory, IServiceFactory<IRig<TBone>>
    where TBone : struct, Enum
{
    public new Eff<IEnv, IRig<TBone>> TypedService => base.TypedService.Map(x => (IRig<TBone>)x);

    protected override Eff<IEnv, IRig> CreateService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    ) => CreateTypedService(skeleton, loggerFactory).Map(IRig (x) => x);

    protected abstract Eff<IEnv, IRig<TBone>> CreateTypedService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    );
}