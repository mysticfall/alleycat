namespace AlleyCat.Core.Logging;

/// <summary>
/// Abstracts notification posting so logging providers do not depend on UI startup order.
/// </summary>
public interface ILogNotificationSink
{
    /// <summary>
    /// Attempts to post a notification, returning false when the UI is not currently available.
    /// </summary>
    bool TryPostNotification(string? message, double timeoutSeconds = 3.0);
}
