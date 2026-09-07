using Godot;

namespace AlleyCat.Testing.Animation;

/// <summary>
/// Test-only physics callback used to observe AnimationTree ordering from the game assembly.
/// </summary>
public partial class LocomotionPlaybackProbePhysicsNode : Node
{
    /// <summary>Callback invoked from the physics slot while armed.</summary>
    public Action? Callback
    {
        get; set;
    }

    /// <summary>Whether the callback should run on the next physics tick.</summary>
    public bool Armed
    {
        get; set;
    }

    /// <summary>Whether the node remains armed after invoking its callback.</summary>
    public bool Repeat
    {
        get; set;
    }

    /// <summary>Invokes the configured probe callback according to the armed state.</summary>
    public override void _PhysicsProcess(double delta)
    {
        if (!Armed)
        {
            return;
        }

        Callback?.Invoke();
        Armed = Repeat;
    }
}
