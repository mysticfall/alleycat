using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using static AlleyCat.IntegrationTests.Mind.AI.Navigation.CoordinateNavigationGate;

namespace AlleyCat.IntegrationTests.Mind.AI.Navigation;

/// <summary>One recorded navigation call with its schema check and parsed arguments.</summary>
internal sealed record NavigationCallObservation(
    string Name,
    string Arguments,
    bool Valid,
    string? InvalidReason,
    double? X,
    double? Z,
    double? Heading,
    string? Pace);

/// <summary>Scene-derived ground truth for one task.</summary>
internal sealed record NavigationGroundTruth(
    string TaskId,
    double? TargetX,
    double? TargetZ,
    double? Bearing);

/// <summary>Deterministically scored outcome of one gate trial.</summary>
internal sealed record NavigationTrialRecord(
    string Run,
    string TaskId,
    int Trial,
    string Outcome,
    int Requests,
    bool MinimapCalled,
    bool MinimapCalledBeforeNavigation,
    IReadOnlyList<NavigationCallObservation> NavigationCalls,
    IReadOnlyList<string> OtherCalls,
    bool ToolChoiceCorrect,
    double? PositionErrorMetres,
    bool? PositionPass,
    double? HeadingErrorDegrees,
    bool? HeadingPass,
    string? PositionFailurePattern,
    string? HeadingFailurePattern,
    string? ExecutionFailure,
    string? TraceFile,
    string? HeadingPath = null,
    string? ArrivalText = null,
    IReadOnlyList<string>? FollowUpCalls = null,
    IReadOnlyList<NavigationCallObservation>? FollowUpNavigationCalls = null,
    bool? MinimapCalledAfterArrival = null,
    bool? ArrivalMinimapAttached = null);

/// <summary>Schema validation, scoring and failure-pattern classification for AI-015 TR-21 gate trials.</summary>
internal static class CoordinateNavigationScoring
{
    public const string OutcomeNavigated = "navigation-call";
    public const string OutcomeNoNavigation = "no-navigation-call";
    public const string OutcomeExecutionFailure = "execution-failure";

    /// <summary>
    /// Checks one navigation call against its declared schema. A JSON <c>null</c> for an optional parameter
    /// is treated as omitted (pre-registered); a string where a number is required is invalid.
    /// </summary>
    public static NavigationCallObservation Observe(FunctionCallContent call)
    {
        IDictionary<string, object?> arguments = call.Arguments ?? new Dictionary<string, object?>();
        string serialised = JsonSerializer.Serialize(arguments);
        List<string> problems = [];
        double? x = null, z = null, heading = null;
        string? pace = null;

        switch (call.Name)
        {
            case WalkToName:
                x = ReadNumber(arguments, "x", required: true, problems);
                z = ReadNumber(arguments, "z", required: true, problems);
                heading = ReadNumber(arguments, "heading", required: false, problems);
                pace = ReadPace(arguments, problems);
                break;
            case TurnToName:
                heading = ReadNumber(arguments, "heading", required: true, problems);
                break;
            case StopName:
                break;
            default:
                problems.Add("not a navigation tool");
                break;
        }

        return new NavigationCallObservation(
            call.Name,
            serialised,
            problems.Count == 0,
            problems.Count == 0 ? null : string.Join("; ", problems),
            x,
            z,
            heading,
            pace);
    }

