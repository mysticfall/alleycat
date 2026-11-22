using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Common;
using Godot;

namespace AlleyCat.Navigation.Recording;

/// <summary>Automatic observer, enabled only as a direct child of the existing desktop NAV playtest.</summary>
[GlobalClass]
public partial class NavigationPlaytestRecorder : Node
{
    internal const string PlaytestScene = "res://assets/testing/navigation/navigation_test.tscn";
    private NavigationRecordingSink? _sink;
    private MotionBricksNavigation? _navigation;
    private Node3D? _actor;
    private Skeleton3D? _skeleton;
    private Camera3D? _camera;
    private (MeshInstance3D Mesh, int Count)[] _meshes = [];
    private long _lastOutcome, _displayTicket;
    private double _displayFrame;
    private bool _rigRecorded;
    private string _lastPhase = "";
    private string _lastResult = "no destination yet", _lastAccepted = "none";
    private string? _startupFailure;
    private double _healthElapsed;
    private object? _view;
    private Task<bool>? _save;

    /// <summary>Absolute folder to share; empty outside the designated scene.</summary>
    public string SessionPath => _sink?.Writer.DirectoryPath ?? "";
    /// <summary>Session attempt numbers are distinct from accepted navigation request IDs and inference tickets.</summary>
    public long Attempt
    {
        get; private set;
    }
    /// <summary>Compact on-screen health, never a claim of complete data when loss/error is flagged.</summary>
    public string StatusText => _sink is null ? (_startupFailure ?? "Recorder disabled") :
        $"Recording: {(_sink.Writer.Error is not null ? "FAILED" : _sink.Writer.Dropped > 0 ? "LOSSY" : "active")} · dropped {_sink.Writer.Dropped}\n" +
        $"Attempt {Attempt}: {_lastResult}\nRequest {_navigation?.CurrentStatus.ActiveRequest?.RequestID.ToString() ?? "none"} · saved through {_sink.Writer.SavedThrough}" +
        (_sink.Writer.Saving || _save is { IsCompleted: false } ? " · saving…" : "") + $"\nSession {Path.GetFileName(SessionPath)[^8..]}" +
        (_sink.Writer.Error is { } error ? $"\nError: {error}" : "");

    /// <inheritdoc/>
    public override void _EnterTree()
    {
        if (Engine.IsEditorHint() || GetParent().SceneFilePath != PlaytestScene)
        {
            return;
        }
        try
        {
            string root = ProjectSettings.GlobalizePath("res://");
            string id = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ") + "-" + Guid.NewGuid().ToString("N");
            string? modelOverride = System.Environment.GetEnvironmentVariable("MOTIONBRICKS_MODEL");
            string model = string.IsNullOrWhiteSpace(modelOverride) ? "res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx" : modelOverride;
            string[] flags = ["MOTIONBRICKS_MODEL"];
            _sink = new(Path.Combine(root, "temp/motionbricks/navigation-playtests", id), new
            {
                session = id,
                scene = PlaytestScene,
                utc = DateTime.UtcNow,
                clockFrequency = Stopwatch.Frequency,
                engine = Engine.GetVersionInfo()["string"].AsString(),
                assembly = typeof(Game).Assembly.ManifestModule.ModuleVersionId,
                flags = flags.ToDictionary(k => k, System.Environment.GetEnvironmentVariable),
                transformLayout = "12 float32: basis columns X,Y,Z then origin; no output correction; bone globals are skeleton-space",
                precision = "Lossless float32 snapshots, only recorded physics/skin boundaries; not sole-contact or every rendered frame"
            }, root, ProjectSettings.GlobalizePath(model));
            NavigationRecordingSink sink = _sink;
            Game.Instance.RegisterDiagnosticDrain(() => sink.FinishAfterAsync(null));
            GetTree().NodeAdded += NodeAdded;
            foreach (Node child in GetParent().GetChildren())
            {
                FindNavigation(child);
            }
            Record("session-start", new
            {
            });
        }
        catch
        {
            _startupFailure = "Recording FAILED to start; gameplay remains available";
            _sink?.Writer.Fault("recorder-startup-failed");
        }
    }

    private void FindNavigation(Node node)
    {
        NodeAdded(node);
        foreach (Node child in node.GetChildren())
        {
            FindNavigation(child);
        }
    }

