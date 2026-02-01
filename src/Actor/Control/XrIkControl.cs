using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Actor.Control;

public class XrIkControl(
    IRig<HumanBone> rig,
    Transform3D headToView,
    IO<Transform3D> viewTransform,
    IO<Transform3D> globalTransform,
    XrDevices xr,
    IObservable<Duration> onPhysicsProcess,
    ILoggerFactory? loggerFactory = null
) : IControl
{
    private readonly ILogger _logger = loggerFactory.GetLogger<XrIkControl>();

    private Eff<Unit> AdjustWorldScale() =>
        from virtualHeight in rig
            .GetRest(HumanBone.Head)
            .Map(x => x * headToView)
            .Map(x => x.Origin.Y.Metres())
        from physicalHeight in xr.BaseEyeHeight
        from currentScale in IO.lift(() => xr.Origin.WorldScale)
        let scale = virtualHeight * currentScale / physicalHeight
        from _ in IO.lift(() =>
        {
            xr.Origin.WorldScale = (float)scale;

            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Adjusted world scale to {scale:F3}.", scale);
            }
        })
        select unit;

    private IO<Unit> SyncOrigin() =>
        from transform in globalTransform
        from _1 in IO.lift(() => { xr.Origin.GlobalTransform = transform; })
        select unit;

    private IO<Unit> SyncCamera() =>
        from transform in viewTransform
        from _1 in IO.lift(() => { xr.Camera.GlobalTransform = transform; })
        select unit;

    public Eff<IEnv, IDisposable> Run()
    {
        return from _ in AdjustWorldScale()
            from cleanup in liftEff(() => onPhysicsProcess.Subscribe(_ =>
            {
                var syncXr =
                    from _1 in SyncOrigin()
                    from _2 in SyncCamera()
                    select unit;

                syncXr.Run();
            }))
            select cleanup;
    }
}