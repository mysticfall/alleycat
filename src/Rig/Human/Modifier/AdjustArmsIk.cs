using AlleyCat.Env;
using AlleyCat.Logging;
using AlleyCat.Rig.Ik;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Rig.Human.Modifier;

public class AdjustArmsIk : IIkModifier
{
    public IObservable<Duration> OnIkProcess { get; }

    public ILogger Logger { get; }

    public ILoggerFactory? LoggerFactory { get; }

    private readonly Eff<IEnv, Unit> _process;

    private readonly Length _poleLength;

    public AdjustArmsIk(
        IMovable3d rightHand,
        IMovable3d leftHand,
        IMovable3d rightElbowPole,
        IMovable3d leftElbowPole,
        IRig<HumanBone> rig,
        IObservable<Duration> onIkProcess,
        Length? poleLength = null,
        ILoggerFactory? loggerFactory = null
    )
    {
        OnIkProcess = onIkProcess;

        Logger = loggerFactory.GetLogger<AdjustLegsIk>();
        LoggerFactory = loggerFactory;

        _poleLength = poleLength ?? 50.Centimetres();

        _process =
            from toSkeleton in rig.GlobalTransform
            let fromSkeleton = toSkeleton.Inverse()
            from _1 in SyncArm(
                toSkeleton,
                fromSkeleton,
                HumanBone.RightUpperArm,
                rightHand,
                rightElbowPole,
                Vector3.Right
            )
            from _2 in SyncArm(
                toSkeleton,
                fromSkeleton,
                HumanBone.LeftUpperArm,
                leftHand,
                leftElbowPole,
                Vector3.Left
            )
            select unit;

        return;

        Eff<IEnv, Unit> SyncArm(
            Transform3D toSkeleton,
            Transform3D fromSkeleton,
            HumanBone upperArmBone,
            ILocatable3d handRef,
            IMovable3d poleTarget,
            Vector3 outsideDir
        )
        {
            return
                from env in runtime<IEnv>()
                let tree = env.Scene.SceneTree
                let label = tree.GetRoot().GetNode<Label>("/root/Global/SubViewport/DebugOverlay/Label")
                from shoulder in rig.GetPose(upperArmBone).Map(x => x.Origin)
                from hand in handRef.GlobalTransform.Map(x => fromSkeleton * x)
                let arm = hand.Origin - shoulder
                let armDir = arm.Normalized()
                let centre = shoulder + armDir * arm.Length() / 2
                let handBack = hand.Basis * Vector3.Back
                let dir1 = hand.Basis * outsideDir
                let plane = new Plane(armDir, centre)
                let dir2 = Optional(plane.IntersectsRay(hand.Origin, handBack))
                    .Map(x => (x - centre).Normalized())
                    .IfNone(handBack)
                let dot = armDir.Dot(handBack)
                let dir = dir2.Lerp(dir1, dot)
                let pole = toSkeleton * (centre + dir * (float)_poleLength.Metres)
                from _2 in poleTarget.SetGlobalTransform(new Transform3D(Basis.Identity, pole))
                from _ in IO.lift(() =>
                {
                    label.Text = $"Dot: {dot:F2}";
                })
                select unit;
        }
    }

    public Eff<IEnv, Unit> Process(Duration delta) => _process;
}