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

    private bool TryResolveSourceNode()
    {
        if (ResolvedSourceNode is not null && IsInstanceValid(ResolvedSourceNode))
        {
            return true;
        }

        IXRRuntime? runtime = ResolveXRRuntime();
        if (runtime is null)
        {
            ResolvedSourceNode = null;
            return false;
        }

        ResolvedSourceNode = Side == LimbSide.Right
            ? runtime.RightHandController.HandPositionNode
            : runtime.LeftHandController.HandPositionNode;

        return ResolvedSourceNode is not null && IsInstanceValid(ResolvedSourceNode);
    }

    /// <inheritdoc />
    public override IKTargetState GetTargetState()
    {
        _ = TryResolveSourceNode();
        return ResolvedSourceNode is not null && IsInstanceValid(ResolvedSourceNode)
            ? new IKTargetState(ResolvedSourceNode.GlobalTransform, DesiredInfluence)
            : new IKTargetState(Transform3D.Identity, 0.0f);
    }

    private static IXRRuntime? ResolveXRRuntime()
    {
        try
        {
            XRManager? xrManager = Game.Instance.GetService<XRManager>();
            return xrManager?.Runtime;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
