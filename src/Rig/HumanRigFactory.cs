using AlleyCat.Env;
using AlleyCat.Service.Typed;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig;

[GlobalClass]
public partial class HumanRigFactory : NodeFactory<IRig<HumanBone>>
{
    [Export] public Skeleton3D? Skeleton { get; set; }

    protected override Eff<IEnv, IRig<HumanBone>> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from skeleton in Skeleton.Require("Skeleton is not set.")
        select new HumanRig(skeleton) as IRig<HumanBone>;
}