using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Navigation.Arrival;

/// <summary>Movement family the arrival planner chose for a request (NAV-001 User Requirement 3, A5).</summary>
public enum ArrivalMovementKind
{
    /// <summary>Already within the arrival tolerances: nothing to play.</summary>
    None,

    /// <summary>Heading-only request: whole or partial pivots.</summary>
    TurnOnly,

    /// <summary>Nearby lateral target: a side-step, then the final pivot.</summary>
    SideStep,

    /// <summary>Target behind, or wide and near: pivot to face it, walk straight, then the final pivot.</summary>
    TurnFirst,

    /// <summary>Distant wide target: an optional lead pivot, an arc into forward walking, then the final pivot.</summary>
    Arc,

    /// <summary>Target ahead: an optional alignment pivot, a straight walk (or slice pair), then the final pivot.</summary>
    Straight,

    /// <summary>
    /// A nonterminal route section (NAV-001 User Requirement 8 route follow-through): travel toward an
    /// intermediate route station through native walk/arc seams, ending mid-travel with no exit, Idle tail or
    /// facing pivot. Final facing, terminal stop, arrival correction and completion belong to the final
    /// approach plan alone.
    /// </summary>
    TravelSection,

    /// <summary>A replan from a travelling clip that keeps travelling to the target, then the final pivot.</summary>
    ContinueTravel,
}

/// <summary>
/// The A5 movement choice of the NAV-001 P4 design (Q5), from the target's offset and the heading change in the
/// actor's frame. Thresholds come from <see cref="ArrivalPlannerConfig"/>.
/// </summary>
public static class ArrivalMovementChoice
{
    /// <summary>Bearing of an actor-semantic offset, in radians (positive left of forward).</summary>
    public static double BearingOf(double right, double forward) => Math.Atan2(-right, forward);

    /// <summary>Chooses the movement family for a target.</summary>
    /// <param name="right">Target offset to the actor's right, in metres.</param>
    /// <param name="forward">Target offset ahead of the actor, in metres.</param>
    /// <param name="yawChange">Heading change to the final facing, in radians (positive left).</param>
    /// <param name="config">Thresholds.</param>
    public static ArrivalMovementKind Choose(double right, double forward, double yawChange, ArrivalPlannerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        double distance = Math.Sqrt((right * right) + (forward * forward));
        double turn = Math.Abs(Mathf.Wrap(yawChange, -Math.PI, Math.PI));
        if (distance <= config.ArrivedDistanceMetres && turn <= config.ArrivedYawRadians)
        {
            return ArrivalMovementKind.None;
        }

        if (distance <= config.TurnOnlyDistanceMetres)
        {
            return ArrivalMovementKind.TurnOnly;
        }

        if (Math.Abs(right) > config.SideStepLateralRatio * Math.Abs(forward)
            && distance <= config.SideStepMaxDistanceMetres
            && turn <= config.SideStepMaxYawChangeRadians)
        {
            return ArrivalMovementKind.SideStep;
        }

        double bearing = Math.Abs(BearingOf(right, forward));
        return bearing > config.TurnFirstBearingRadians
            || (bearing > config.NearTurnFirstBearingRadians && distance < config.ArcMinDistanceMetres)
            ? ArrivalMovementKind.TurnFirst
            : bearing > config.NearTurnFirstBearingRadians ? ArrivalMovementKind.Arc : ArrivalMovementKind.Straight;
    }
}

/// <summary>
/// Where an arrival plan starts: the clip the NPC is playing (Idle when standing), its playback position after the
/// last tick, and a crossfade into it that may still be running.
/// </summary>
/// <param name="Pose">Actor pose after the last tick.</param>
/// <param name="Role">Role of the playing clip.</param>
/// <param name="PositionSeconds">Its playback position after the last tick (on the physics tick grid of its seams).</param>
/// <param name="Fading">A crossfade into it that is still running, if any.</param>
public sealed record ArrivalStart(SequencePose Pose, string Role, double PositionSeconds, SequenceCrossfadeProgress? Fading = null)
{
    /// <summary>
    /// Whether the first transition out of a non-Idle clip must begin at its first aligned seam into the planner's
    /// successors of that clip (NAV-001 Technical Requirement 14 cancellation and supersession). Ignored for Idle.
    /// </summary>
    public bool Prompt
    {
        get; init;
    }
}

