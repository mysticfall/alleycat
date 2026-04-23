using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Convenience helpers for posting global notifications from any node.
/// </summary>
public static class NotificationUIExtensions
{
    private static readonly NodePath _uiOverlayPath = "/root/Global/XR/SubViewport/UIOverlay";

    extension(Node node)
    {
        /// <summary>
        /// Attempts to post a notification through the global <see cref="UIOverlay"/>.
        /// </summary>
        /// <param name="message">Notification text to display.</param>
        /// <param name="timeoutSeconds">Message lifetime in seconds. Defaults to 3 seconds.</param>
        /// <returns>
        /// <see langword="true"/> when the notification widget was resolved; otherwise <see langword="false"/>.
        /// </returns>
        public bool PostNotification(string? message, double timeoutSeconds = 3.0)
        {
            UIOverlay? uiOverlay = node.GetNodeOrNull<UIOverlay>(_uiOverlayPath);
            if (uiOverlay is null)
            {
                GD.PushWarning($"Global UI overlay node '{_uiOverlayPath}' is unavailable.");
                return false;
            }

            return uiOverlay.TryPostNotification(message, timeoutSeconds);
        }

        /// <summary>
        /// Clears active notifications when available. Missing overlay/widget logs a warning and is ignored.
        /// </summary>
        public void ClearNotifications()
            => _ = node.TryClearNotifications();

        /// <summary>
        /// Attempts to clear active notifications through the global <see cref="UIOverlay"/>.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when the notification widget was resolved; otherwise <see langword="false"/>.
        /// </returns>
        public bool TryClearNotifications()
        {
            UIOverlay? uiOverlay = node.GetNodeOrNull<UIOverlay>(_uiOverlayPath);
            if (uiOverlay is null)
            {
                GD.PushWarning($"Global UI overlay node '{_uiOverlayPath}' is unavailable.");
                return false;
            }

            return uiOverlay.TryClearNotifications();
        }
    }
}
