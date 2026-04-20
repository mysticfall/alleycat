using Godot;

namespace AlleyCat.IK.Anchors;

/// <summary>
/// Defines a single arm pole-anchor sample in body space.
/// </summary>
[Tool]
[GlobalClass]
public partial class ArmPoleAnchorResource : Resource
{
    /// <summary>
    /// Gets or sets the anchor name used for diagnostics.
    /// </summary>
    [Export]
    public StringName Name
    {
        get; set;
    } = new();

    /// <summary>
    /// Gets or sets the arm direction sample in body space.
    /// </summary>
    [Export]
    public Vector3 ArmDirBody
    {
        get; set;
    }

    /// <summary>
    /// Gets or sets the normalised reach ratio for this sample.
    /// This is shoulder-to-hand distance divided by rest arm length.
    /// </summary>
    [Export]
    public float ReachRatio
    {
        get; set;
    } = 1.0f;

    /// <summary>
    /// Gets or sets the desired elbow-pole intent direction in body space.
    /// </summary>
    [Export]
    public Vector3 PoleIntentBody
    {
        get; set;
    }
}
