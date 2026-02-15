using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Human.Modifier;

[GlobalClass]
public abstract partial class HumanIkModifierFactory : IkModifierFactory
{
    [Export] public HumanRigFactory? Rig { get; set; }

    protected override Eff<IEnv, IIkModifier> CreateService(
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from rig in Rig.Require("Rig is not set.").Bind(x => x.TypedService)
        from modifier in CreateService(rig, onIkProcess, loggerFactory)
        select modifier;

    protected abstract Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    );
}