    private void NodeAdded(Node node)
    {
        if (_navigation is not null || node is not MotionBricksNavigation navigation || !GetParent().IsAncestorOf(node))
        {
            return;
        }
        try
        {
            _navigation = navigation;
            Character.Character character = navigation.GetParent<Character.Character>();
            _actor = character;
            _skeleton = character.RequireMotionTarget();
            _camera = GetParent().RequireNode<Camera3D>("Camera3D");
            // A value-only sink method, not a closure over this Node, is captured by the native worker.
            navigation.ReturnedMotion = _sink!.Returned;
            navigation.WindowSubmitted += Submitted;
            navigation.PoseObserved += Observed;
            navigation.NativeHandoffObserved += Handoff;
            navigation.PlaybackObserved += Playback;
            navigation.OrdinaryRefusalObserved += Refusal;
            _skeleton.SkeletonUpdated += ObserveFailedHold;
        }
        catch { _sink?.Writer.Fault("recorder-binding-failed"); }
    }

    private void Record(string kind, object data)
    {
        _ = _sink?.Writer.Record(kind, new
        {
            tick = Engine.GetPhysicsFrames(),
            time = _navigation?.ObservationTime,
            attempt = Attempt,
            status = _navigation?.CurrentStatus,
            view = _view,
            data
        });
    }

    private void Refusal(JsonElement snapshot)
    {
        try
        {
            Record("navigation-refusal", snapshot);
        }
        catch { _sink?.Writer.Fault("refusal-observer-failed"); }
    }

    /// <summary>Called before the normal adapter consumes the full transform and ordered vias.</summary>
    public void BeforeCommand(Transform3D destination, Vector3[] vias)
    {
        Attempt++;
        _lastResult = "submitting";
        Record("command-input", new
        {
            requested = NavigationRecordingSink.Pack([destination]),
            vias = vias.Select(v => new[] { v.X, v.Y, v.Z }).ToArray(),
            actor = NavigationRecordingSink.Pack([_actor?.GlobalTransform ?? Transform3D.Identity]),
            measured = NavigationRecordingSink.Pack([_navigation?.RecordingMeasuredTransform ?? Transform3D.Identity]),
            anatomicalHeadingWorld = _navigation?.RecordingAnatomicalHeading,
            headingConvention = "world forward = (sin heading,0,cos heading); anatomical hip pair, not actor/skeleton yaw",
            replaces = _navigation?.CurrentStatus,
            speed = _navigation?.NormalisedSpeed
        });
    }

    /// <summary>Records acceptance or refusal without changing the adapter's result.</summary>
    public void AfterCommand(int result)
    {
        _lastResult = ((NavigationDestinationResult)result).ToString();
        if (result == (int)NavigationDestinationResult.Accepted)
        {
            _lastAccepted = _navigation?.CurrentStatus.ActiveRequest?.ToString() ?? "completed (see outcomes)";
        }
        Record("command-result", new
        {
            result = ((NavigationDestinationResult)result).ToString()
        });
        Outcomes();
    }

    /// <summary>Record UI intent immediately before cancellation or speed mutation.</summary>
    public void Control(string kind, double value = 0) => Record(kind, new { value });
    /// <summary>Bookmark even a completed-but-visually-bad request, tied to the numbered attempt.</summary>
    public void MarkIssue()
    {
        Outcomes();
        Record("bookmark", new
        {
            lastOutcome = _lastOutcome,
            lastAccepted = _lastAccepted,
            ticket = _displayTicket
        });
    }
    /// <summary>Seal a durable checkpoint asynchronously; gameplay and recording continue.</summary>
    public void SaveFeedback()
    {
        Outcomes();
        Record("save-feedback", new
        {
        });
        if (_sink is not null && _save is not { IsCompleted: false })
        {
            _save = _sink.CheckpointAsync();
        }
    }

    /// <summary>Exact desktop camera controller state, aligned with event and pose timestamps.</summary>
    public void ViewState(Vector3 origin, double distance, double yaw, double pitch, bool orbiting, bool panning)
    {
        _view = new
        {
            origin = new[] { origin.X, origin.Y, origin.Z },
            distance,
            yaw,
            pitch,
            orbiting,
            panning
        };
        Record("view", new
        {
            camera = NavigationRecordingSink.Pack([(_camera ?? GetParent().GetNodeOrNull<Camera3D>("Camera3D"))?.GlobalTransform ?? Transform3D.Identity]),
            viewport = new[] { GetViewport().GetVisibleRect().Size.X, GetViewport().GetVisibleRect().Size.Y }
        });
    }

