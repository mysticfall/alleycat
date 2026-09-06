using System.Collections.ObjectModel;
using AlleyCat.Mind.AI.Watch;
using Godot;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>Projects active watch state without creating semantic watch outcomes.</summary>
[GlobalClass]
public sealed partial class WatchSceneStatusProjector : SceneStatusProjector
{
    /// <summary>Stable binding ID for the active-watch current-scene projection.</summary>
    public const string ProjectorIDValue = "active-watches";

    /// <summary>Direct sibling registry whose immutable snapshot this projector renders.</summary>
    [Export]
    public WatchRegistry? Registry
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override Type ProjectionType => typeof(ActiveWatchesSceneStatus);

    /// <inheritdoc />
    public override ISceneStatusProjection Project(SceneStatusBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        WatchRegistry registry = Registry
            ?? throw new InvalidOperationException("Watch scene-status projector requires its WatchRegistry.");
        _ = registry.GetParent() == GetParent()
            ? true
            : throw new InvalidOperationException("Watch scene-status projector requires a WatchRegistry direct sibling.");

        return new ActiveWatchesSceneStatus(registry.GetActiveWatchSnapshot());
    }
}

/// <summary>Immutable ordered root for active watch scene status.</summary>
public sealed record ActiveWatchesSceneStatus(IReadOnlyList<WatchStatusSnapshot> Watches) : ISceneStatusProjection
{
    /// <summary>Copies the ordered watch snapshot so a request can never observe later mutations.</summary>
    public ActiveWatchesSceneStatus(IEnumerable<WatchStatusSnapshot> watches)
        : this(new ReadOnlyCollection<WatchStatusSnapshot>([.. watches ?? throw new ArgumentNullException(nameof(watches))]))
    {
    }
}
