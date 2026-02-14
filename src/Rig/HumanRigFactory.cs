using AlleyCat.Env;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig;

[GlobalClass]
public partial class HumanRigFactory : RigFactory<HumanBone>
{
    protected override Eff<IEnv, IRig<HumanBone>> CreateService(
        Skeleton3D skeleton,
        ILoggerFactory loggerFactory
    ) => SuccessEff<IRig<HumanBone>>(new HumanRig(skeleton));
}