/// <summary>
/// A destination: planar world position <c>(X, Z)</c> and final facing yaw. A request is one final target today; via
/// points can later become preceding legs of the same type.
/// </summary>
/// <param name="Position">World <c>(X, Z)</c> destination.</param>
/// <param name="Yaw">Final facing yaw about world +Y, in radians (positive left).</param>
public readonly record struct ArrivalTarget(Vector2 Position, float Yaw);

/// <summary>Outcome of planning.</summary>
public enum ArrivalPlanStatus
{
    /// <summary>A plan of aligned seams was found.</summary>
    Planned,

    /// <summary>No plan (NAV-001 Technical Requirement 13 <c>Failed</c>); see <see cref="ArrivalPlanFailure"/>.</summary>
    Failed,
}

/// <summary>Why planning failed.</summary>
public enum ArrivalPlanFailure
{
    /// <summary>Not failed.</summary>
    None,

    /// <summary>The start clip or its running crossfade is not a plannable state.</summary>
    InvalidStart,

    /// <summary>No candidate sequence of aligned seams reaches Idle (TR7 "no aligned seam").</summary>
    NoAlignedPlan,

    /// <summary>The work budget ran out before any candidate was found.</summary>
    BudgetExhausted,
}

/// <summary>Stage of a plan segment.</summary>
public enum ArrivalStage
{
    /// <summary>Leaving the clip playing at the start, to Idle or into continued travel.</summary>
    Prefix,

    /// <summary>Pivot that aligns the NPC with its path before travelling.</summary>
    Align,

    /// <summary>The travel (walk, arc or side-step).</summary>
    Travel,

    /// <summary>Final pivot to the requested facing (the final approach).</summary>
    Face,
}

/// <summary>One planned clip segment and the join that enters it.</summary>
/// <param name="Role">Clip role.</param>
/// <param name="Kind">Planner role of the clip.</param>
/// <param name="Stage">Plan stage.</param>
/// <param name="EntrySeconds">Seek position <c>b</c> of the entering seam.</param>
/// <param name="CrossfadeSeconds">The entering pair's crossfade duration <c>d</c>.</param>
/// <param name="ExitSeconds">Outgoing time <c>a</c> of the leaving seam (for the last segment, where the plan ends).</param>
/// <param name="ExitCycles">Extra whole cycles of a looping clip before it leaves.</param>
/// <param name="EntrySeparation">Pose separation of the entering seam, in metres.</param>
public readonly record struct ArrivalPlanSegment(
    string Role,
    SeamClipRole Kind,
    ArrivalStage Stage,
    double EntrySeconds,
    double CrossfadeSeconds,
    double ExitSeconds,
    int ExitCycles,
    float EntrySeparation)
{
    /// <summary>The predictor's view of the segment.</summary>
    public SequenceSegment ToSequenceSegment() => new(Role, EntrySeconds, CrossfadeSeconds, ExitSeconds, ExitCycles);
}

/// <summary>Work and time an arrival plan took.</summary>
/// <param name="Compositions">Candidate compositions evaluated with the endpoint model.</param>
/// <param name="SummaryPredictions">Finalists predicted exactly in summary mode.</param>
/// <param name="BudgetExhausted">Whether the composition budget ran out.</param>
/// <param name="EstimateErrorMetres">
/// Difference between the chosen plan's endpoint-model cost and its exact cost, in metres of residual cost.
/// </param>
/// <param name="SearchMilliseconds">Wall-clock time of the candidate search.</param>
/// <param name="ElapsedMilliseconds">Wall-clock planning time, including the exact verification of finalists.</param>
public readonly record struct ArrivalPlanStatistics(
    int Compositions,
    int SummaryPredictions,
    bool BudgetExhausted,
    double EstimateErrorMetres,
    double SearchMilliseconds,
    double ElapsedMilliseconds);

