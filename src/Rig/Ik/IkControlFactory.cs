using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Rig.Human;
using AlleyCat.Transform;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Rig.Ik;

[GlobalClass]
public partial class IkControlFactory : ControlFactory
{
    [Export] public HumanRigFactory? Rig { get; set; }

    [Export] public Node3D? Root { get; set; }

    [Export] public CharacterBody3D? Head { get; set; }

    [Export] public CharacterBody3D? RightHand { get; set; }

    [Export] public CharacterBody3D? LeftHand { get; set; }

    [Export] public Marker3D? Viewpoint { get; set; }

    protected override Eff<IEnv, IControl> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from xr in service<XrDevices>()
        from rig in Rig
            .Require("Rig is not set.")
            .Bind(x => x.TypedService)
        from head in Head
            .Require("Head is not set.")
        from rightHand in RightHand
            .Require("Right hand is not set.")
        from leftHand in LeftHand
            .Require("Left hand is not set.")
        from viewpoint in Viewpoint
            .Require("Viewpoint is not set.")
        from root in Root
            .Require("Root is not set.")
        let skeleton = rig.Skeleton
        from onBeforeIkProcess in IO.lift(() =>
        {
            var node = new IkModifierNode();

            skeleton.AddChild(node, false, InternalMode.Front);

            node.Owner = skeleton;

            return node.OnIkProcess;
        })
        from onAfterIkProcess in IO.lift(() =>
        {
            var node = new IkModifierNode();

            skeleton.AddChild(node, false, InternalMode.Back);

            node.Owner = skeleton;

            return node.OnIkProcess;
        })
        select (IControl)new IkControl(
            xr,
            rig,
            root.AsLocatable(),
            head,
            rightHand,
            leftHand,
            viewpoint,
            loggerFactory
        );
}