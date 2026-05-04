using Godot;

namespace AlleyCat.Control;

/// <summary>
/// Shared base for locomotion components that consume permission sources.
/// </summary>
public abstract partial class LocomotionBase : Node, ILocomotion
{
    private ILocomotionAnimationSource[] _locomotionAnimationSources = [];
    private ILocomotionPermissionSource[] _permissionSources = [];

    /// <summary>
    /// Nodes that contribute locomotion permissions to this component.
    /// </summary>
    [Export]
    public Node?[] PermissionSourceNodes
    {
        get;
        set;
    } = [];

    /// <inheritdoc />
    public override void _Ready()
    {
        _permissionSources = ResolvePermissionSources();
        _locomotionAnimationSources = ResolveLocomotionAnimationSources();
    }

    /// <summary>
    /// Resolves the current combined locomotion permissions.
    /// </summary>
    protected LocomotionPermissions GetCurrentLocomotionPermissions()
    {
        LocomotionPermissions combined = LocomotionPermissions.Allowed;

        for (int i = 0; i < _permissionSources.Length; i++)
        {
            combined = combined.Combine(_permissionSources[i].LocomotionPermissions);
        }

        return combined;
    }

    /// <summary>
    /// Resolves the current locomotion animation-state override, if any.
    /// </summary>
    protected LocomotionStateTarget? GetLocomotionStateTarget()
    {
        for (int i = 0; i < _locomotionAnimationSources.Length; i++)
        {
            LocomotionStateTarget? target =
                _locomotionAnimationSources[i].LocomotionStateTarget;
            if (target.HasValue)
            {
                return target;
            }
        }

        return null;
    }

    /// <summary>
    /// Applies movement permissions to a desired planar locomotion input.
    /// </summary>
    protected static Vector2 ApplyMovementPermissions(Vector2 desiredMovementInput, LocomotionPermissions permissions)
        => permissions.MovementAllowed ? desiredMovementInput.LimitLength(1f) : Vector2.Zero;

    /// <summary>
    /// Applies rotation permissions to a desired yaw delta.
    /// </summary>
    protected static float ApplyRotationPermissions(float desiredYawDelta, LocomotionPermissions permissions)
        => permissions.RotationAllowed ? desiredYawDelta : 0f;

    /// <inheritdoc />
    public abstract void SetMovementInput(Vector2 input);

    /// <inheritdoc />
    public abstract void SetRotationInput(Vector2 input);

    private ILocomotionAnimationSource[] ResolveLocomotionAnimationSources()
    {
        if (PermissionSourceNodes.Length == 0)
        {
            return [];
        }

        List<ILocomotionAnimationSource> sources = new(PermissionSourceNodes.Length);

        for (int i = 0; i < PermissionSourceNodes.Length; i++)
        {
            if (PermissionSourceNodes[i] is ILocomotionAnimationSource source
                && !sources.Contains(source))
            {
                sources.Add(source);
            }
        }

        return [.. sources];
    }

    private ILocomotionPermissionSource[] ResolvePermissionSources()
    {
        if (PermissionSourceNodes.Length == 0)
        {
            return [];
        }

        List<ILocomotionPermissionSource> sources = new(PermissionSourceNodes.Length);

        for (int i = 0; i < PermissionSourceNodes.Length; i++)
        {
            Node? permissionSourceNode = PermissionSourceNodes[i];
            if (permissionSourceNode is not ILocomotionPermissionSource permissionSource)
            {
                string nodeDescription = permissionSourceNode is null
                    ? "null"
                    : DescribePermissionSourceNode(permissionSourceNode);

                throw new InvalidOperationException(
                    $"{GetType().Name} requires every {nameof(PermissionSourceNodes)} entry to implement " +
                    $"{nameof(ILocomotionPermissionSource)}. Entry {i} ({nodeDescription}) does not.");
            }

            if (!sources.Contains(permissionSource))
            {
                sources.Add(permissionSource);
            }
        }

        return [.. sources];
    }

    private static string DescribePermissionSourceNode(Node permissionSourceNode)
        => permissionSourceNode.IsInsideTree()
            ? permissionSourceNode.GetPath().ToString()
            : $"{permissionSourceNode.Name} ({permissionSourceNode.GetType().Name})";
}
