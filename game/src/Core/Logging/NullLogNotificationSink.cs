namespace AlleyCat.Core.Logging;

/// <summary>
/// Notification sink used before the in-game UI is available or in tests.
/// </summary>
public sealed class NullLogNotificationSink : ILogNotificationSink
{
    /// <inheritdoc />
    public bool TryPostNotification(string? message, double timeoutSeconds = 3.0) => false;
}
