using AlleyCat.Navigation;
using Godot;

namespace AlleyCat.Tests.MotionBricks;

/// <summary>Retains only a task after scene removal, never a freed scene-node reference.</summary>
public partial class MotionBricksLifecycleObserver : Node
{
    private Task? _completion;
    /// <summary>Call before scene removal; no barrier or delay is inserted into the worker.</summary>
    public void Arm(Node actor) => _completion = ((MotionBricksNavigation)((Character.Character)actor).Navigation!).WorkerCompletion;
    /// <summary>Native work and owned disposal have both finished.</summary>
    public bool Completed => _completion?.IsCompleted == true;
    /// <summary>Reports actual worker faults, rather than suppressing shutdown failures.</summary>
    public string Error => _completion?.Exception?.ToString() ?? "";
}
