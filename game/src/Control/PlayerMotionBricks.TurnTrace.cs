using System.Collections.Concurrent;
using AlleyCat.IK;
using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

// Temporary headset diagnostic. Keep all sampling below null-conditional calls so an
// untraced player never reads a bone, allocates a row or inspects the environment per tick.
public partial class PlayerMotionBricks
{
    private TurnTrace? _turnTrace;
    internal void TraceTurnTick() => _turnTrace?.Tick();
    internal void TraceTurnSkin() => _turnTrace?.Skin();

    private void StartTurnTrace()
    {
        if (System.Environment.GetEnvironmentVariable("ALLEYCAT_TURN_TRACE") != "1")
        {
            return;
        }
        try
        {
            _turnTrace = new TurnTrace(this);
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "Could not initialise optional headset turn trace");
        }
    }

    private sealed class TurnTrace
    {
        private readonly PlayerMotionBricks _p;
        private readonly WalkingExperimentDriver _driver;
        private readonly Skeleton3D _skin;
        private readonly RetargetModifier3D _native;
        private readonly PlayerInputTrace _log;
        private readonly int _spine;
        private readonly Leg _leftLeg, _rightLeg;
        private readonly ConcurrentQueue<(long Ticket, string Reason, long Time)> _rejects = new();
        private long _executing;
        private bool _enabled = true;
        private Vector2 _controllerLeft, _controllerRight;

        internal TurnTrace(PlayerMotionBricks player)
        {
            _p = player;
            _driver = player.ContinuousDriver!;
            _skin = player._skeleton!;
            _native = _skin.GetParent<RetargetModifier3D>();
            _spine = _skin.FindBone("Spine");
            _leftLeg = new Leg(_skin, "Left");
            _rightLeg = new Leg(_skin, "Right");
            string directory = ProjectSettings.GlobalizePath("res://temp/turn-trace");
            _log = new PlayerInputTrace(Path.Combine(directory, $"{DateTime.UtcNow:yyyyMMddTHHmmssfffffffZ}.jsonl"), player._logger!);
            _driver.WindowSubmitted += Submitted;
            _driver.PlaybackStarted += Applied;
            _driver.PlaybackCompleted += Completed;
            _driver.TraceRetirement = Retired;
            _driver.TraceWorkerEvent = (ticket, reason) =>
            {
                if (reason == "worker-start")
                {
                    Volatile.Write(ref _executing, ticket);
                }
                if (reason == "worker-end")
                {
                    Volatile.Write(ref _executing, 0);
                }
                _rejects.Enqueue((ticket, reason, System.Diagnostics.Stopwatch.GetTimestamp()));
            };
        }

        private static float[] XY(Vector2 v) => [v.X, v.Y];
        private static ulong Frame => Engine.GetPhysicsFrames();
        private void Submitted(long ticket, Transform3D[][] context, Transform3D[][] target) => Event("submit", ticket, _driver.RequestReason);
        private void Applied(long ticket) => Event("apply", ticket, _driver.Phase);
        private void Completed(long ticket) => Event("endpoint", ticket, _driver.Phase);
        private void Retired(long ticket, string reason) => Event("retire", ticket, reason);
        private void Event(string kind, long ticket, string reason) =>
            _log.Record(kind, new
            {
                tick = Frame,
                ticket,
                reason,
                inputEpoch = _p._inputEpoch
            });

        internal void Controller(bool left, Vector2 raw, bool accepted)
        {
            if (left)
            {
                _controllerLeft = raw;
            }
            else
            {
                _controllerRight = raw;
            }
            _log.Record("controller", new
            {
                tick = Frame,
                left,
                raw = XY(raw),
                accepted
            });
        }

        internal void Input(bool left, Vector2 raw, Vector2 processed, string origin) =>
            _log.Record("input", new
            {
                tick = Frame,
                left,
                raw = XY(raw),
                processed = XY(processed),
                origin
            });

        private void DrainRejects()
        {
            while (_rejects.TryDequeue(out (long Ticket, string Reason, long Time) reject))
            {
                _log.Record("worker", new
                {
                    tick = Frame,
                    ticket = reject.Ticket,
                    reason = reject.Reason,
                    stopwatch = reject.Time
                });
            }
        }

        internal void Tick()
        {
            if (!_enabled)
            {
                return;
            }
            try
            {
                DrainRejects();
                MetadataStationaryTurn? turn = _driver.MetadataTurn;
                _log.Record("tick", new
                {
                    tick = Frame,
                    simulationSeconds = _p._time,
                    controllerLeft = XY(_controllerLeft),
                    controllerRight = XY(_controllerRight),
                    rawLeft = XY(_p._rawLeft),
                    rawRight = XY(_p._rawRight),
                    left = XY(_p._left),
                    right = XY(_p._right),
                    leftDead = _p._left.Length() <= _p.Deadzone,
                    rightDead = Mathf.Abs(_p._right.X) <= _p.Deadzone,
                    deadzone = _p.Deadzone,
                    bin = _p.TurnIntent,
                    role = _driver.ReviewedRoles.Role,
                    phase = _p.Phase,
                    inputEpoch = _p._inputEpoch,
                    owner = turn?.Owner?.Sequence,
                    ownerEpoch = turn?.Epoch,
                    ownerLandmark = turn?.Owner?.EventId,
                    submitted = _p._ticket,
                    applied = _driver.LastAppliedRequest,
                    pending = _driver.RecordingPending,
                    executing = Volatile.Read(ref _executing),
                    playing = _driver.IsPlaying,
                    submittedBin = _driver.SubmittedTurnIntent,
                    playedBin = _driver.PlayedTurnIntent,
                    frame = _driver.RecordingFrame,
                    nativeActive = _native.Active,
                    actorYaw = Mathf.RadToDeg(_p._actor!.GlobalRotation.Y)
                });
            }
            catch (Exception e) { Disable(e); }
        }

        internal void Skin()
        {
            if (!_enabled)
            {
                return;
            }
            try
            {
                Transform3D toActor = _p._actor!.GlobalTransform.AffineInverse() * _skin.GlobalTransform;
                Vector3 forward = -(toActor.Basis * _skin.GetBoneGlobalPose(_spine).Basis).Z;
                _log.Record("skin", new
                {
                    tick = Frame,
                    spineYaw = Mathf.RadToDeg(Mathf.Atan2(-forward.X, -forward.Z)),
                    left = _leftLeg.Sample(_skin, _p._actor, toActor),
                    right = _rightLeg.Sample(_skin, _p._actor, toActor)
                });
            }
            catch (Exception e) { Disable(e); }
        }

        private void Disable(Exception error)
        {
            _enabled = false;
            _log.Record("sampling-error", new
            {
                tick = Frame,
                message = error.Message
            });
        }

        internal SomaInferenceWorker.ISession Wrap(SomaInferenceWorker.ISession session) => _log.Wrap(session);

        internal void Stop()
        {
            _driver.WindowSubmitted -= Submitted;
            _driver.PlaybackStarted -= Applied;
            _driver.PlaybackCompleted -= Completed;
            _driver.TraceRetirement = null;
            DrainRejects();
            _log.Stop();
        }

        private sealed class Leg(Skeleton3D skin, string side)
        {
            private readonly int _hip = skin.FindBone(side + "UpperLeg"), _knee = skin.FindBone(side + "LowerLeg"), _foot = skin.FindBone(side + "Foot");
            private readonly LegIKController? _pole = skin.GetNodeOrNull<LegIKController>(side + "LegIKController");
            private readonly SkeletonModifier3D? _solve = skin.GetNodeOrNull<SkeletonModifier3D>(side + "LegTwoBoneIKController");

            internal object Sample(Skeleton3D s, Node3D actor, Transform3D toActor)
            {
                Vector3 hip = toActor * s.GetBoneGlobalPose(_hip).Origin;
                Vector3 foot = toActor * s.GetBoneGlobalPose(_foot).Origin;
                Vector3 axis = (foot - hip).Normalized();
                Vector3 knee = ((toActor * s.GetBoneGlobalPose(_knee).Origin) - hip).Slide(axis);
                Vector3? pole = _pole?.PoleTarget is { } target
                    ? (actor.ToLocal(target.GlobalPosition) - hip).Slide(axis).Normalized() : null;
                return new
                {
                    kneeMm = knee.X * 1000,
                    poleSide = pole?.X,
                    poleForward = -pole?.Z,
                    poleActive = _pole?.Active,
                    poleWeight = _pole?.Influence,
                    solveActive = _solve?.Active,
                    solveWeight = _solve?.Influence
                };
            }
        }
    }
}
