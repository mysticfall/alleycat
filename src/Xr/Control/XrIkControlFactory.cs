using System.Reactive.Linq;
using System.Reactive.Subjects;
using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Rig;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Xr.Control;

[GlobalClass]
public partial class XrIkControlFactory : ControlFactory
{
    [Export] public HumanRigFactory? Rig { get; set; }

    [ExportGroup("IK Targets")] [Export] public CharacterBody3D? Head { get; set; }

    [Export] public CharacterBody3D? RightHand { get; set; }

    [Export] public CharacterBody3D? LeftHand { get; set; }

    [Export] public Node3D? RightFoot { get; set; }

    [Export] public Node3D? LeftFoot { get; set; }

    [ExportGroup("References")] [Export] public Marker3D? Viewpoint { get; set; }

    [Export] public Node3D? Root { get; set; }

    private partial class ControlModifier(Subject<Duration> subject) : SkeletonModifier3D
    {
        public override void _ProcessModificationWithDelta(double delta)
        {
            base._ProcessModificationWithDelta(delta);

            subject.OnNext(delta.Seconds());
        }

        public override void _ExitTree()
        {
            base._ExitTree();

            subject.OnCompleted();
        }
    }

    protected override Eff<IEnv, IControl> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from rig in Rig
            .Require("Rig is not set.")
            .Bind(x => x.TypedService)
        from head in Head
            .Require("Head is not set.")
        from rightHand in RightHand
            .Require("Right hand is not set.")
        from leftHand in LeftHand
            .Require("Left hand is not set.")
        from rightFoot in RightFoot
            .Require("Right foot is not set.")
        from leftFoot in LeftFoot
            .Require("Left foot is not set.")
        from viewpoint in Viewpoint
            .Require("Viewpoint is not set.")
        from globalTransform in Root
            .Require("Root is not set.")
            .Map(x => IO.lift(() => x.GlobalTransform))
        from onModificationProcess in IO.lift(() =>
        {
            var subject = new Subject<Duration>();
            var skeleton = rig.Skeleton;

            var _modifier = new ControlModifier(subject);

            skeleton.AddChild(_modifier, false, InternalMode.Back);
            // skeleton.MoveChild(_modifier, 0);

            _modifier.Owner = skeleton;

            return subject.AsObservable();
        })
        from xr in service<XrDevices>()
        select (IControl)new XrIkControl(
            rig,
            head,
            rightHand,
            leftHand,
            rightFoot,
            leftFoot,
            viewpoint,
            globalTransform,
            xr,
            onModificationProcess,
            loggerFactory
        );
}