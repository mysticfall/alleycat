using Godot;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>
/// Produces one typed, immutable root for an authored current-scene-status prompt section.
/// </summary>
public interface ISceneStatusProjector
{
    /// <summary>Stable exact identifier used by <see cref="Prompting.ProjectionPromptSection" /> bindings.</summary>
    string ProjectorID
    {
        get;
    }

    /// <summary>Exact immutable root type produced by this projector.</summary>
    Type ProjectionType
    {
        get;
    }

    /// <summary>Builds a projection from one coherent request-scoped status snapshot.</summary>
    ISceneStatusProjection Project(SceneStatusBuildContext context);
}

/// <summary>Marker for immutable scene-status projection roots.</summary>
public interface ISceneStatusProjection;

/// <summary>
/// Authorable direct-child base for current-scene-status projectors.
/// </summary>
[GlobalClass]
public abstract partial class SceneStatusProjector : Node, ISceneStatusProjector
{
    /// <summary>Stable exact identifier used by authored projection prompt sections.</summary>
    [Export]
    public string ProjectorID { get; set; } = string.Empty;

    /// <inheritdoc />
    public abstract Type ProjectionType
    {
        get;
    }

    /// <inheritdoc />
    public abstract ISceneStatusProjection Project(SceneStatusBuildContext context);
}
