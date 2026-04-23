using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Hosts reusable UI widgets and resolves them by interface type.
/// </summary>
[GlobalClass]
public partial class UIOverlay : Control
{
    /// <summary>
    /// Finds the first widget implementing <typeparamref name="TWidget"/>.
    /// </summary>
    /// <typeparam name="TWidget">Widget interface type to resolve.</typeparam>
    /// <returns>Resolved widget or <see langword="null"/> when unavailable.</returns>
    public TWidget? FindWidget<TWidget>()
        where TWidget : class, IUIWidget
        => FindWidgetInSubtree<TWidget>(this);

    /// <summary>
    /// Resolves a required widget implementing <typeparamref name="TWidget"/>.
    /// </summary>
    /// <typeparam name="TWidget">Widget interface type to resolve.</typeparam>
    /// <returns>Resolved widget.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the widget cannot be found.</exception>
    public TWidget GetWidget<TWidget>()
        where TWidget : class, IUIWidget
        => FindWidget<TWidget>()
            ?? throw new InvalidOperationException(
                $"Required widget '{typeof(TWidget).FullName}' not found under overlay '{GetPath()}'.");

    /// <summary>
    /// Sets or clears the debug message using an <see cref="IDebugWidget"/> when available.
    /// </summary>
    /// <param name="message">Message to set, or null/empty to clear.</param>
    /// <returns>
    /// <see langword="true"/> when a debug widget was resolved; otherwise <see langword="false"/>.
    /// </returns>
    public bool TrySetDebugMessage(string? message)
    {
        IDebugWidget? debugWidget = FindWidget<IDebugWidget>();
        if (debugWidget is null)
        {
            GD.PushWarning($"UI overlay '{GetPath()}' has no widget implementing {nameof(IDebugWidget)}.");
            return false;
        }

        if (string.IsNullOrEmpty(message))
        {
            debugWidget.ClearDebugMessage();
            return true;
        }

        debugWidget.SetDebugMessage(message);
        return true;
    }

    /// <summary>
    /// Posts a temporary notification message using an <see cref="INotificationWidget"/> when available.
    /// </summary>
    /// <param name="message">Message to post. Null/empty/whitespace is treated as a no-op.</param>
    /// <param name="timeoutSeconds">Message lifetime in seconds. Defaults to 3 seconds.</param>
    /// <returns>
    /// <see langword="true"/> when a notification widget was resolved; otherwise <see langword="false"/>.
    /// When <paramref name="message"/> is blank, this method does not post and does not clear notifications.
    /// </returns>
    public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
    {
        INotificationWidget? notificationWidget = FindWidget<INotificationWidget>();
        if (notificationWidget is null)
        {
            GD.PushWarning($"UI overlay '{GetPath()}' has no widget implementing {nameof(INotificationWidget)}.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        notificationWidget.PostNotification(message, timeoutSeconds);
        return true;
    }

    /// <summary>
    /// Clears queued notifications using an <see cref="INotificationWidget"/> when available.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a notification widget was resolved; otherwise <see langword="false"/>.
    /// </returns>
    public bool TryClearNotifications()
    {
        INotificationWidget? notificationWidget = FindWidget<INotificationWidget>();
        if (notificationWidget is null)
        {
            GD.PushWarning($"UI overlay '{GetPath()}' has no widget implementing {nameof(INotificationWidget)}.");
            return false;
        }

        notificationWidget.ClearNotifications();
        return true;
    }

    private static TWidget? FindWidgetInSubtree<TWidget>(Node node)
        where TWidget : class, IUIWidget
    {
        if (node is TWidget currentWidget)
        {
            return currentWidget;
        }

        foreach (Node child in node.GetChildren())
        {
            TWidget? childWidget = FindWidgetInSubtree<TWidget>(child);
            if (childWidget is not null)
            {
                return childWidget;
            }
        }

        return null;
    }
}
