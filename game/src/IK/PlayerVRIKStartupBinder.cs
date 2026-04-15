using AlleyCat.Common;
using AlleyCat.XR;
using Godot;

namespace AlleyCat.IK;

/// <summary>
/// Binds XR runtime abstractions to the first loaded player VRIK node.
/// </summary>
[GlobalClass]
public partial class PlayerVRIKStartupBinder : Node
{
    /// <summary>
    /// Path to the global XRManager node.
    /// </summary>
    [Export]
    public NodePath XRManagerPath
    {
        get;
        set;
    } = new();

    private XRManager? _xrManager;
    private bool _xrInitialised;
    private bool _bindCompleted;

    /// <inheritdoc />
    public override void _Ready()
    {
        _xrManager = XRManagerPath.IsEmpty
            ? this.RequireNode<XRManager>("../XR")
            : this.RequireNode<XRManager>(XRManagerPath);

        _xrManager.Initialised += OnXRInitialised;

        if (_xrManager.InitialisationAttempted)
        {
            if (_xrManager.InitialisationSucceeded)
            {
                _xrInitialised = true;
            }
            else
            {
                _bindCompleted = true;
            }
        }

        if (_xrInitialised)
        {
            _bindCompleted = TryBindResolvedPlayerVRIK();
        }

        SetProcess(!_bindCompleted);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is not null)
        {
            _xrManager.Initialised -= OnXRInitialised;
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        _ = delta;

        if (_bindCompleted)
        {
            SetProcess(false);
            return;
        }

        if (!_xrInitialised)
        {
            return;
        }

        _bindCompleted = TryBindResolvedPlayerVRIK();

        if (_bindCompleted)
        {
            SetProcess(false);
        }
    }

    /// <summary>
    /// Resolves the player VRIK instance to bind.
    /// </summary>
    protected virtual PlayerVRIK? ResolvePlayerVRIK()
    {
        Godot.Collections.Array<Node> players = GetTree().GetNodesInGroup("Player");
        foreach (Node playerNode in players)
        {
            PlayerVRIK? vrik = playerNode.GetNodeOrNull<PlayerVRIK>("VRIK");
            if (vrik is not null)
            {
                return vrik;
            }
        }

        return null;
    }

    private bool TryBindResolvedPlayerVRIK()
    {
        if (_xrManager is null)
        {
            return false;
        }

        PlayerVRIK? vrik = ResolvePlayerVRIK();
        return vrik is not null && vrik.TryBind(_xrManager.Runtime);
    }

    private void OnXRInitialised(bool succeeded)
    {
        if (!succeeded)
        {
            GD.PushWarning("XR initialisation failed, skipping PlayerVRIK bind.");
            _bindCompleted = true;
            SetProcess(false);
            return;
        }

        _xrInitialised = true;
        SetProcess(true);
    }
}