/// <summary>
/// An arrival plan: a sequence of clip segments joined only at aligned seams of the planner set, ending in Idle, with
/// its exact prediction, or a failure with its reason.
/// </summary>
public sealed class ArrivalPlan
{
    internal ArrivalPlan(
        ArrivalStart start,
        ArrivalTarget target,
        ArrivalMovementKind kind,
        SequenceStart sequenceStart,
        ImmutableArray<ArrivalPlanSegment> segments,
        SequenceSummary summary,
        ArrivalPlanStatistics statistics)
    {
        Start = start;
        Target = target;
        Status = ArrivalPlanStatus.Planned;
        Kind = kind;
        SequenceStart = sequenceStart;
        Segments = segments;
        Summary = summary;
        Statistics = statistics;
    }

    internal ArrivalPlan(ArrivalStart start, ArrivalTarget target, ArrivalPlanFailure failure, string detail, ArrivalPlanStatistics statistics)
    {
        Start = start;
        Target = target;
        Status = ArrivalPlanStatus.Failed;
        Failure = failure;
        FailureDetail = detail;
        Segments = [];
        Statistics = statistics;
    }

    /// <summary>Plan start.</summary>
    public ArrivalStart Start
    {
        get;
    }

    /// <summary>The destination planned for.</summary>
    public ArrivalTarget Target
    {
        get;
    }

    /// <summary>Planned or failed.</summary>
    public ArrivalPlanStatus Status
    {
        get;
    }

    /// <summary>Why planning failed.</summary>
    public ArrivalPlanFailure Failure
    {
        get;
    }

    /// <summary>Failure details.</summary>
    public string FailureDetail { get; } = string.Empty;

    /// <summary>Movement family (A5).</summary>
    public ArrivalMovementKind Kind
    {
        get;
    }

    /// <summary>
    /// The predictor's start: the playing clip, with the exit at which the first segment's transition is requested.
    /// </summary>
    public SequenceStart? SequenceStart
    {
        get;
    }

    /// <summary>Segments after the playing clip, in playback order; the last one is Idle.</summary>
    public ImmutableArray<ArrivalPlanSegment> Segments
    {
        get;
    }

    /// <summary>The predictor's segments.</summary>
    public ImmutableArray<SequenceSegment> SequenceSegments => [.. Segments.Select(segment => segment.ToSequenceSegment())];

    /// <summary>
    /// Exact prediction of the plan (summary mode, with its support windows). The per-tick prediction is
    /// <see cref="ArrivalPlanner.Predict"/>.
    /// </summary>
    public SequenceSummary? Summary
    {
        get;
    }

    /// <summary>Number of ticks the plan plays, until its final Idle crossfade has ended.</summary>
    public int TickCount => Summary?.TickCount ?? 0;

    /// <summary>Predicted actor pose when the final Idle crossfade has ended.</summary>
    public SequencePose PredictedEnd => Summary?.EndPose ?? Start.Pose;

    /// <summary>World <c>(X, Z)</c> residual from the predicted end to the target.</summary>
    public Vector2 PlanarResidual => Target.Position - PredictedEnd.Position;

    /// <summary>Heading residual from the predicted end to the target facing, in radians (wrapped).</summary>
    public float YawResidual => Mathf.Wrap(Target.Yaw - PredictedEnd.Yaw, -Mathf.Pi, Mathf.Pi);

    /// <summary>
    /// Support windows of the prediction: single-swing (one foot swinging) and both-swing windows are where unit 6's
    /// swing-phase correction may act.
    /// </summary>
    public ImmutableArray<SupportWindow> Windows => Summary?.Windows ?? [];

    /// <summary>Largest seam separation of the plan's joins, in metres.</summary>
    public float MaxSeparation => Segments.IsEmpty ? 0f : Segments.Max(segment => segment.EntrySeparation);

    /// <summary>Work and time taken.</summary>
    public ArrivalPlanStatistics Statistics
    {
        get;
    }
}
