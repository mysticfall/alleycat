using System.Diagnostics.CodeAnalysis;
using Godot;

namespace AlleyCat.IK.Anchors;

/// <summary>
/// Defines a configurable set of arm pole anchors used by arm IK.
/// </summary>
[Tool]
[GlobalClass]
public partial class ArmPoleAnchorSetResource : Resource
{
    /// <summary>
    /// Gets or sets the anchor collection used for blending.
    /// </summary>
    [Export]
    public ArmPoleAnchorResource[] Anchors
    {
        get; set;
    } = [];

    /// <summary>
    /// Gets or sets the epsilon value in radians used by inverse-distance weighting.
    /// </summary>
    [Export]
    public float WeightEpsilonRadians
    {
        get; set;
    } = 0.01f;

    /// <summary>
    /// Gets or sets the reach-delta weighting multiplier used in anchor scoring.
    /// </summary>
    [Export]
    public float ReachWeight
    {
        get; set;
    }

    /// <summary>
    /// Mirrors a body-space vector across X for the selected arm side.
    /// </summary>
    /// <param name="vector">The input body-space vector.</param>
    /// <param name="side">The side to evaluate.</param>
    /// <returns>
    /// <c>(-x, y, z)</c> for <see cref="ArmSide.Left"/>, otherwise the vector unchanged.
    /// </returns>
    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public Vector3 MirrorXForSide(Vector3 vector, ArmSide side)
    {
        return side == ArmSide.Left
            ? new Vector3(-vector.X, vector.Y, vector.Z)
            : vector;
    }
}
