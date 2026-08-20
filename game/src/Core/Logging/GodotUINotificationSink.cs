using AlleyCat.Core.Threading;
using AlleyCat.UI;
using Godot;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Resolves the runtime notification widget without emitting further log entries when unavailable.
/// </summary>
public sealed class GodotUINotificationSink(Node root, IMainThreadDispatcher? mainThreadDispatcher = null)
    : ILogNotificationSink
{
    private static readonly NodePath _uiOverlayPath = "/root/Global/XR/SubViewport/UIOverlay";

    /// <summary>
    /// Attempts to post a notification, returning false when the UI is not currently available. When a main
    /// thread dispatcher is configured, the widget post runs asynchronously on the main thread and the return
    /// value reports only acceptance for posting — completion, cancellation, and delivery failures after
    /// acceptance are contained within the asynchronous dispatch.
    /// </summary>
    /// <inheritdoc />
    public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

        if (mainThreadDispatcher is { } dispatcher)
        {
            _ = PostViaDispatcherAsync(dispatcher, message, timeoutSeconds);
            return true;
        }

        return TryPostOnCurrentThread(message, timeoutSeconds);
    }

    private async Task PostViaDispatcherAsync(
        IMainThreadDispatcher dispatcher,
        string message,
        double timeoutSeconds)
    {
        try
        {
            await dispatcher
                .InvokeAsync(() => _ = TryPostOnCurrentThread(message, timeoutSeconds))
                .ConfigureAwait(false);
        }
        catch
        {
            // Dispatcher shutdown and Godot teardown races must never escape through logging callers.
        }
    }

    private bool TryPostOnCurrentThread(string message, double timeoutSeconds)
    {
        try
        {
            UIOverlay? uiOverlay = root.GetNodeOrNull<UIOverlay>(_uiOverlayPath);
            INotificationWidget? notificationWidget = uiOverlay?.FindWidget<INotificationWidget>();
            if (notificationWidget is null)
            {
                return false;
            }

            notificationWidget.PostNotification(message, timeoutSeconds);
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
