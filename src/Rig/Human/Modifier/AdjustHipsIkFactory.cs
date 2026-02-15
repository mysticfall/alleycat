using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Human.Modifier;

[GlobalClass]
public partial class AdjustHipsIkFactory : HumanIkModifierFactory
{
    [Export] public Node3D? HeadIkTarget { get; set; }

    [Export] public Node3D? HipsIkTarget { get; set; }

    protected override Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from head in HeadIkTarget.Require("Head target is not set.")
        from hips in HipsIkTarget.Require("Hips target is not set.")
        select (IIkModifier)new AdjustHipsIk(
            head.AsLocatable(),
            hips.AsMovable(),
            rig,
            onIkProcess,
            loggerFactory
        );
}