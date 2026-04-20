using Godot;

namespace AlleyCat.IK.Anchors.Authoring;

/// <summary>
/// Represents one authored arm-pole anchor sample pose in the editor.
/// </summary>
[Tool]
[GlobalClass]
public partial class ArmPoleAnchorAuthoringPose : Node3D
{
    /// <summary>
    /// Gets or sets the anchor name written to the baked resource.
    /// </summary>
    [Export]
    public StringName AnchorName
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets the path to the hand marker node.
    /// </summary>
    [Export]
    public NodePath HandMarkerPath
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Gets or sets the path to the pole marker node.
    /// </summary>
    [Export]
    public NodePath PoleMarkerPath
    {
        get;
        set;
    } = new();

    /// <summary>
    /// Tries to resolve hand and pole marker nodes.
    /// </summary>
    /// <param name="hand">Resolved hand marker when successful.</param>
    /// <param name="pole">Resolved pole marker when successful.</param>
    /// <returns><see langword="true"/> when both markers resolve to <see cref="Node3D"/>.</returns>
    public bool TryGetMarkers(out Node3D hand, out Node3D pole)
    {
        hand = null!;
        pole = null!;

        if (HandMarkerPath.IsEmpty || PoleMarkerPath.IsEmpty)
        {
            return false;
        }

        Node3D? resolvedHand = GetNodeOrNull<Node3D>(HandMarkerPath);
        Node3D? resolvedPole = GetNodeOrNull<Node3D>(PoleMarkerPath);

        if (resolvedHand is null || resolvedPole is null)
        {
            return false;
        }

        hand = resolvedHand;
        pole = resolvedPole;
        return true;
    }
}