    /// <summary>Scores a completed trial's terminal navigation batch against its ground truth.</summary>
    public static (bool ToolChoiceCorrect, double? PositionError, bool? PositionPass, double? HeadingError,
        bool? HeadingPass, string? PositionPattern, string? HeadingPattern, string? HeadingPath) Score(
        NavigationGateTask task,
        NavigationGroundTruth truth,
        IReadOnlyList<NavigationCallObservation> terminalBatch,
        (double X, double Z, double Bearing) subject,
        IReadOnlyList<NavigationCallObservation>? followUp = null)
    {
        string expectedTool = task.Kind == NavigationTaskKind.Turn ? TurnToName : WalkToName;
        NavigationCallObservation? expected = terminalBatch.LastOrDefault(call => call.Name == expectedTool);
        bool toolChoiceCorrect = expected is not null;

        double? positionError = null;
        bool? positionPass = null;
        string? positionPattern = null;
        if (task.Kind != NavigationTaskKind.Turn)
        {
            double tolerance = PositionToleranceMetres + (task.StandOff ? StandOffAllowanceMetres : 0d);
            if (expected is { Valid: true, X: { } x, Z: { } z })
            {
                positionError = Distance(x, z, truth.TargetX!.Value, truth.TargetZ!.Value);
                positionPass = positionError <= tolerance;
                if (positionPass == false)
                {
                    positionPattern = ClassifyPosition(x, z, truth, subject, tolerance);
                }
            }
            else
            {
                positionPass = false;
                positionPattern = expected is null ? "no-walk_to" : "invalid-arguments";
            }
        }

        double? headingError = null;
        bool? headingPass = null;
        string? headingPattern = null;
        string? headingPath = null;
        if (task.Kind != NavigationTaskKind.Walk)
        {
            // Two-step case 2: a heading-less walk_to followed, after the simulated arrival, by a turn_to in the
            // next request. Only consulted when the follow-up was run (two-step heading path).
            NavigationCallObservation? followUpTurn = task.Kind == NavigationTaskKind.WalkAndFace
                && expected is { Valid: true, Heading: null }
                    ? followUp?.FirstOrDefault(static call => call.Name == TurnToName)
                    : null;
            if (expected is { Valid: true, Heading: { } heading })
            {
                headingPath = task.Kind == NavigationTaskKind.WalkAndFace ? "case1-walk_to.heading" : "turn_to";
                headingError = AngularError(heading, truth.Bearing!.Value);
                headingPass = headingError <= HeadingToleranceDegrees;
                if (headingPass == false)
                {
                    headingPattern = ClassifyHeading(heading, truth.Bearing.Value, subject.Bearing);
                }
            }
            else if (followUpTurn is not null)
            {
                headingPath = "case2-follow-up-turn_to";
                if (followUpTurn is { Valid: true, Heading: { } turnHeading })
                {
                    headingError = AngularError(turnHeading, truth.Bearing!.Value);
                    headingPass = headingError <= HeadingToleranceDegrees;
                    if (headingPass == false)
                    {
                        headingPattern = "case2:" + ClassifyHeading(turnHeading, truth.Bearing.Value, subject.Bearing);
                    }
                }
                else
                {
                    headingPass = false;
                    headingPattern = "case2:invalid-arguments";
                }
            }
            else
            {
                headingPass = false;
                if (followUp is not null && expected is { Valid: true, Heading: null })
                {
                    headingPath = "case2-no-turn_to";
                }

                headingPattern = expected is null
                    ? $"no-{expectedTool}"
                    : expected.Valid ? "heading-omitted" : "invalid-arguments";
                if (task.Kind == NavigationTaskKind.WalkAndFace
                    && terminalBatch.Any(static call => call.Name == TurnToName))
                {
                    headingPattern += "+separate-turn_to";
                }

                if (headingPath is not null)
                {
                    headingPattern += "+case2:no-turn_to";
                }
            }
        }

        return (toolChoiceCorrect, positionError, positionPass, headingError, headingPass, positionPattern,
            headingPattern, headingPath);
    }

    private static string ClassifyPosition(
        double x,
        double z,
        NavigationGroundTruth truth,
        (double X, double Z, double Bearing) subject,
        double tolerance)
    {
        double tx = truth.TargetX!.Value;
        double tz = truth.TargetZ!.Value;
        return Math.Abs(x) > 50d || Math.Abs(z) > 50d
            ? "pixel-like-values"
            : Distance(x, z, subject.X, subject.Z) <= 0.5d
            ? "own-position"
            : Distance(x, -z, tx, tz) <= tolerance ? "z-sign-flip"
            : Distance(-x, z, tx, tz) <= tolerance ? "x-sign-flip"
            : Distance(z, x, tx, tz) <= tolerance ? "axes-swapped"
            : Distance(x + subject.X, z + subject.Z, tx, tz) <= tolerance ? "relative-to-self"
            : Distance(x, z, tx, tz) <= tolerance + 1.5d ? "near-miss"
            : "other";
    }

    private static string ClassifyHeading(double heading, double truth, double subjectBearing)
        => AngularError(heading + 180d, truth) <= HeadingToleranceDegrees ? "opposite"
            : AngularError(360d - heading, truth) <= HeadingToleranceDegrees ? "left-right-mirror"
            : AngularError(180d - heading, truth) <= HeadingToleranceDegrees ? "up-down-mirror"
            : AngularError(90d - heading, truth) <= HeadingToleranceDegrees ? "math-angle-convention"
            : AngularError(heading + subjectBearing, truth) <= HeadingToleranceDegrees ? "relative-to-facing"
            : AngularError(heading, truth) <= HeadingToleranceDegrees + 30d ? "near-miss"
            : "other";

    private static double Distance(double ax, double az, double bx, double bz)
        => Math.Sqrt(((ax - bx) * (ax - bx)) + ((az - bz) * (az - bz)));

    private static double? ReadNumber(
        IDictionary<string, object?> arguments,
        string name,
        bool required,
        List<string> problems)
    {
        if (!arguments.TryGetValue(name, out object? raw) || raw is null
            || raw is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            if (required)
            {
                problems.Add($"missing required '{name}'");
            }

            return null;
        }

        double? value = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetDouble(),
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            _ => null,
        };
        if (value is not { } number)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"'{name}' is not a number ({Describe(raw)})"));
            return null;
        }

        if (!double.IsFinite(number))
        {
            problems.Add($"'{name}' is not finite");
            return null;
        }

        return number;
    }

    private static string? ReadPace(IDictionary<string, object?> arguments, List<string> problems)
    {
        if (!arguments.TryGetValue("pace", out object? raw) || raw is null
            || raw is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })
        {
            return null;
        }

        string? text = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            string s => s,
            _ => null,
        };
        if (text is null || !Paces.Contains(text, StringComparer.Ordinal))
        {
            problems.Add($"'pace' is not one of stroll/walk/hurry ({Describe(raw)})");
            return null;
        }

        return text;
    }

    private static string Describe(object raw)
        => raw is JsonElement element ? element.GetRawText() : raw.ToString() ?? "null";
}
