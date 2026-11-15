using AlleyCat.Character;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Composes the subject's own head-direction line of the AI-013 vision-capture presentation (playtest R5): when
/// the subject character's active gaze turns their head away from their body facing, one second-person line
/// states the direction of that turn, so an agent reading body-facing imagery alongside the captured view knows
/// the frame comes from a turned head.
/// </summary>
/// <remarks>
/// The line expresses direction only — never a gaze target — because its sole purpose is to reconcile the turned
/// head with body-facing imagery such as the minimap. It derives from the subject's live look-target state
/// (the cue's ground-plane bearing against the subject's body facing, using the relative-position perception's
/// 45°/135° thresholds), is deliberately never part of observation records, and claims only the precision the
/// data supports: when the head and body facing align — no look target, or one within the front cone — the
/// imagery is already consistent and no line is composed.
/// </remarks>
internal static class VisualContextGaze
{
    /// <summary>The shared empty descriptor-line set presented when the subject's head is not turned.</summary>
    internal static readonly IReadOnlyList<string> NoLines = [];

    private const float FrontAngleThresholdDegrees = 45f;
    private const float BackAngleThresholdDegrees = 135f;
    private const float ZeroGroundSeparationSquared = 1e-6f;

    /// <summary>
    /// Composes the subject's head-direction line, or an empty list when the subject's head is not turned away
    /// from their body facing: no readable look target, or one within the front cone, composes nothing.
    /// </summary>
    public static IReadOnlyList<string> ComposeSubjectHeadDirectionLines(ICharacter subject)
    {
        ArgumentNullException.ThrowIfNull(subject);

        return subject.TryGetVision(out IVision? vision)
            && vision is not null
            && vision.LookTarget is { } target
            && GodotObject.IsInstanceValid(target)
            && DescribeHeadTurn(subject, target) is { } line
            ? [line]
            : NoLines;
    }

    /// <summary>
    /// Appends the subject's head-direction line to one delivered-capture message; a message for an untilled
    /// head is returned unchanged.
    /// </summary>
    public static string AppendSubjectHeadDirection(string message, ICharacter subject)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(subject);
        return AppendDescriptorLines(message, ComposeSubjectHeadDirectionLines(subject));
    }

    /// <summary>
    /// Appends pre-composed descriptor lines to one presentation text, separated by single spaces; a text with
    /// no lines is returned unchanged. The tool delivered-results and the auto-mode fabricated exchange both
    /// compose through this single append, so the delivery mode can never drift the presented content.
    /// </summary>
    public static string AppendDescriptorLines(string message, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0 ? message : $"{message} {string.Join(" ", lines)}";
    }

    /// <summary>
    /// Describes the subject's head turn by its look target's ground-plane bearing relative to their own body
    /// facing — to one side, or back over a shoulder — in second person, mirroring the relative-position
    /// perception's bearing classification thresholds. A bearing within the front cone aligns head and body, and
    /// a target with no readable horizontal separation has no honest direction; both return null.
    /// </summary>
    private static string? DescribeHeadTurn(ICharacter subject, VisualCue target)
    {
        Vector3 local = subject.GlobalTransform.Inverse() * target.GlobalTransform.Origin;
        Vector3 groundBearing = new(local.X, 0f, local.Z);
        if (groundBearing.LengthSquared() < ZeroGroundSeparationSquared)
        {
            return null;
        }

        float signedAngle = Vector3.Forward.SignedAngleTo(groundBearing.Normalized(), Vector3.Up);
        float magnitude = Mathf.RadToDeg(Math.Abs(signedAngle));
        if (magnitude <= FrontAngleThresholdDegrees)
        {
            return null;
        }

        bool toTheLeft = signedAngle >= 0f;
        return magnitude >= BackAngleThresholdDegrees
            ? toTheLeft
                ? "Your head is turned back over your left shoulder."
                : "Your head is turned back over your right shoulder."
            : toTheLeft
                ? "Your head is turned to your left."
                : "Your head is turned to your right.";
    }
}
