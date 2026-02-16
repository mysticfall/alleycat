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
    [ExportGroup("IK Targets")] [Export] public Node3D? RightFootTarget { get; set; }

    [Export] public Marker3D? LeftFootTarget { get; set; }

    [ExportGroup("IK Poles")] [Export] public Node3D? RightKneePole { get; set; }

    [Export] public Marker3D? LeftKneePole { get; set; }

    [Export(PropertyHint.Range, "0.1,3")] public float PoleLength { get; set; } = 1f;

    protected override Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from rightFoot in RightFootTarget.Require("Right foot target is not set.")
        from leftFoot in LeftFootTarget.Require("Left foot target is not set.")
        from rightKnee in RightKneePole.Require("Right knee pole is not set.")
        from leftKnee in LeftKneePole.Require("Left knee pole is not set.")
        select (IIkModifier)new AdjustLegsIk(
            rightFoot.AsMovable(),
            leftFoot.AsMovable(),
            rightKnee.AsMovable(),
            leftKnee.AsMovable(),
            rig,
            onIkProcess,
            PoleLength.Metres(),
            loggerFactory
        );
}