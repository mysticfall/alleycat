namespace AlleyCat.Navigation;

/// <summary>
/// Route-independent finite transition timing policy. Default finite clips preserve ANIM-002 source cadence.
/// </summary>
public static class NPCFiniteTransitionCadence
{
    /// <summary>
    /// Default finite transition cadence in ANIM-002 package source seconds per wall-clock second.
    /// </summary>
    public const float SourceSecondsPerWallSecond = 1f;

    /// <summary>
    /// Maps elapsed wall time to bounded package source time at the default source cadence.
    /// </summary>
    public static float SourceTimeAtElapsed(float elapsedSeconds, double sourceDurationSeconds)
    {
        return !float.IsFinite(elapsedSeconds) || !double.IsFinite(sourceDurationSeconds) || sourceDurationSeconds < 0d
            ? 0f
            : (float)Math.Clamp(
                elapsedSeconds * SourceSecondsPerWallSecond,
                0f,
                sourceDurationSeconds);
    }
}
