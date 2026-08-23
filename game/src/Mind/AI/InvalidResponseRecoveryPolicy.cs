namespace AlleyCat.Mind.AI;

/// <summary>
/// Defines the bounded recovery behaviour for malformed model responses independently from transport retries.
/// </summary>
internal interface IInvalidResponseRecoveryPolicy
{
    /// <summary>Gets the maximum number of consecutive invalid responses permitted in one session.</summary>
    int ConsecutiveFailureBudget
    {
        get;
    }

    /// <summary>
    /// Waits before a fresh request after the specified consecutive invalid response count.
    /// </summary>
    /// <param name="consecutiveFailureCount">The one-based count of consecutive invalid responses.</param>
    /// <param name="cancellationToken">Cancels the wait for session lifetime or observation interruption.</param>
    Task BackoffAsync(int consecutiveFailureCount, CancellationToken cancellationToken);
}

/// <summary>
/// Immutable, cancellation-aware invalid-response recovery policy for an agent session.
/// </summary>
internal sealed class InvalidResponseRecoveryPolicy : IInvalidResponseRecoveryPolicy
{
    internal const int DefaultConsecutiveFailureBudget = 3;

    internal static readonly TimeSpan[] DefaultBackoffDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly TimeSpan[] _backoffDelays;

    public InvalidResponseRecoveryPolicy(
        int consecutiveFailureBudget,
        IReadOnlyList<TimeSpan> backoffDelays)
    {
        if (consecutiveFailureBudget < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(consecutiveFailureBudget),
                "The invalid-response recovery budget must be at least one.");
        }

        ArgumentNullException.ThrowIfNull(backoffDelays);
        if (backoffDelays.Count == 0)
        {
            throw new ArgumentException("The invalid-response recovery policy requires at least one backoff delay.", nameof(backoffDelays));
        }

        _backoffDelays = [.. backoffDelays];
        if (_backoffDelays.Any(static delay => delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan))
        {
            throw new ArgumentOutOfRangeException(
                nameof(backoffDelays),
                "Invalid-response recovery backoff delays cannot be negative.");
        }

        ConsecutiveFailureBudget = consecutiveFailureBudget;
    }

    public int ConsecutiveFailureBudget
    {
        get;
    }

    /// <inheritdoc />
    public Task BackoffAsync(int consecutiveFailureCount, CancellationToken cancellationToken)
    {
        if (consecutiveFailureCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(consecutiveFailureCount));
        }

        TimeSpan delay = _backoffDelays[Math.Min(consecutiveFailureCount - 1, _backoffDelays.Length - 1)];
        return Task.Delay(delay, cancellationToken);
    }
}
