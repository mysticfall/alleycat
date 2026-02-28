using System.Reactive.Disposables;
using System.Reactive.Linq;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Physics;
using AlleyCat.Rig.Human;
using AlleyCat.Transform;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public class IkControl : IControl
{
    public Eff<IEnv, IDisposable> Run { get; }

    public IkControl(
        XrDevices xr,
        IRig<HumanBone> rig,
        ILocatable3d root,
        CharacterBody3D head,
        CharacterBody3D rightHand,
        CharacterBody3D leftHand,
        Node3D viewpoint,
        IObservable<Duration> onBeforeIkProcess,
        IObservable<Duration> onAfterIkProcess,
        ILoggerFactory? loggerFactory = null
    )
    {
        var logger = loggerFactory.GetLogger<IkControl>();

        var headToView = viewpoint.Transform;
        var viewToHead = headToView.Inverse();

        var physicalHead = IO.lift(() => xr.Camera.GlobalTransform * viewToHead);

        var trackers = Seq(
            new PhysicsBodyFollower(head, physicalHead),
            new PhysicsBodyFollower(
                rightHand,
                xr.Trackers.RightHand.Placeholder.AsLocatable().GlobalTransform
            ),
            new PhysicsBodyFollower(
                leftHand,
                xr.Trackers.LeftHand.Placeholder.AsLocatable().GlobalTransform
            )
        );

        var adjustWorldScale =
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

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Adjusted world scale to {scale:F3}.", scale);
                }
            })
            select unit;

        var resetOrigin =
            from transform in root.GlobalTransform
            from _ in IO.lift(() => xr.Origin.GlobalTransform = transform)
            select unit;

        var adjustOrigin =
            from physicalPose in physicalHead
            from virtualPose in rig.GetGlobalPose(HumanBone.Head)
            from _ in IO.lift(() =>
            {
                var localPose = xr.Origin.GlobalTransform.Inverse() * physicalPose;

                xr.Origin.GlobalTransform = virtualPose * localPose.Inverse();
            })
            select unit;

        Run =
            from _1 in adjustWorldScale
            from initPos in callDeferred(
                from initTrans in root.GlobalTransform
                from _ in IO.lift(() => { xr.Origin.GlobalTransform = initTrans; })
                from initPos in trackers
                    .Traverse(t => t
                        .Initialise()
                        .Map(x => x.Origin)
                    )
                    .As()
                select initPos
            )
            from d1 in IO.lift(() =>
                onBeforeIkProcess
                    .Scan(initPos, (lastPos, duration) =>
                    {
                        var syncTrackers = trackers
                            .Zip(lastPos)
                            .Traverse(x => x.First.Process(x.Second, duration));

                        var sync =
                            from _1 in resetOrigin
                            from nextPos in syncTrackers
                            select nextPos;

                        return sync.Run();
                    })
                    .Subscribe()
            )
            from d2 in IO.lift(() =>
                onAfterIkProcess.Subscribe(_ =>
                {
                    adjustOrigin.Run().IfFail(e =>
                    {
                        logger.LogError(e, "Failed to run the IK post-process.");
                    });
                })
            )
            select (IDisposable)new CompositeDisposable(d1, d2);
    }
}