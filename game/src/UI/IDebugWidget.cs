namespace AlleyCat.UI;

/// <summary>
/// Contract for UI widgets that can display a debug message.
/// </summary>
public interface IDebugWidget : IUIWidget
{
    /// <summary>
    /// Sets the current debug message and shows the widget.
    /// </summary>
    /// <param name="message">Message text to display.</param>
    void SetDebugMessage(string message);

    /// <summary>
    /// Clears the current message and hides the widget.
    /// </summary>
    void ClearDebugMessage();
}
