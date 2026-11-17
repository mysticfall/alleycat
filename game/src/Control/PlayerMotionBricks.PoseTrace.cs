using System.Diagnostics;
using System.Runtime.CompilerServices;
using AlleyCat.IK;
using AlleyCat.Navigation.Recording;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

public partial class PlayerMotionBricks
{
    /// <summary>Binary companion session for the latest opt-in input trace; empty when disabled.</summary>
    public string InputPoseTracePath { get; private set; } = "";
    internal string PoseTraceEvidenceLabel { get; set; } = "runtime-provider";
    private PlayerPoseRecording? _poseRecording;
    // The worker captures only this value-only recorder link, never this player node.
    private readonly StrongBox<PlayerPoseRecording?> _poseRecordingLink = new();
    private Task? _poseTraceCompletion;
    private PlayerVRIK? _recordingVRIK;

    private void StartPoseTrace()
    {
        if (!RecordInputTrace || _inputTrace is null || _poseRecording is not null || _data is null || _skeleton is null || _actor is null)
        {
            return;
        }
        try
        {
            InputPoseTracePath = Path.ChangeExtension(InputTracePath, ".motion");
            string[] flags = ["PLAYER_ANIMATION_BODY", "PLAYER_MOTION_TARGET", "PLAYER_MOTION_TRACE", "MOTIONBRICKS_MODEL"];
            string? configured = System.Environment.GetEnvironmentVariable("MOTIONBRICKS_MODEL");
            string model = ProjectSettings.GlobalizePath(string.IsNullOrWhiteSpace(configured) ? ModelPath : configured);
            _recordingVRIK = _actor.GetNodeOrNull<PlayerVRIK>("VRIK");
            var recording = new PlayerPoseRecording(InputPoseTracePath, new
            {
                controllerSchema = "alleycat-player-footwork-v1",
                evidence = PoseTraceEvidenceLabel,
                transport = "NavigationSessionWriter sealed JSONL/ACNAV001 binary container; not NAV gameplay semantics",
                legacyTrace = Path.GetFileName(InputTracePath),
                utc = DateTime.UtcNow,
                engine = Engine.GetVersionInfo()["string"].AsString(),
                assembly = typeof(Game).Assembly.ManifestModule.ModuleVersionId,
                clockFrequency = Stopwatch.Frequency,
                physicsTicksPerSecond = Engine.PhysicsTicksPerSecond,
                flags = flags.ToDictionary(k => k, System.Environment.GetEnvironmentVariable),
                effective = new
                {
                    stableInputChaining = true,
                    reviewedRoles = ContinuousDriver?.ReviewedRoles is not null,
                    reconcile = ContinuousDriver?.Reconcile,
                    normalVRIK = _recordingVRIK is not null && !_recordingVRIK.AnimationBodyDiagnostic,
                    stationaryTurnCompatibility = "reviewed virtual CTRL only; exact same-sign .95/1 bins against live submitted/played owners",
                    WalkingSteeringDegreesPerSecond,
                    Deadzone,
                    rightInputQuantisation = .05,
                    generationHorizon = WalkingExperimentDriver.GenerationLookaheadSeconds
                },
                precision = "Lossless float32 snapshots at submission/return/SkeletonUpdated; no rendered-pixel or sole/contact assertion",
                rawPreEndpoint = "not recorded; provider-returned is processed output",
                xrOrigin = "not recorded; optional active viewport camera and VRIK compensation state are separate observations"
            }, ProjectSettings.GlobalizePath("res://"), model);
            _poseRecording = recording;
            Volatile.Write(ref _poseRecordingLink.Value, recording);
            _inputTrace.AttachPoses(recording);
            Game.Instance.RegisterDiagnosticDrain(recording.FinishAfterWorkersAsync);
            recording.Record("rig", new
            {
                skeletonMotionScale = _skeleton.MotionScale,
                canonicalNames = _data.Names,
                canonicalParents = _data.Parents,
                canonicalRest = NavigationRecordingSink.Pack(_data.Rest),
                bones = Enumerable.Range(0, _skeleton.GetBoneCount()).Select(i => new
                {
                    name = _skeleton.GetBoneName(i).ToString(),
                    parent = _skeleton.GetBoneParent(i),
                    rest = NavigationRecordingSink.Pack([_skeleton.GetBoneRest(i)])
                }).ToArray(),
                transformLayout = "All bulk transforms: 12 float32 basis columns X,Y,Z then origin. Canonical FK uses canonicalParents; canonical->world is ModelToWorld pi yaw. Final bones are skeleton-space globals: multiply by recorded skeletonWorld. Anatomical heading derives from hip pairs, not root Basis.Z.",
                skinLayout = "actorWorld,skeletonWorld,activeViewportCameraWorld,rawGenerated[n],suppliedAfterReconciliation[n],calibratedActualFeedback[n],finalSkeletonSpaceGlobals[b]. Camera identity when unavailable; generated slots invalid until generatedAvailable."
            });
            _logger!.LogInformation("Player footwork companion recording: {Path}", InputPoseTracePath);
        }
        catch (Exception error)
        {
            _poseRecording?.Writer.Fault("start-" + error.GetType().Name);
            _logger!.LogError(error, "Player footwork recording could not start; locomotion unchanged");
        }
    }

