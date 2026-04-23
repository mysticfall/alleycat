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
