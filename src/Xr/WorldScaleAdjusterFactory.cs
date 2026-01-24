using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Service.Typed;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;

namespace AlleyCat.Xr;

[GlobalClass]
public partial class WorldScaleAdjusterFactory : NodeFactory<WorldScaleAdjuster>
{
    [Export]
    public Skeleton3D? Skeleton { get; set; }

    [Export]
    public Marker3D? Viewpoint { get; set; }

    protected override Eff<IEnv, WorldScaleAdjuster> CreateService(ILoggerFactory loggerFactory) =>
        from xr in service<XrDevices>()
        from skeleton in Skeleton.Require("Skeleton is not set.")
        from viewpoint in Viewpoint.Require("Viewpoint is not set.")
        select new WorldScaleAdjuster(
            skeleton,
            viewpoint,
            xr.Camera,
            xr.Origin,
            xr.Interface,
            loggerFactory
        );
}