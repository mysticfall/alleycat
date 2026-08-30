using AlleyCat.Core;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.Observation;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Polling faculty that observes the active look subject's position relative to the observing character and emits one
/// durable <see cref="ObservedRelativePosition"/> observation per material change (AI-006 TR-46..TR-52).
/// </summary>
/// <remarks>
/// The attach-time sample runs on the first process frame after a subject attaches, without waiting for
/// <see cref="PollingActiveLookPerception.PollIntervalSeconds"/>; afterwards the inherited polling cadence
/// re-examines the subject. At most one examination runs per frame in total. Geometry helpers operate only on
/// <see cref="ISpatial"/> transforms and are kept private to this faculty, which therefore adds no Vision dependency
/// for geometry (TR-52).
/// </remarks>
[GlobalClass]
public sealed partial class RelativePositionPerception : PollingActiveLookPerception
{
    private const float ZeroGroundSeparationSquared = 1e-6f;

    private readonly Dictionary<string, EmittedState> _lastEmitted = new(StringComparer.Ordinal);

    private bool _initialSamplePending;

    private ILogger<RelativePositionPerception>? _logger;

    /// <summary>
    /// Gets or sets the minimum absolute distance change in world units that counts as a material change and triggers
    /// a new emission for a focused subject. The value must be finite and non-negative; it is validated when a
    /// subject attaches, before sampling starts.
    /// </summary>
    [Export]
    public float MinimumDistanceChange
    {
        get;
        set;
    } = 0.1f;

    /// <summary>
    /// Gets or sets the ground-plane angle in degrees, measured from the reference facing, at or below which a
    /// bearing classifies as <see cref="RelativeDirection.Front"/>. The value must be finite and satisfy
    /// <c>0 &lt;= FrontAngleThresholdDegrees &lt;= BackAngleThresholdDegrees &lt;= 180</c>; it is validated when a
    /// subject attaches, before sampling starts.
    /// </summary>
    [Export]
    public float FrontAngleThresholdDegrees
    {
        get;
        set;
    } = 45f;

    /// <summary>
    /// Gets or sets the ground-plane angle in degrees, measured from the reference facing, at or above which a
    /// bearing classifies as <see cref="RelativeDirection.Back"/>. The value must be finite and satisfy
    /// <c>0 &lt;= FrontAngleThresholdDegrees &lt;= BackAngleThresholdDegrees &lt;= 180</c>; it is validated when a
    /// subject attaches, before sampling starts.
    /// </summary>
    [Export]
    public float BackAngleThresholdDegrees
    {
        get;
        set;
    } = 135f;

    /// <summary>
    /// Runs the pending attach-time sample immediately — without waiting for the poll interval — and otherwise
    /// defers to the inherited polling cadence. When the immediate sample runs, the base polling accumulation is
    /// skipped for that frame so at most one examination ever runs per frame; skipping one frame of accumulation
    /// only delays the next periodic poll by a single frame, which is imperceptible next to the poll interval.
    /// </summary>
    public override void _Process(double delta)
    {
        if (!HasValidTunables)
        {
            return;
        }

        if (_initialSamplePending && TryRunInitialSample())
        {
            return;
        }

        base._Process(delta);
    }

