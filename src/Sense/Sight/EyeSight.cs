using System.Reactive.Linq;
using AlleyCat.Animation;
using AlleyCat.Async;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static Godot.AnimationNodeOneShot;

namespace AlleyCat.Sense.Sight;

public readonly record struct EyeAnimParams(
    AnimationParam Blink,
    AnimationParam UpDown,
    AnimationParam RightLeft
);

public interface IEyeSight : ISight, IPhysicsFrameAware, IRunnable, ILoggable
{
    protected VectorRange2 EyesRotationRange { get; }

    protected AnimationTree AnimationTree { get; }

    protected EyeAnimParams EyeAnimParams { get; }

    protected Duration EyesBlinkInterval { get; }

    protected Duration EyesBlinkVariation { get; }

    Eff<IEnv, IDisposable> IRunnable.Run => IO.lift(() =>
        OnPhysicsProcess
            .Scan(State.Initial, (s, delta) => (
                from s1 in ProcessRotation(s, delta)
                from s2 in ProcessBlink(s1, delta)
                select s2
            ).Run())
            .Do(_ => { },
                e => Logger.LogError(e, "Failed to control the eyes.")
            )
            .Retry()
            .Subscribe()
    );

    private IO<Duration> CalculateNextBlink() => IO.lift<Duration>(() =>
        EyesBlinkInterval + EyesBlinkVariation * (float)GD.RandRange(-1.0, 1.0)
    );

    private IO<State> Blink(State state) =>
        from next in CalculateNextBlink()
        from _ in IO.lift(() => AnimationTree.Set(EyeAnimParams.Blink, (int)OneShotRequest.Fire))
        select state with { SinceLastBlink = default, NextBlink = next };

    private IO<State> ProcessBlink(State state, Duration delta)
    {
        var ensureInitialised = state.NextBlink == default
            ? from next in CalculateNextBlink() select state with { NextBlink = next }
            : IO.pure(state);

        return from s in ensureInitialised
            let sinceLastBlink = s.SinceLastBlink + delta
            let isTimeForBlink = sinceLastBlink >= s.NextBlink
            from finalState in isTimeForBlink
                ? Blink(s)
                : IO.pure(s with { SinceLastBlink = sinceLastBlink })
            select finalState;
    }

    private Vector2 CalculateEyeRotation(
        Transform3D viewpoint,
        Option<Vector3> lookAt
    ) => lookAt.Match(l =>
        {
            var origin = viewpoint.Origin;
            var basis = viewpoint.Basis;

            var lookDir = (l - origin).Normalized();

            var forward = basis * Vector3.Forward;
            var up = basis * Vector3.Up;
            var left = basis * Vector3.Left;

            var horizontalRot = forward.SignedAngleTo(lookDir, up);
            var verticalRot = forward.SignedAngleTo(lookDir, left);

            var horizontalRotDeg = float.RadiansToDegrees(horizontalRot);
            var verticalRotDeg = float.RadiansToDegrees(verticalRot);

            var x = (float.Clamp(horizontalRotDeg, EyesRotationRange.Min.X, EyesRotationRange.Max.X) -
                     EyesRotationRange.Min.X) / (EyesRotationRange.Max.X - EyesRotationRange.Min.X);
            var y = (float.Clamp(verticalRotDeg, EyesRotationRange.Min.Y, EyesRotationRange.Max.Y) -
                     EyesRotationRange.Min.Y) / (EyesRotationRange.Max.Y - EyesRotationRange.Min.Y);

            return new Vector2(x, y);
        },
        () => State.Initial.EyeRotation
    );

    private static Vector2 LerpEyeRotation(
        Vector2 target,
        Vector2 current,
        Duration sinceLastUpdate
    ) => current.Lerp(target, (float)sinceLastUpdate.Milliseconds / 100);

    private IO<Unit> RotateEyes(Vector2 rotation) => IO.lift(() =>
    {
        AnimationTree.Set(EyeAnimParams.RightLeft, rotation.X);
        AnimationTree.Set(EyeAnimParams.UpDown, rotation.Y);
    });

    private IO<State> ProcessRotation(State state, Duration delta) =>
        from lookAt in LookAt
            .Bind(x => x.Traverse(y => y.GlobalTransform))
            .Map(x => x.Map(y => y.Origin))
        from viewpoint in GlobalTransform
        let target = CalculateEyeRotation(viewpoint, lookAt)
        let rotation = LerpEyeRotation(target, state.EyeRotation, delta)
        from _ in RotateEyes(rotation)
        select state with
        {
            EyeRotation = rotation
        };

    private readonly record struct State(
        Vector2 EyeRotation,
        Duration SinceLastBlink = default,
        Duration NextBlink = default
    )
    {
        public static readonly State Initial = new(new Vector2(0.5f, 0.5f));
    }
}