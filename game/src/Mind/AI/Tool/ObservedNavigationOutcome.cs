using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Navigation;
using AgentObservation = AlleyCat.Mind.Observation.Observation;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>The coordinate navigation tool that started a tracked move (AI-015 TR-13).</summary>
internal enum CoordinateMoveKind
{
    /// <summary>A <c>walk_to</c> move.</summary>
    Walk,

    /// <summary>A stationary <c>turn_to</c> move.</summary>
    Turn,
}

/// <summary>The model-facing kind of one navigation outcome (AI-015 TR-13).</summary>
internal enum NavigationOutcomeKind
{
    /// <summary>The move completed.</summary>
    Arrived,

    /// <summary>The move failed, was blocked, or was cancelled by anything other than <c>stop</c>.</summary>
    Failed,

    /// <summary>The move was cancelled by <c>stop</c>.</summary>
    Stopped,

    /// <summary>The move was superseded by a newer move.</summary>
    Replaced,
}

/// <summary>
/// The single outcome of one move started by <c>walk_to</c> or <c>turn_to</c> (AI-015 TR-13–TR-15): an
/// event-timeline observation sent through Mind's queue and delivered in the AI-002 <c>scene_update</c> exchange.
/// Its text names the outcome kind — and the reason for a failure — and ends with the character's final own
/// position and heading from the shared <see cref="SelfPositionText" /> formatter. It never carries an image.
/// </summary>
internal sealed record ObservedNavigationOutcome : AgentObservation
{
    /// <summary>Stable exact semantic key for navigation outcomes.</summary>
    public const string TypeKeyValue = "navigation.outcome";

    /// <summary>
    /// Provisional scheduling importance. Outcomes never interrupt a running turn (no fresh-turn claim); arrivals and
    /// failures wake a parked agent through <see cref="WakesWaitingAgent" />.
    /// </summary>
    internal const float Importance = 0.5f;

    /// <summary>Creates a validated outcome.</summary>
    public ObservedNavigationOutcome(
        NavigationOutcomeKind kind,
        CoordinateMoveKind move,
        string? failureReason,
        double finalX,
        double finalZ,
        double finalBearing)
    {
        bool isFailure = kind == NavigationOutcomeKind.Failed;
        if (isFailure == string.IsNullOrWhiteSpace(failureReason))
        {
            throw new ArgumentException(
                "A failed navigation outcome requires a reason, and no other outcome kind carries one.",
                nameof(failureReason));
        }

        if (!double.IsFinite(finalX) || !double.IsFinite(finalZ))
        {
            throw new ArgumentOutOfRangeException(nameof(finalX), "The final own position must be finite.");
        }

        Kind = kind;
        Move = move;
        FailureReason = failureReason;
        FinalX = finalX;
        FinalZ = finalZ;
        FinalBearing = CompassBearing.Normalise(finalBearing);
    }

    /// <summary>Gets the outcome kind.</summary>
    public NavigationOutcomeKind Kind
    {
        get;
    }

    /// <summary>Gets the tool that started the move.</summary>
    public CoordinateMoveKind Move
    {
        get;
    }

    /// <summary>Gets the failure reason, present only for <see cref="NavigationOutcomeKind.Failed" />.</summary>
    public string? FailureReason
    {
        get;
    }

    /// <summary>Gets the final own world X in metres.</summary>
    public double FinalX
    {
        get;
    }

    /// <summary>Gets the final own world Z in metres.</summary>
    public double FinalZ
    {
        get;
    }

    /// <summary>Gets the final own heading as a compass bearing in [0, 360).</summary>
    public double FinalBearing
    {
        get;
    }

    /// <inheritdoc />
    public override string TypeKey => TypeKeyValue;

