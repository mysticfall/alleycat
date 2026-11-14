using AlleyCat.Character;
using AlleyCat.Scene;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Composes the observed-character look-direction descriptor lines of the AI-013 visual-context presentation
/// (playtest R1): for every scene character other than the observer whose vision rig currently holds a gaze look
/// target, one naturally phrased orientation line stating where that character is looking — resolving the look
/// target to its owning character when the cue is character-owned, and otherwise classifying the cue's bearing
/// relative to the character's body facing.
/// </summary>
/// <remarks>
/// The lines exist because a character can turn her head while her body — and therefore her minimap marker —
/// keeps facing forward: without the descriptor, an agent reading body-facing imagery receives contradictory
/// information about where the observed character is looking. Derivation reads only live look-target state; it
/// is deliberately never part of observation records, and phrasing claims only the precision the data supports.
/// </remarks>
internal static class VisualContextGaze
{
    /// <summary>The maximum number of orientation lines one presentation carries, in full-ID ordinal order.</summary>
    internal const int MaximumDescriptorLines = 4;

    /// <summary>The shared empty orientation-line set presented when no observed character holds a look target.</summary>
    internal static readonly IReadOnlyList<string> NoLines = [];

    private const float FrontAngleThresholdDegrees = 45f;
    private const float BackAngleThresholdDegrees = 135f;
    private const float ZeroGroundSeparationSquared = 1e-6f;

    /// <summary>
    /// Composes the orientation lines for every observed character with a readable gaze look target, or an empty
    /// list when none applies. The observer itself is never described.
    /// </summary>
    public static IReadOnlyList<string> ComposeOrientationLines(ICharacter observer, ISceneContext scene)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scene);

        List<string> lines = [];
        foreach (ICharacter character in scene.Characters.OrderBy(
                     static character => character.FullId,
                     StringComparer.Ordinal))
        {
            if (ReferenceEquals(character, observer) || lines.Count >= MaximumDescriptorLines)
            {
                continue;
            }

            if (!character.TryGetVision(out IVision? vision) || vision is null)
            {
                continue;
            }

            VisualCue? target = vision.LookTarget;
            if (target is null || !GodotObject.IsInstanceValid(target))
            {
                continue;
            }

            if (FindNearestSubject(target) is ICharacter targetCharacter)
            {
                lines.Add($"`{character.FullId}` is looking toward `{targetCharacter.FullId}`.");
            }
            else if (DescribeDirectionalGaze(character, target) is { } directional)
            {
                lines.Add(directional);
            }
        }

        return lines;
    }

    /// <summary>
    /// Appends the orientation lines to one delivered-capture message, separated by single spaces; a message
    /// with no lines is returned unchanged.
    /// </summary>
    public static string AppendOrientationLines(string message, ICharacter observer, ISceneContext scene)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(scene);
        return AppendOrientationLines(message, ComposeOrientationLines(observer, scene));
    }

    /// <summary>
    /// Appends pre-composed orientation lines to one presentation text, separated by single spaces; a text with
    /// no lines is returned unchanged. The tool delivered-results and the auto-mode fabricated exchange both
    /// compose through this single append, so the delivery mode can never drift the presented content.
    /// </summary>
    public static string AppendOrientationLines(string message, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count == 0 ? message : $"{message} {string.Join(" ", lines)}";
    }

    /// <summary>Resolves the cue's nearest visual-subject ancestor — the subject that owns the cue.</summary>
    private static IVisualSubject? FindNearestSubject(VisualCue cue)
    {
        for (Node? ancestor = cue.GetParent(); ancestor is not null; ancestor = ancestor.GetParent())
        {
            if (ancestor is IVisualSubject subject)
            {
                return subject;
            }
        }

        return null;
    }

    /// <summary>
    /// Describes a non-character look target by its ground-plane bearing relative to the character's body facing
    /// — straight ahead, to one side, or back over the shoulder — mirroring the relative-position perception's
    /// bearing classification thresholds. The phrasing anchors the side explicitly to the character ("their own
    /// left") and claims only gaze direction, never head motion. A cue with no readable horizontal separation
    /// from the character has no honest direction and returns null.
    /// </summary>
    private static string? DescribeDirectionalGaze(ICharacter character, VisualCue target)
    {
        Vector3 local = character.GlobalTransform.Inverse() * target.GlobalTransform.Origin;
        Vector3 groundBearing = new(local.X, 0f, local.Z);
        if (groundBearing.LengthSquared() < ZeroGroundSeparationSquared)
        {
            return null;
        }

        float signedAngle = Vector3.Forward.SignedAngleTo(groundBearing.Normalized(), Vector3.Up);
        float magnitude = Mathf.RadToDeg(Math.Abs(signedAngle));
        return magnitude <= FrontAngleThresholdDegrees
            ? $"`{character.FullId}` is looking straight ahead."
            : magnitude >= BackAngleThresholdDegrees
                ? $"`{character.FullId}` is looking back over their own shoulder."
                : signedAngle >= 0f
                    ? $"`{character.FullId}` is looking to their own left."
                    : $"`{character.FullId}` is looking to their own right.";
    }
}
