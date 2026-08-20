namespace AlleyCat.Core.Logging;

/// <summary>
/// Marks a structured log entry state as eligible for routing to the in-game notification UI.
/// </summary>
/// <remarks>
/// The log level is the single universal switch: entry-carrying diagnostics emit at trace level, so the configured
/// level of their log category governs both console logging and notification routing.
/// </remarks>
public interface IUINotificationEntry
{
    /// <summary>
    /// Renders the transient notification text for this entry.
    /// </summary>
    string ToNotificationText();

    /// <summary>
    /// Toast lifetime for this entry, in seconds. Error-path posts are not entry-driven and keep the notification
    /// sink's own default timeout.
    /// </summary>
    double NotificationTimeoutSeconds => 5.0;
}