    /// <summary>
    /// Maps one NAV-001 terminal record of a tracked move to its outcome (AI-015 TR-13): completed → arrived,
    /// superseded → replaced, a cancellation requested by <c>stop</c> → stopped, and every other terminal — failed,
    /// blocked, or any cancellation not caused by <c>stop</c> — → failed with a reason. The final pose is the
    /// record's realised terminal transform.
    /// </summary>
    public static ObservedNavigationOutcome FromTerminalRecord(
        NavigationTerminalOutcomeRecord record,
        CoordinateMoveKind move,
        bool stopRequested)
    {
        NavigationOutcomeKind kind = record.Outcome switch
        {
            NavigationTerminalOutcome.Completed => NavigationOutcomeKind.Arrived,
            NavigationTerminalOutcome.Superseded => NavigationOutcomeKind.Replaced,
            NavigationTerminalOutcome.Cancelled when stopRequested => NavigationOutcomeKind.Stopped,
            NavigationTerminalOutcome.Cancelled
                or NavigationTerminalOutcome.Failed
                or NavigationTerminalOutcome.Blocked => NavigationOutcomeKind.Failed,
            _ => NavigationOutcomeKind.Failed,
        };
        return new ObservedNavigationOutcome(
            kind,
            move,
            kind == NavigationOutcomeKind.Failed ? DescribeFailure(record, move) : null,
            record.FinalTransform.X,
            record.FinalTransform.Z,
            CompassBearing.FromYawRadians(record.FinalTransform.YawRadians));
    }

    /// <inheritdoc />
    protected override string RenderBody(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        string tool = Move == CoordinateMoveKind.Walk ? "`walk_to`" : "`turn_to`";
        string leading = Kind switch
        {
            NavigationOutcomeKind.Arrived => Move == CoordinateMoveKind.Walk
                ? $"Your {tool} move arrived: you reached the destination."
                : $"Your {tool} move arrived: you finished turning.",
            NavigationOutcomeKind.Failed => $"Your {tool} move failed: {FailureReason}.",
            NavigationOutcomeKind.Stopped => $"Your {tool} move stopped because you called `stop`.",
            NavigationOutcomeKind.Replaced => $"Your {tool} move was replaced by a newer move.",
            _ => throw new InvalidOperationException($"Unhandled navigation outcome kind '{Kind}'."),
        };
        return $"{leading} {SelfPositionText.Format(FinalX, FinalZ, FinalBearing)}";
    }

    /// <summary>
    /// An <c>arrived</c> or <c>failed</c> outcome wakes a parked agent so it reacts at once, as to a late tool result;
    /// <c>stopped</c> and <c>replaced</c> outcomes follow the NPC's own call and never wake (AI-015 TR-14).
    /// </summary>
    public override bool WakesWaitingAgent(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Kind is NavigationOutcomeKind.Arrived or NavigationOutcomeKind.Failed;
    }

    /// <inheritdoc />
    public override float CalculateImportance(ObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Importance;
    }

    private static string DescribeFailure(NavigationTerminalOutcomeRecord record, CoordinateMoveKind move)
        => record.Outcome switch
        {
            NavigationTerminalOutcome.Blocked => "the way was blocked",
            NavigationTerminalOutcome.Cancelled => DescribeCancellation(record.CancellationSource),
            NavigationTerminalOutcome.Failed when record.Reason == NavigationTerminalReason.MotionUnavailable =>
                "you cannot move right now",
            NavigationTerminalOutcome.Failed
                or NavigationTerminalOutcome.Completed
                or NavigationTerminalOutcome.Superseded => DescribeConvergenceFailure(move),
            _ => DescribeConvergenceFailure(move),
        };

    private static string DescribeCancellation(NavigationCancellationSource? source)
        => source switch
        {
            NavigationCancellationSource.ManualOverride => "your movement was taken over",
            NavigationCancellationSource.NavigationDisabled => "navigation was disabled",
            NavigationCancellationSource.OwnerRemoved or NavigationCancellationSource.Reset => "navigation was reset",
            NavigationCancellationSource.ExplicitClear or null => "the move was cancelled",
            _ => "the move was cancelled",
        };

    private static string DescribeConvergenceFailure(CoordinateMoveKind move)
        => move == CoordinateMoveKind.Walk
            ? "you could not get close enough to the destination"
            : "you could not finish the turn";
}
