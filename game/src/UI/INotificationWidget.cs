namespace AlleyCat.UI;

/// <summary>
/// Contract for widgets that post temporary notification messages.
/// </summary>
public interface INotificationWidget : IUIWidget
{
    /// <summary>
    /// Gets or sets the maximum number of queued notifications.
    /// </summary>
    int MaximumQueueSize
    {
        get; set;
    }

    /// <summary>
    /// Posts a notification for the provided duration.
    /// </summary>
    /// <param name="message">Message text to display.</param>
    /// <param name="timeoutSeconds">Notification lifetime in seconds.</param>
    void PostNotification(string message, double timeoutSeconds = 3.0);

    /// <summary>
    /// Clears all currently queued notifications.
    /// </summary>
    void ClearNotifications();
}
