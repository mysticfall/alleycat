using AlleyCat.Rigging;
using Godot;

namespace AlleyCat.IK;

/// <summary>Side adapter that exposes coordinator values to the generic CharacterIK resolver.</summary>
[GlobalClass]
public partial class ContactLockFootTargetProvider : IKTargetIntentProvider
{
    [ThreadStatic]
    private static int _resolvingSideMask;

    /// <inheritdoc/>
    public LimbSide Side
    {
        get; set;
    }
    /// <inheritdoc/>
    public Node3D? AnimationSynchronizedFootTarget
    {
        get; set;
    }
    /// <summary>Actor transform source used to distinguish root motion yaw from animated foot orientation.</summary>
    public Node3D? RootMotionActor
    {
        get; set;
    }
    /// <inheritdoc/>
    public ContactLockCoordinator? Coordinator
    {
        get; set;
    }
    /// <summary>Explicit same-foot non-contact provider used only for release fallback sampling.</summary>
    public IKTargetIntentProvider? OrdinaryFallbackIntentProvider
    {
        get; set;
    }

    /// <inheritdoc/>
    public override IKTargetIntent GetTargetIntent() => TryGetTargetIntent(out IKTargetIntentResolution resolution) ? resolution.Intent : default;

    /// <inheritdoc/>
    public override bool TryGetTargetIntent(out IKTargetIntentResolution resolution)
    {
        int sideMask = Side == LimbSide.Left ? 1 : 2;
        if ((_resolvingSideMask & sideMask) != 0)
        {
            resolution = default;
            return false;
        }

        Node3D? source = AnimationSynchronizedFootTarget;
        Node3D? rootMotionActor = RootMotionActor;
        ContactLockCoordinator? coordinator = Coordinator;
        IKTargetIntentProvider? fallback = OrdinaryFallbackIntentProvider;
        if (source is not null && IsInstanceValid(source) && coordinator is not null
            && fallback is not null && IsInstanceValid(fallback)
            && rootMotionActor is not null && IsInstanceValid(rootMotionActor))
        {
            _resolvingSideMask |= sideMask;
            try
            {
                return coordinator.TryGetResolution(
                    Side,
                    source.GlobalTransform,
                    rootMotionActor.GlobalTransform,
                    fallback,
                    out resolution);
            }
            finally
            {
                _resolvingSideMask &= ~sideMask;
            }
        }

        resolution = default;
        return false;
    }
}