    private void Playback(long ticket, Transform3D[][]? poses, double frame)
    {
        _displayTicket = ticket;
        _displayFrame = frame;
    }

    private void Submitted(long ticket, Transform3D[][] context, Transform3D[][] targets)
    {
        try
        {
            _ = _sink!.Writer.Record("submission", new
            {
                tick = Engine.GetPhysicsFrames(),
                time = _navigation!.ObservationTime,
                attempt = Attempt,
                status = _navigation.CurrentStatus,
                ticket,
                role = _navigation.SubmittedRole,
                terminalTurnPlan = _navigation.TerminalTurnSnapshot,
                intent = _navigation.SourceRateCurveIntent,
                placement = _navigation.PositioningPlacementIntent,
                contextShape = context.Select(p => p.Length).ToArray(),
                targetShape = targets.Select(p => p.Length).ToArray()
            },
                checked((context.Sum(p => p.Length) + targets.Sum(p => p.Length)) * 12),
                () => NavigationRecordingSink.Pack([.. context, .. targets]));
        }
        catch { _sink!.Writer.Fault("submission-observer-failed"); }
    }

    private void Handoff(MotionBricksNavigation.NativeHandoffObservation handoff)
    {
        Record("handoff", new
        {
            handoff.PreviousRequest,
            handoff.Request,
            handoff.Revision,
            handoff.GameTime,
            handoff.ContextAt,
            handoff.Age,
            handoff.OldRemaining,
            handoff.PublishedAt,
            handoff.ConsumedAt,
            handoff.Accepted,
            handoff.Reason,
            handoff.Arrival,
            handoff.Curve
        });
    }

    private void Outcomes()
    {
        if (_navigation is null)
        {
            return;
        }
        foreach (NavigationTerminalOutcomeRecord outcome in _navigation.TerminalOutcomes)
        {
            if (outcome.Sequence <= _lastOutcome)
            {
                continue;
            }
            Record("outcome", outcome);
            _lastOutcome = outcome.Sequence;
        }
    }

    /// <inheritdoc/>
    public override void _Process(double delta)
    {
        if (_sink is null)
        {
            return;
        }
        _healthElapsed += delta;
        if (_healthElapsed < .2)
        {
            return;
        }
        _healthElapsed = 0;
        Outcomes();
        if (_navigation is not null && _lastPhase != _navigation.Phase + _navigation.Failure)
        {
            _lastPhase = _navigation.Phase + _navigation.Failure;
            Record("phase", new
            {
                _navigation.Phase,
                _navigation.Failure
            });
        }
    }

