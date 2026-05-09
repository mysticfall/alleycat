using AlleyCat.XR;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// IK target provider that follows the hand-position node for one XR controller side.
/// </summary>
[GlobalClass]
public partial class XRControllerTargetProvider : IKTargetStateProvider
{
    /// <summary>
    /// Limb side used to select the corresponding XR controller during runtime binding.
    /// </summary>
    [Export]
    public LimbSide Side
    {
        get;
        set;
    } = LimbSide.Right;

    /// <summary>
    /// Resolved XR hand-position node, or <see langword="null" /> before binding.
    /// </summary>
    public Node3D? ResolvedSourceNode
    {
        get;
        private set;
    }

    /// <summary>
    /// Desired influence while the XR source is available.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float DesiredInfluence { get; set; } = 1.0f;

    /// <summary>
    /// Binds this provider to the hand-position node for its configured side.
    /// </summary>
    public void Bind(IXRHandController rightHandController, IXRHandController leftHandController)
        => ResolvedSourceNode = Side == LimbSide.Right
            ? rightHandController.HandPositionNode
            : leftHandController.HandPositionNode;

    /// <inheritdoc />
    public override IKTargetState GetTargetState()
        => ResolvedSourceNode is not null && IsInstanceValid(ResolvedSourceNode)
            ? new IKTargetState(ResolvedSourceNode.GlobalTransform, DesiredInfluence)
            : new IKTargetState(Transform3D.Identity, 0.0f);
}
