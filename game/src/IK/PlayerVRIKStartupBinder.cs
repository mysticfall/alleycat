using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat.IK;

/// <summary>
/// Binds XR runtime abstractions to the first loaded player VRIK node.
/// </summary>
[GlobalClass]
public partial class PlayerVRIKStartupBinder : Node
{
    /// <summary>
    /// When true, enables binding processing. When false, skips binding.
    /// </summary>
    [Export]
    public bool Active
    {
        get;
        set;
    } = true;

    private XRManager? _xrManager;
    private bool _xrInitialised;
    private bool _bindCompleted;

    /// <inheritdoc />
    public override void _Ready()
    {
        if (!Active)
        {
            base._Ready();
            return;
        }

        _xrManager = Game.Instance.GetRequiredService<XRManager>();

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
            _bindCompleted = BindResolvedPlayerVRIK();
        }

        SetProcess(!_bindCompleted);
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is XRManager xrManager)
        {
            xrManager.Initialised -= OnXRInitialised;
        }
    }

    /// <inheritdoc />
    public override void _Process(double delta)
    {
        if (!Active)
        {
            return;
        }

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

        _bindCompleted = BindResolvedPlayerVRIK();

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

    private bool BindResolvedPlayerVRIK()
    {
        if (_xrManager is null)
        {
            return false;
        }

        PlayerVRIK? vrik = ResolvePlayerVRIK();
        return vrik is not null && vrik.BindToXRServices();
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
