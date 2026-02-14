using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using AlleyCat.Service;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig;

[GlobalClass]
public partial class HumanRigFactory : NodeFactory<IRig<HumanBone>>, IServiceFactory
{
    [Export] public Skeleton3D? Skeleton { get; set; }

    //Need eager initialisation to trigger idle animations (i.e. ShowRestOnly = false).
    InstantiationOption IServiceFactory.Instantiation => InstantiationOption.Singleton;

    protected override Eff<IEnv, IRig<HumanBone>> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from skeleton in Skeleton.Require("Skeleton is not set.")
        from _ in IO.lift(() => skeleton.ShowRestOnly = false)
        select new HumanRig(skeleton) as IRig<HumanBone>;
}