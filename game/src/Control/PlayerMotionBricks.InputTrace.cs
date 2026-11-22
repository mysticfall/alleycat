using AlleyCat.Motion;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

public partial class PlayerMotionBricks
{
    /// <summary>Opt-in bounded event trace; toggle off to finish and flush before exiting.</summary>
    [Export]
    public bool RecordInputTrace
    {
        get; set;
    }
    /// <summary>Filename label, sanitised; each recording gets a unique suffix.</summary>
    [Export]
    public string InputTraceLabel { get; set; } = "walking";
    /// <summary>Most recently started recording's absolute path.</summary>
    public string InputTracePath { get; private set; } = "";
    internal Task? InputTraceCompletion
    {
        get
        {
            Task? legacy = _inputTrace?.Completion ?? field;
            Task? poses = _poseRecording?.Completion ?? _poseTraceCompletion;
            return poses is null ? legacy : Task.WhenAll(legacy ?? Task.CompletedTask, poses);
        }
        private set;
    }
    private PlayerInputTrace? _inputTrace;
    private int _traceDiscarded;
    internal string? InputTraceDirectoryOverride
    {
        get; set;
    }

    private void UpdateInputTrace()
    {
        if (_logger is null)
        {
            return;
        }
        if (RecordInputTrace && _inputTrace is null)
        {
            string label = string.Concat(InputTraceLabel.Take(60).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
            string experiment = System.Environment.GetEnvironmentVariable("PLAYER_LOOKAHEAD_PROBE") == "1"
                ? "player-generation-lookahead-01"
                : System.Environment.GetEnvironmentVariable("PLAYER_PREDICTION_PROBE") is not null ||
                System.Environment.GetEnvironmentVariable("PLAYER_PREDICTION_NOISE") == "1"
                ? "player-target-prediction-01"
                : "player-motion";
            string directory = InputTraceDirectoryOverride ?? ProjectSettings.GlobalizePath($"res://temp/motionbricks/{experiment}/headset");
            InputTracePath = Path.Combine(directory, $"{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.jsonl");
            _inputTrace = new PlayerInputTrace(InputTracePath, _logger);
            _inputTrace.Record("start", new
            {
                VirtualSnapshotSeconds,
                VirtualChangeMetres,
                VirtualChangeDegrees,
                VirtualSharpDegrees,
                Deadzone,
                WalkingSteeringDegreesPerSecond,
                movementQuantisation = 0,
                linePolicy = "curved-walking-guide; stationary-cycle-angle-by-intensity; ordered-primary-lifts",
                animationBody = System.Environment.GetEnvironmentVariable("PLAYER_ANIMATION_BODY") == "1"
            });
            _logger.LogInformation("Player input trace started: {Path}", InputTracePath);
        }
        else if (!RecordInputTrace && _inputTrace is not null)
        {
            _inputTrace.Record("stop", new
            {
                epoch = _inputEpoch,
                request = _ticket
            });
            StopPoseTrace(null, true);
            _inputTrace.Stop();
            InputTraceCompletion = _inputTrace.Completion;
            _inputTrace = null;
        }
        StartPoseTrace();
        if (_traceDiscarded != DiscardedResults)
        {
            _traceDiscarded = DiscardedResults;
            _inputTrace?.Record("discard-count", new
            {
                count = _traceDiscarded,
                current = _ticket,
                epoch = _inputEpoch
            });
        }
    }

    internal void RecordControllerInput(bool left, Vector2 raw, bool accepted)
    {
        _turnTrace?.Controller(left, raw, accepted);
        _inputTrace?.Record("input-received", new
        {
            physicsFrame = Engine.GetPhysicsFrames(),
            side = left ? "left" : "right",
            raw = new[] { raw.X, raw.Y },
            accepted
        });
    }

    private void RecordStick(bool left, Vector2 raw, Vector2 quantised, bool changed, string origin) => _inputTrace?.Record("input-acknowledged", new
    {
        physicsFrame = Engine.GetPhysicsFrames(),
        side = left ? "left" : "right",
        raw = new[] { raw.X, raw.Y },
        rawLeft = new[] { _rawLeft.X, _rawLeft.Y },
        rawRight = new[] { _rawRight.X, _rawRight.Y },
        quantised = new[] { quantised.X, quantised.Y },
        acknowledged = new[] { _movement.X, _movement.Y },
        TurnIntent,
        submittedTurnIntent = ContinuousDriver?.SubmittedTurnIntent,
        playedTurnIntent = ContinuousDriver?.PlayedTurnIntent,
        changed,
        origin,
        epoch = _inputEpoch,
        actor = _actor is null ? null : TraceMatrix(_actor.GlobalTransform),
        referenceFrame = TraceMatrix(new Transform3D(_movementFrame, Vector3.Zero)),
        phase = Phase,
        playedFrame = ContinuousDriver?.RecordingFrame,
        request = _ticket,
        lastApplied = LastAppliedRequest,
        playing = ContinuousDriver?.IsPlaying ?? false
    });

    private void RecordSubmission(long ticket, Transform3D[][] context, Transform3D[][] target, string reason, double contextTime)
    {
        _inputTrace?.Record("submit", new
        {
            physicsFrame = Engine.GetPhysicsFrames(),
            epoch = _inputEpoch,
            ticket,
            reason,
            requestedTurnIntent = TurnIntent,
            submittedTurnIntent = ContinuousDriver?.SubmittedTurnIntent,
            metadataTurn = ContinuousDriver?.MetadataTurn is not null ? ContinuousDriver.LastMetadataTurnRequest : null,
            contextTime,
            observedPhysicsFrame = _observedPhysicsFrame,
            contextRoot = TraceMatrix(context[3][0]),
            targetWorld = TraceMatrix(WorldTarget(target[0][0])),
            virtualWorld = ContinuousDriver?.VirtualTarget is { } desired ? TraceMatrix(WorldTarget(desired)) : null,
            predictionSeconds = ContinuousDriver?.PredictionSeconds,
            generationLookaheadSeconds = WalkingExperimentDriver.GenerationLookaheadSeconds,
            generationWorld = ContinuousDriver?.GenerationTarget is { } guide ? TraceMatrix(WorldTarget(guide)) : null,
            requestedSourceVelocity = ContinuousDriver is { } driver ? new[] { driver.RequestedVelocity.X, driver.RequestedVelocity.Y, driver.RequestedVelocity.Z } : null,
            movement = new[] { _movement.X, _movement.Y },
            actor = TraceMatrix(_actor!.GlobalTransform),
            referenceFrame = TraceMatrix(new Transform3D(_movementFrame, Vector3.Zero)),
            lastApplied = LastAppliedRequest,
            playing = ContinuousDriver?.IsPlaying ?? false,
            sourceRole = ContinuousDriver?.ReviewedRoles.Role
        });
        RecordPoseSubmission(ticket, context, target, reason, contextTime);
    }

    private void RecordPlayback(string kind, long ticket) => _inputTrace?.Record(kind, new
    {
        physicsFrame = Engine.GetPhysicsFrames(),
        epoch = _inputEpoch,
        ticket,
        requestedTurnIntent = TurnIntent,
        submittedTurnIntent = ContinuousDriver?.SubmittedTurnIntent,
        playedTurnIntent = ContinuousDriver?.PlayedTurnIntent,
        actor = TraceMatrix(_actor!.GlobalTransform),
        phase = ContinuousDriver?.Phase ?? Phase,
        responseSeconds = ContinuousDriver?.LastResponseSeconds ?? LastResponseSeconds
        ,
        playedFrame = ContinuousDriver?.RecordingFrame
    });

    private void OnContinuousSubmitted(long ticket, Transform3D[][] context, Transform3D[][] target)
    {
        _ticket = ticket;
        SetContinuousPending(ticket, target[0][0]);
        RecordSubmission(ticket, context, target, ContinuousDriver!.RequestReason, ContinuousDriver.ObservedAt);
        WindowSubmitted?.Invoke(ticket, context, target);
    }

    private void OnContinuousStarted(long ticket)
    {
        StartContinuousMarker(ticket);
        RecordPlayback("consume", ticket);
    }

    private void OnContinuousCompleted(long ticket)
    {
        _activeMarkerCaption = $"ACTIVE completed / held #{ticket}\nGeneration guide (+Z forward)";
        _continuousMarkersDirty = true;
        RecordPlayback("completed", ticket);
    }

    private static Transform3D WorldTarget(Transform3D target) => new(SomaMotionData.ModelToWorld * target.Basis, SomaMotionData.ModelToWorld * target.Origin);
    private static float[] TraceMatrix(Transform3D t) => [t.Origin.X, t.Origin.Y, t.Origin.Z,
        t.Basis.X.X, t.Basis.X.Y, t.Basis.X.Z, t.Basis.Y.X, t.Basis.Y.Y, t.Basis.Y.Z, t.Basis.Z.X, t.Basis.Z.Y, t.Basis.Z.Z];
}