    private void RecordPoseSubmission(long ticket, Transform3D[][] context, Transform3D[][] target, string reason, double contextTime)
    {
        PlayerPoseRecording? recording = _poseRecording;
        if (recording is null)
        {
            return;
        }
        try
        {
            recording.Motion("submission", new
            {
                ticket,
                epoch = _inputEpoch,
                clock = Stopwatch.GetTimestamp(),
                physicsFrame = Engine.GetPhysicsFrames(),
                playerTime = _time,
                requestedTurnIntent = TurnIntent,
                submittedTurnIntent = ContinuousDriver?.SubmittedTurnIntent,
                contextTime,
                reason,
                horizon = ContinuousDriver is not null ? WalkingExperimentDriver.GenerationLookaheadSeconds : (double?)null,
                source = ContinuousDriver?.ReviewedRoles?.RecordingSnapshot(),
                metadataTurn = ContinuousDriver?.MetadataTurn is not null ? ContinuousDriver.LastMetadataTurnRequest : null,
                legacySourceSelection = ContinuousDriver?.LastReviewedRequest,
                contextShape = context.Select(p => p.Length).ToArray(),
                targetShape = target.Select(p => p.Length).ToArray(),
                layout = "four complete canonical context poses followed by four complete placed target poses; raw source reconstructed only against recorded bundle hash/original-frame identities"
            }, [.. context, .. target]);
        }
        catch (Exception error) { recording.Writer.Fault("submission-" + error.GetType().Name); }
    }

    private void RecordSkinPose()
    {
        PlayerPoseRecording? recording = _poseRecording;
        if (recording is null)
        {
            return;
        }
        try
        {
            Skeleton3D skeleton = _skeleton!;
            WalkingExperimentDriver? driver = ContinuousDriver;
            Camera3D? camera = GetViewport().GetCamera3D();
            int n = _observed.Length, b = skeleton.GetBoneCount();
            recording.Record("skin-pose", new
            {
                clock = Stopwatch.GetTimestamp(),
                physicsFrame = Engine.GetPhysicsFrames(),
                playerTime = _time,
                observedTime = _observedTime,
                driverTime = driver?.ObservedAt,
                epoch = _inputEpoch,
                submittedTicket = _ticket,
                requestedTurnIntent = TurnIntent,
                submittedTurnIntent = driver?.SubmittedTurnIntent,
                playedTurnIntent = driver?.PlayedTurnIntent,
                playedTicket = driver?.LastAppliedRequest ?? LastAppliedRequest,
                playedFrame = driver?.RecordingFrame,
                advancedThisTick = driver?.RecordingAdvanced,
                playing = driver?.IsPlaying ?? false,
                pending = driver?.RecordingPending ?? false,
                generatedAvailable = driver is not null && LastAppliedRequest > 0 && driver.RecordingFrame >= 0,
                Phase,
                Failure,
                generatedBones = n,
                skeletonBones = b,
                cameraAvailable = camera is not null,
                xrCompensationApplied = _recordingVRIK?.XRCompensationApplied,
                boundary = "SkeletonUpdated after all modifiers; final skin-ready bones plus freshly observed calibrated feedback, never source/target as feedback"
            }, (3 + (3 * n) + b) * 12, () =>
            {
                float[] values = new float[(3 + (3 * n) + b) * 12];
                int offset = 0;
                NavigationRecordingSink.Put(values, ref offset, _actor!.GlobalTransform);
                NavigationRecordingSink.Put(values, ref offset, skeleton.GlobalTransform);
                NavigationRecordingSink.Put(values, ref offset, camera?.GlobalTransform ?? Transform3D.Identity);
                foreach (Transform3D pose in driver?.RecordingRawPose ?? _pose)
                {
                    NavigationRecordingSink.Put(values, ref offset, pose);
                }
                foreach (Transform3D pose in SuppliedPose)
                {
                    NavigationRecordingSink.Put(values, ref offset, pose);
                }
                foreach (Transform3D pose in _observed)
                {
                    NavigationRecordingSink.Put(values, ref offset, pose);
                }
                for (int i = 0; i < b; i++)
                {
                    NavigationRecordingSink.Put(values, ref offset, skeleton.GetBoneGlobalPose(i));
                }
                return values;
            });
        }
        catch (Exception error) { recording.Writer.Fault("skin-" + error.GetType().Name); }
    }

    private void StopPoseTrace(Task? worker, bool toggle)
    {
        if (_poseRecording is not { } recording)
        {
            return;
        }
        recording.Record("capture-stop", new
        {
            toggle,
            pending = ContinuousDriver?.RecordingPending ?? false,
            meaning = toggle ? "Explicit toggle ends capture now; late returns may be outside capture" : "Scene exit; wait off-thread for this worker before finalising"
        });
        if (toggle && (ContinuousDriver?.RecordingPending ?? false))
        {
            recording.Writer.Fault("toggle-stop-with-pending-output");
        }
        _poseTraceCompletion = recording.FinishAfterAsync(worker);
        if (toggle)
        {
            _inputTrace?.AttachPoses(null);
            Volatile.Write(ref _poseRecordingLink.Value, null);
            _poseRecording = null;
        }
    }
}