    private void Observed()
    {
        try
        {
            MotionBricksNavigation nav = _navigation!;
            Skeleton3D skeleton = _skeleton!;
            if (!_rigRecorded)
            {
                _rigRecorded = true;
                _meshes = [.. Descendants(_actor!).OfType<MeshInstance3D>().Where(m => m.Mesh is not null)
                    .Select(m => (m, m.GetBlendShapeCount())).Where(p => p.Item2 > 0)];
                Record("rig", new
                {
                    canonicalNames = nav.GetRecordingPoseLayout()?.Names,
                    canonicalParents = nav.GetRecordingPoseLayout()?.Parents,
                    canonicalRest = nav.GetRecordingPoseLayout() is { } layout ? NavigationRecordingSink.Pack(layout.Rest) : [],
                    camera = new
                    {
                        _camera!.Fov,
                        _camera.Size,
                        _camera.Near,
                        _camera.Far,
                        projection = _camera.Projection.ToString(),
                        keepAspect = _camera.KeepAspect.ToString()
                    },
                    bones = Enumerable.Range(0, skeleton.GetBoneCount()).Select(i => new
                    {
                        name = skeleton.GetBoneName(i).ToString(),
                        parent = skeleton.GetBoneParent(i),
                        rest = NavigationRecordingSink.Pack([skeleton.GetBoneRest(i)])
                    }).ToArray(),
                    blends = _meshes.Select(m => new
                    {
                        path = _actor!.GetPathTo(m.Mesh).ToString(),
                        count = m.Count,
                        names = Enumerable.Range(0, m.Count).Select(i => m.Mesh.Mesh is ArrayMesh mesh ? mesh.GetBlendShapeName(i).ToString() : i.ToString()).ToArray()
                    }).ToArray(),
                    policy = new
                    {
                        nav.ModelPath,
                        nav.RoleComponentDeadzone,
                        nav.PositioningEnterDistance,
                        nav.PositioningExitDistance,
                        nav.PositioningFacingDegrees,
                        nav.PositioningLateralBandDegrees,
                        nav.PositioningHysteresisDegrees,
                        nav.PositioningCrossingDistance,
                        nav.FacingToleranceDegrees,
                        nav.DestinationReachedDistance
                    },
                    layout = "pose bulk: actorWorld, skeletonWorld, cameraWorld, generatedInput[n], calibratedFeedback[n], finalSkeletonSpaceGlobalBones[b], blendWeights[count]; each transform 12 floats. Canonical pose names/parents/rest and final bones from this rig event."
                });
            }
            Outcomes();
            if (_lastPhase != nav.Phase + nav.Failure)
            {
                _lastPhase = nav.Phase + nav.Failure;
                Record("phase", new
                {
                    nav.Phase,
                    nav.Failure
                });
            }
            int n = nav.GeneratedInputPose.Length, b = skeleton.GetBoneCount(), blends = _meshes.Sum(m => m.Count);
            _ = _sink!.Writer.Record("pose", new
            {
                tick = Engine.GetPhysicsFrames(),
                time = nav.ObservationTime,
                attempt = Attempt,
                status = nav.CurrentStatus,
                nav.Phase,
                nav.NativePhase,
                ticket = _displayTicket,
                frame = _displayFrame,
                feedbackSamples = nav.SamplesRecorded,
                feedbackIsCurrentSkinSample = nav.Phase != "failed",
                generatedBones = n,
                skeletonBones = b,
                blendWeights = blends,
                role = nav.SubmittedRole,
                terminalTurnPlan = nav.TerminalTurnSnapshot,
                view = _view,
                viewport = new[] { GetViewport().GetVisibleRect().Size.X, GetViewport().GetVisibleRect().Size.Y }
            },
                ((3 + (2 * n) + b) * 12) + blends, () =>
                {
                    float[] values = new float[((3 + (2 * n) + b) * 12) + blends];
                    int offset = 0;
                    NavigationRecordingSink.Put(values, ref offset, _actor!.GlobalTransform);
                    NavigationRecordingSink.Put(values, ref offset, skeleton.GlobalTransform);
                    NavigationRecordingSink.Put(values, ref offset, _camera!.GlobalTransform);
                    foreach (Transform3D pose in nav.GeneratedInputPose)
                    {
                        NavigationRecordingSink.Put(values, ref offset, pose);
                    }
                    foreach (Transform3D pose in nav.RealisedFeedback)
                    {
                        NavigationRecordingSink.Put(values, ref offset, pose);
                    }
                    for (int i = 0; i < b; i++)
                    {
                        NavigationRecordingSink.Put(values, ref offset, skeleton.GetBoneGlobalPose(i));
                    }
                    foreach ((MeshInstance3D mesh, int count) in _meshes)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            values[offset++] = mesh.GetBlendShapeValue(i);
                        }
                    }
                    return values;
                });
        }
        catch { _sink?.Writer.Fault("pose-observer-failed"); }
    }

    private void ObserveFailedHold()
    {
        // Failed navigation intentionally stops committing history, but the visible modifiers/camera still run.
        // Preserve those final bones while truthfully labelling calibrated feedback as the last committed sample.
        if (_navigation is { Phase: "failed" } nav && !nav.IsRecordingObservationPending() && nav.GeneratedInputPose.Length != 0)
        {
            Observed();
        }
    }

    private static IEnumerable<Node> Descendants(Node parent)
    {
        foreach (Node child in parent.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <inheritdoc/>
    public override void _ExitTree()
    {
        if (_sink is null)
        {
            return;
        }
        GetTree().NodeAdded -= NodeAdded;
        Outcomes();
        Record("scene-exit", new
        {
        });
        if (_navigation is not null)
        {
            _navigation.WindowSubmitted -= Submitted;
            _navigation.PoseObserved -= Observed;
            _navigation.NativeHandoffObserved -= Handoff;
            _navigation.PlaybackObserved -= Playback;
            _navigation.OrdinaryRefusalObserved -= Refusal;
        }
        if (_skeleton is not null && IsInstanceValid(_skeleton))
        {
            _skeleton.SkeletonUpdated -= ObserveFailedHold;
        }
        _ = _sink.FinishAfterAsync(_navigation?.WorkerCompletion);
    }
}
