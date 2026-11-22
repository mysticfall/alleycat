namespace AlleyCat.Mind.AI.Decision;

/// <summary>Failure backoff schedule: 2× the base interval on first failure, doubling thereafter, capped.</summary>
internal static class DecisionFailureBackoff
{
    /// <summary>The maximum backoff delay in seconds.</summary>
    public const double MaximumSeconds = 30d;

    /// <summary>Computes the next backoff delay after one more consecutive failure.</summary>
    /// <param name="currentSeconds">The backoff in effect for the failed evaluation; zero when none.</param>
    /// <param name="baseIntervalSeconds">The evaluation cadence interval.</param>
    public static double NextSeconds(double currentSeconds, double baseIntervalSeconds)
    {
        if (!double.IsFinite(baseIntervalSeconds) || baseIntervalSeconds <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(baseIntervalSeconds),
                baseIntervalSeconds,
                "Base interval must be finite and positive.");
        }

        if (!double.IsFinite(currentSeconds) || currentSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(currentSeconds),
                currentSeconds,
                "Current backoff must be finite and non-negative.");
        }

        double next = currentSeconds <= 0d ? baseIntervalSeconds * 2d : currentSeconds * 2d;
        return Math.Min(next, MaximumSeconds);
    }
}
