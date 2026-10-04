namespace AlleyCat.Control.Locomotion;

/// <summary>
/// Pure CTRL-001 stop-responsiveness policy: the per-lane stop playback rate resolved from the configured
/// release-to-stop time and post-release displacement targets (responsiveness takes priority over
/// stop-continuation naturalness), plus the lateral-dominance classification that routes lateral-dominant
/// releases into the neutral stop policy instead of the forward stop clip.
/// </summary>
public static class PlayerStopPolicy
{
    /// <summary>
    /// Resolves the stop playback rate for one stop lane: the authored clip compressed so its natural
    /// completion lands inside whichever target is tighter — the release-to-stop time target, or the
    /// post-release displacement target expressed as the clip time that covers the displacement budget at
    /// the clip's authored mean root speed. The result is clamped to the configured stop-rate bounds.
    /// </summary>
    /// <param name="clipSeconds">Authored stop-clip length in seconds.</param>
    /// <param name="authoredDisplacementMetres">
    /// Planar root displacement the clip authors over its full length, in world metres (catalogue
    /// endpoint scaled by the bound skeleton's motion scale).
    /// </param>
    /// <param name="targetSeconds">Maximum release-to-stop seconds.</param>
    /// <param name="targetDisplacementMetres">Maximum post-release displacement metres.</param>
    /// <param name="minimumRate">Lowest permitted stop playback rate.</param>
    /// <param name="maximumRate">Highest permitted stop playback rate.</param>
    public static float ResolveStopPlaybackRate(
        float clipSeconds,
        float authoredDisplacementMetres,
        float targetSeconds,
        float targetDisplacementMetres,
        float minimumRate,
        float maximumRate)
    {
        float safeMinimumRate = MathF.Max(0.01f, minimumRate);
        float safeMaximumRate = MathF.Max(safeMinimumRate, maximumRate);
        float safeClipSeconds = MathF.Max(0.01f, clipSeconds);
        float displacementBudgetSeconds = authoredDisplacementMetres > 0f
            ? targetDisplacementMetres * safeClipSeconds / authoredDisplacementMetres
            : float.PositiveInfinity;
        float budgetSeconds = MathF.Min(MathF.Max(0.01f, targetSeconds), displacementBudgetSeconds);
        return Math.Clamp(safeClipSeconds / budgetSeconds, safeMinimumRate, safeMaximumRate);
    }

    /// <summary>
    /// Whether a travelled sustained blend is lateral-dominant: the lateral component strictly exceeds the
    /// forward component, so pure-lateral and lateral-heavy diagonal releases take the neutral stop policy
    /// (their remaining motion must not be rotated into the forward stop clip's authored forward root),
    /// while forward/backward-dominant blends — including an even 45-degree diagonal — keep their authored
    /// stop lanes.
    /// </summary>
    /// <param name="lateral">Lateral (actor X) component of the travelled sustained blend.</param>
    /// <param name="forward">Forward (actor Y) component of the travelled sustained blend.</param>
    public static bool IsLateralDominantRelease(float lateral, float forward)
        => MathF.Abs(lateral) > MathF.Abs(forward);
}
