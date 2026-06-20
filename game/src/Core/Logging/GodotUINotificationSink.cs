using AlleyCat.UI;
using Godot;

namespace AlleyCat.Core.Logging;

/// <summary>
/// Resolves the runtime notification widget without emitting further log entries when unavailable.
/// </summary>
public sealed class GodotUINotificationSink(Node root) : ILogNotificationSink
{
    private static readonly NodePath _uiOverlayPath = "/root/Global/XR/SubViewport/UIOverlay";

    /// <inheritdoc />
    public bool TryPostNotification(string? message, double timeoutSeconds = 3.0)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return true;
        }

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
