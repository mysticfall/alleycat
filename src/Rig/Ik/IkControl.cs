using System.Reactive.Disposables;
using System.Reactive.Linq;
using AlleyCat.Control;
using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Physics;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Ik;

public class IkControl : IControl
{
    private readonly Eff<IEnv, IDisposable> _run;

    public IkControl(
        IRig<HumanBone> rig,
        CharacterBody3D head,
        CharacterBody3D rightHand,
        CharacterBody3D leftHand,
        Node3D hips,
        Node3D rightFoot,
        Node3D leftFoot,
        Node3D viewpoint,
        IO<Transform3D> globalTransform,
        XrDevices xr,
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
            new PhysicalTracker(head, physicalHead),
            new PhysicalTracker(rightHand, xr.Trackers.RightHand.Placeholder),
            new PhysicalTracker(leftHand, xr.Trackers.LeftHand.Placeholder)
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
            from transform in globalTransform
            from _ in IO.lift(() => xr.Origin.GlobalTransform = transform)
            select unit;

        var syncPose =
            from toSkeleton in IO.lift(() => rig.Skeleton.GlobalTransform)
            let fromSkeleton = toSkeleton.Inverse()
            from pHead in IO.lift(() => head.GlobalTransform)
            let pHeadOrigin = (fromSkeleton * pHead).Origin
            from vHeadOrigin in rig.GetPose(HumanBone.Head).Map(x => x.Origin)
            let headOffset = pHeadOrigin - vHeadOrigin
            from hipsPose in rig.GetPose(HumanBone.Hips).Map(x => x.Translated(headOffset))
            let hipsGlobalPose = toSkeleton * hipsPose
            from _ in IO.lift(() => { hips.GlobalTransform = hipsGlobalPose; })
            select unit;

        var syncHead =
            from toSkeleton in IO.lift(() => rig.Skeleton.GlobalTransform)
            let fromSkeleton = toSkeleton.Inverse()
            from pHead in physicalHead
            from vHeadOrigin in rig.GetPose(HumanBone.Head).Map(x => x.Origin)
            from vHeadBasis in IO.lift(() => (fromSkeleton * head.GlobalTransform).Basis)
            let vHeadPose = new Transform3D(vHeadBasis, vHeadOrigin)
            let vHead = toSkeleton * vHeadPose
            from _1 in rig.SetPose(HumanBone.Head, vHeadPose)
            from _2 in IO.lift(() =>
            {
                var pHeadLocal = xr.Origin.GlobalTransform.Inverse() * pHead;

                xr.Origin.GlobalTransform = vHead * pHeadLocal.Inverse();
            })
            select unit;

        var syncFeet =
            from animRightFoot in rig.GetGlobalPose(HumanBone.RightFoot)
            from animLeftFoot in rig.GetGlobalPose(HumanBone.LeftFoot)
            from _1 in IO.lift(() =>
            {
                rightFoot.GlobalTransform = animRightFoot;
                leftFoot.GlobalTransform = animLeftFoot;
            })
            select unit;

        var postSync =
            from _1 in syncHead
            from _2 in syncFeet
            select unit;

        _run =
            from _1 in adjustWorldScale
            from initPos in callDeferred(
                from initTrans in globalTransform
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
                            from _2 in syncPose
                            select nextPos;

                        return sync.Run().Match(identity, e =>
                        {
                            logger.LogError(e, "Failed to run IK processes.");

                            return lastPos;
                        });
                    })
                    .Subscribe()
            )
            from d2 in IO.lift(() =>
                onAfterIkProcess.Subscribe(_ =>
                    postSync
                        .Run()
                        .IfFail(e =>
                            logger.LogError(e, "Failed to run post-IK processes")
                        )
                )
            )
            select (IDisposable)new CompositeDisposable(d1, d2);
    }

    public Eff<IEnv, IDisposable> Run() => _run;
}