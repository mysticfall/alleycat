using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Rig.Human.Modifier;

[GlobalClass]
public partial class AdjustArmsIkFactory : HumanIkModifierFactory
{
    [ExportGroup("References")] [Export] public Node3D? RightHand { get; set; }

    [Export] public Node3D? LeftHand { get; set; }

    [ExportGroup("IK Targets")] [Export] public Node3D? RightShoulder { get; set; }

    [Export] public Node3D? LeftShoulder { get; set; }

    [ExportGroup("IK Poles")] [Export] public Node3D? RightElbowPole { get; set; }

    [Export] public Marker3D? LeftElbowPole { get; set; }

    [Export(PropertyHint.Range, "0.1")] public float PoleLength { get; set; } = 0.5f;

    protected override Eff<IEnv, IIkModifier> CreateService(
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        ILoggerFactory loggerFactory
    ) =>
        from rightHand in RightHand.Require("Right hand is not set.")
        from leftHand in LeftHand.Require("Left hand is not set.")
        from rightShoulder in RightShoulder.Require("Right shoulder target is not set.")
        from leftShoulder in LeftShoulder.Require("Left shoulder target is not set.")
        from rightElbow in RightElbowPole.Require("Right elbow pole is not set.")
        from leftElbow in LeftElbowPole.Require("Left elbow pole is not set.")
        select (IIkModifier)new AdjustArmsIk(
            rightHand.AsLocatable(),
            leftHand.AsLocatable(),
            rightShoulder.AsMovable(),
            leftShoulder.AsMovable(),
            rightElbow.AsMovable(),
            leftElbow.AsMovable(),
            rig,
            onIkProcess,
            PoleLength.Metres(),
            loggerFactory
        );
}