using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human;

[GlobalClass]
public partial class HumanRigFactory : TypedIkRigFactory<HumanBone>
{
    protected override Eff<IEnv, IIkRig<HumanBone>> CreateTypedService(
        Skeleton3D skeleton,
        IObservable<Duration> onBeforeIk,
        IObservable<Duration> onAfterIk,
        ILoggerFactory loggerFactory
    ) => SuccessEff<IIkRig<HumanBone>>(
        new HumanRig(skeleton, onBeforeIk, onAfterIk)
    );
}