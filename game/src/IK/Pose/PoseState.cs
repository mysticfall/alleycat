using Godot;

namespace AlleyCat.IK.Pose;

/// <summary>
/// Abstract Resource base describing a pose state in the VRIK pose state machine.
/// </summary>
/// <remarks>
/// Each state couples its own animation binding and hip reconciliation profile as a single
/// per-state responsibility, per the IK-004 contract. Subclasses override the lifecycle hooks
/// selectively; all hooks are no-ops by default.
/// </remarks>
[GlobalClass]
public abstract partial class PoseState : Resource, IPoseState
{
    /// <summary>
    /// Unique identifier for this state (for example <c>Standing</c>).
    /// </summary>
    [Export]
    public StringName Id
    {
        get;
        set;
    } = new();

    string IPoseState.Id => Id.ToString();

    /// <summary>
    /// Hip reconciliation profile bound to this state.
    /// </summary>
    [Export]
    public HipReconciliationProfile? HipReconciliation
    {
        get;
        set;
    }

    /// <summary>
    /// Animation binding bound to this state. May be <c>null</c> while animation wiring is
    /// deferred to a later increment.
    /// </summary>
    [Export]
    public AnimationBinding? AnimationBinding
    {
        get;
        set;
    }

    /// <summary>
    /// Invoked once when the state becomes active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnEnter(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Invoked once when the state is exiting in favour of a different state.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnExit(PoseStateContext context)
    {
        // No-op by default.
    }

    /// <summary>
    /// Invoked each tick while the state is active.
    /// </summary>
    /// <param name="context">Current pose-state context snapshot.</param>
    public virtual void OnUpdate(PoseStateContext context)
    {
        // No-op by default.
    }
}
