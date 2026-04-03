using AlleyCat.Common;
using Godot;

namespace AlleyCat.UI.Marker;

/// <summary>
/// A 3D axes gizmo with an optional text label, intended for visually testing
/// Godot scenes in-game.
/// </summary>
[GlobalClass]
public partial class DebugMarker : Node3D
{
    /// <summary>
    /// Sets the optional label text displayed beside the gizmo.
    /// If <c>null</c> or empty, the label is hidden.
    /// </summary>
    [Export]
    public string? LabelText
    {
        get;
        set;
    }

    /// <summary>
    /// Initialises the label when the node enters the scene tree.
    /// </summary>
    public override void _Ready()
    {
        Label3D label = this.RequireNode<Label3D>("Label3D");

        if (string.IsNullOrEmpty(LabelText))
        {
            label.Visible = false;
        }

        label.Text = LabelText;
    }
}
