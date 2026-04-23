using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Convenience helpers for setting global debug UI text from any node.
/// </summary>
public static class DebugUIExtensions
{
    private static readonly NodePath _uiOverlayPath = "/root/Global/XR/SubViewport/UIOverlay";

    extension(Node node)
    {
        /// <summary>
        /// Attempts to set a debug message through the global <see cref="UIOverlay"/>.
        /// </summary>
        /// <param name="message">Message to set, or null/empty to clear.</param>
        /// <returns>
        /// <see langword="true"/> when the debug widget was resolved; otherwise <see langword="false"/>.
        /// </returns>
        public bool SetDebugMessage(string? message)
        {
            UIOverlay? uiOverlay = node.GetNodeOrNull<UIOverlay>(_uiOverlayPath);
            if (uiOverlay is null)
            {
                GD.PushWarning($"Global UI overlay node '{_uiOverlayPath}' is unavailable.");
                return false;
            }

            return uiOverlay.TrySetDebugMessage(message);
        }

        /// <summary>
        /// Clears the active debug message when available. Missing overlay/widget logs a warning and is ignored.
        /// </summary>
        public void ClearDebugMessage()
            => _ = node.SetDebugMessage(null);
    }
}
