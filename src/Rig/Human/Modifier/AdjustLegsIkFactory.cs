using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Human.Modifier;

[GlobalClass]
public partial class AdjustLegsIkFactory : HumanIkModifierFactory
{
    [Export] public Node3D? RightFootTarget { get; set; }

    [Export] public Node3D? LeftFootTarget { get; set; }

    protected override Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from rightFoot in RightFootTarget.Require("Right foot target is not set.")
        from leftFoot in LeftFootTarget.Require("Left foot target is not set.")
        select (IIkModifier)new AdjustLegsIk(
            rightFoot.AsMovable(),
            leftFoot.AsMovable(),
            rig,
            onIkProcess,
            loggerFactory
        );
}