using AlleyCat.Common;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Rig;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Actor.Control;

[GlobalClass]
public partial class XrIkControlFactory : ControlFactory
{
    [Export] public HumanRigFactory? Rig { get; set; }

    [Export] public Marker3D? Viewpoint { get; set; }

    [Export] public Node3D? Root { get; set; }

    protected override Eff<IEnv, IControl> CreateService(
        ILoggerFactory loggerFactory
    ) =>
        from rig in Rig
            .Require("Rig is not set.")
            .Bind(x => x.TypedService)
        from viewpoint in Viewpoint
            .Require("Viewpoint is not set.")
            .Map(x => IO.lift(() => x.GlobalTransform))
        from root in Root
            .Require("Root is not set.")
            .Map(x => IO.lift(() => x.GlobalTransform))
        from xr in service<XrDevices>()
        select (IControl)new XrIkControl(
            rig,
            viewpoint,
            root,
            xr,
            OnPhysicsProcess,
            loggerFactory
        );
}