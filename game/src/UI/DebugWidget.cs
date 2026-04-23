using AlleyCat.Common;
using Godot;

namespace AlleyCat.UI;

/// <summary>
/// Displays ad-hoc debug text in the global UI overlay.
/// </summary>
[GlobalClass]
public partial class DebugWidget : MarginContainer, IDebugWidget
{
    private Label? _label;

    /// <inheritdoc />
    public override void _Ready()
    {
        EnsureLabelBound();
        ClearDebugMessage();
    }

    /// <inheritdoc />
    public void SetDebugMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ClearDebugMessage();
            return;
        }

        EnsureLabelBound();
        _label!.Text = message;
        Show();
    }

    /// <inheritdoc />
    public void ClearDebugMessage()
    {
        EnsureLabelBound();
        _label!.Text = string.Empty;
        Hide();
    }

    private void EnsureLabelBound()
        => _label ??= this.RequireNode<Label>("Label");
}