    /// <summary>
    /// Re-examines the active subject as one periodic poll. The implementation is fully synchronous and runs to
    /// completion on the main thread from StartPoll, so it cannot observe or leave torn intermediate state.
    /// </summary>
    protected override ValueTask PollActiveSubjectAsync(
        IVisualSubject subject,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        _ = TryExamine(subject, context);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    protected override void OnActiveSubjectAttached(IVisualSubject subject)
    {
        base.OnActiveSubjectAttached(subject);
        ValidateTunables();
        _initialSamplePending = true;
    }

    /// <inheritdoc />
    protected override void OnActiveSubjectDetached(IVisualSubject subject)
    {
        base.OnActiveSubjectDetached(subject);
        // The pending flag is cleared so a stale attach-time sample never runs after clear, replacement, or exit.
        // Emission-suppression state deliberately persists across focus cycles for the node's lifetime (TR-49).
        _initialSamplePending = false;
    }

    /// <summary>
    /// Attempts the pending attach-time sample under the same liveness guards as the polling cadence. Returns
    /// whether an examination consumed this frame's single examination slot; while spatial preconditions are unmet
    /// the pending flag stays set and the sample is retried on the next frame.
    /// </summary>
    private bool TryRunInitialSample()
    {
        IVisualSubject? subject = ActiveSubject;
        PerceptionContext? context = LatestContext;
        VisualCue? cue = ActiveCue;
        if (subject is null
            || context is null
            || cue is null
            || !IsLiveCue(cue)
            || FindNearestSubject(cue) is not IVisualSubject nearest
            || !ReferenceEquals(subject, nearest))
        {
            return false;
        }

        try
        {
            if (!TryExamine(subject, context))
            {
                return false;
            }
        }
        catch (Exception exception)
        {
            LogExaminationFault(exception);
        }

        _initialSamplePending = false;
        return true;
    }

    /// <summary>
    /// Samples the subject's position relative to the observer and emits a durable observation when the sample is
    /// materially different from the last emitted state. Returns whether both spatial preconditions were met and the
    /// sample ran; unmet preconditions skip sampling with no emission and no failure (TR-48).
    /// </summary>
    private bool TryExamine(IVisualSubject subject, PerceptionContext context)
    {
        if (!TryGetLiveSpatialTransform(subject, out Transform3D subjectTransform)
            || !TryGetLiveSpatialTransform(context.Character, out Transform3D observerTransform))
        {
            return false;
        }

        IdentityValidator.Validate(subject, nameof(subject));
        string subjectId = subject.FullId;

        float distance = subjectTransform.Origin.DistanceTo(observerTransform.Origin);
        RelativeDirection subjectDirection = ClassifyBearing(observerTransform, subjectTransform);
        RelativeDirection observerDirection = ClassifyBearing(subjectTransform, observerTransform);

        if (!IsMaterialChange(subjectId, distance, subjectDirection, observerDirection))
        {
            return true;
        }

        // The stored snapshot updates only on emission, so comparison is always against the last emitted state and
        // sub-threshold drift accumulates until it crosses the threshold relative to that snapshot (TR-49). All
        // dictionary access happens on the main thread: the immediate sample runs in _Process and polls start — and,
        // being fully synchronous, complete — inside _Process, so no cross-thread synchronisation is required.
        _lastEmitted[subjectId] = new EmittedState(distance, subjectDirection, observerDirection);
        Emit(new ObservedRelativePosition(subjectId, distance, subjectDirection, observerDirection));
        return true;
    }

    /// <summary>
    /// Determines whether a freshly sampled state differs materially from the last emitted state of the subject: the
    /// first-ever valid state is always material, and afterwards a material change is a distance drift of at least
    /// <see cref="MinimumDistanceChange"/> from the last emitted snapshot or either direction classification
    /// changing (TR-49).
    /// </summary>
    private bool IsMaterialChange(
        string subjectId,
        float distance,
        RelativeDirection subjectDirection,
        RelativeDirection observerDirection)
        => !_lastEmitted.TryGetValue(subjectId, out EmittedState last)
            || Math.Abs(distance - last.Distance) >= MinimumDistanceChange
            || subjectDirection != last.SubjectDirection
            || observerDirection != last.ObserverDirection;

    /// <summary>
    /// Classifies where <paramref name="to"/>'s origin lies relative to <paramref name="from"/>'s facing on the world
    /// ground plane (XZ). The tie-break order tests Front, then Back, then the signed lateral side, and zero
    /// horizontal separation classifies deterministically as <see cref="RelativeDirection.Front"/> (TR-51).
    /// </summary>
    private RelativeDirection ClassifyBearing(Transform3D from, Transform3D to)
    {
        Vector3 local = from.Inverse() * to.Origin;
        Vector3 groundBearing = new(local.X, 0f, local.Z);
        if (groundBearing.LengthSquared() < ZeroGroundSeparationSquared)
        {
            return RelativeDirection.Front;
        }

        float signedAngle = Vector3.Forward.SignedAngleTo(groundBearing.Normalized(), Vector3.Up);
        float magnitude = Mathf.RadToDeg(Math.Abs(signedAngle));
        return magnitude <= FrontAngleThresholdDegrees ? RelativeDirection.Front
            : magnitude >= BackAngleThresholdDegrees ? RelativeDirection.Back
            : signedAngle >= 0f ? RelativeDirection.Left
            : RelativeDirection.Right;
    }

    /// <summary>
    /// Resolves a transform through the spatial trait while preserving Godot lifetime safety for engine-backed
    /// providers. Plain trait providers have no Godot lifetime or tree membership to validate, whereas any Godot
    /// wrapper is validated before its transform is read and node-backed providers must also remain in the tree.
    /// </summary>
    private static bool TryGetLiveSpatialTransform(ISpatial spatial, out Transform3D transform)
    {
        transform = default;

        bool isInvalidGodotProvider = spatial is GodotObject godotObject && !IsInstanceValid(godotObject);
        bool isOutOfTreeNodeProvider = spatial is Node node && !node.IsInsideTree();
        if (isInvalidGodotProvider || isOutOfTreeNodeProvider)
        {
            return false;
        }

        transform = spatial.GlobalTransform;
        return true;
    }

    private bool HasValidTunables
        => HasValidMinimumDistanceChange && HasValidAngleThresholds;

    // The per-frame guard mirrors HasValidPollInterval: validation throws at attach for fail-fast diagnostics, and the
    // guard keeps sampling suppressed when an attach failed on invalid authored values.
    private bool HasValidMinimumDistanceChange => float.IsFinite(MinimumDistanceChange) && MinimumDistanceChange >= 0f;

    private bool HasValidAngleThresholds
        => float.IsFinite(FrontAngleThresholdDegrees)
            && float.IsFinite(BackAngleThresholdDegrees)
            && FrontAngleThresholdDegrees >= 0f
            && FrontAngleThresholdDegrees <= BackAngleThresholdDegrees
            && BackAngleThresholdDegrees <= 180f;

    private void ValidateTunables()
    {
        if (!HasValidMinimumDistanceChange)
        {
            throw new InvalidOperationException(
                $"'{GetType().Name}.MinimumDistanceChange' must be a finite, non-negative value; received "
                + $"'{MinimumDistanceChange}'.");
        }

        if (!HasValidAngleThresholds)
        {
            throw new InvalidOperationException(
                $"'{GetType().Name}.FrontAngleThresholdDegrees' and '{GetType().Name}.BackAngleThresholdDegrees' must be "
                + "finite and satisfy 0 <= FrontAngleThresholdDegrees <= BackAngleThresholdDegrees <= 180; received "
                + $"front '{FrontAngleThresholdDegrees}' and back '{BackAngleThresholdDegrees}'.");
        }
    }

    private void LogExaminationFault(Exception exception)
    {
        ILogger<RelativePositionPerception>? logger = TryGetLogger();
        if (logger?.IsEnabled(LogLevel.Error) == true)
        {
            logger.LogError(
                exception,
                "Immediate relative-position examination failed for perception node '{Path}'.",
                GetPath());
        }
    }

    private ILogger<RelativePositionPerception>? TryGetLogger()
    {
        if (_logger is null && GameLoggerResolver.TryResolve(out ILogger<RelativePositionPerception>? logger))
        {
            _logger = logger;
        }

        return _logger;
    }

    /// <summary>The last emitted relative-position snapshot for one subject.</summary>
    private readonly record struct EmittedState(
        float Distance,
        RelativeDirection SubjectDirection,
        RelativeDirection ObserverDirection);
}
