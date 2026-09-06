using System.Collections.ObjectModel;
using AlleyCat.Mind.AI.Prompting;

namespace AlleyCat.Mind.AI.SceneStatus;

/// <summary>Session-start validated direct-child projector registry.</summary>
internal sealed class SceneStatusProjectorRegistry
{
    private readonly IReadOnlyDictionary<string, ISceneStatusProjector> _projectors;

    private SceneStatusProjectorRegistry(IReadOnlyDictionary<string, ISceneStatusProjector> projectors)
    {
        _projectors = projectors;
    }

    /// <summary>Discovers direct projector children in their authored scene order.</summary>
    public static SceneStatusProjectorRegistry Discover(AgenticMind mind)
    {
        ArgumentNullException.ThrowIfNull(mind);

        Dictionary<string, ISceneStatusProjector> projectors = new(StringComparer.Ordinal);
        foreach (Godot.Node child in mind.GetChildren())
        {
            if (child is not ISceneStatusProjector projector)
            {
                continue;
            }

            ValidateProjector(projector, child);
            if (!projectors.TryAdd(projector.ProjectorID, projector))
            {
                throw new InvalidOperationException(
                    $"AgenticMind '{mind.GetPath()}' has duplicate direct scene-status projector ID "
                    + $"'{projector.ProjectorID}'. Projector IDs are exact and ordinal case-sensitive.");
            }
        }

        return new SceneStatusProjectorRegistry(
            new ReadOnlyDictionary<string, ISceneStatusProjector>(projectors));
    }

    /// <summary>Validates every projection prompt-section binding before the session can begin.</summary>
    public void ValidateBindings(IReadOnlyList<PromptSection> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        foreach (PromptSection? section in sections)
        {
            if (section is null)
            {
                throw new InvalidOperationException("CurrentSceneStatus prompt stack cannot contain a null section.");
            }

            if (section is ProjectionPromptSection projectionSection)
            {
                _ = Resolve(projectorID: projectionSection.ProjectorID, projectionSection.ResolveExpectedRootType());
            }
        }
    }

    /// <summary>Runs each bound projection once to reject incompatible data at session start.</summary>
    public void ValidateProjections(SceneStatusBuildContext context, IReadOnlyList<PromptSection> sections)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(sections);
        foreach (PromptSection section in sections)
        {
            if (section is ProjectionPromptSection projectionSection)
            {
                _ = Project(projectionSection.ProjectorID, projectionSection.ResolveExpectedRootType(), context);
            }
        }
    }

    /// <summary>Builds one strictly typed projection for an authored section binding.</summary>
    public ISceneStatusProjection Project(string projectorID, Type expectedRootType, SceneStatusBuildContext context)
    {
        ArgumentNullException.ThrowIfNull(expectedRootType);
        ArgumentNullException.ThrowIfNull(context);

        ISceneStatusProjector projector = Resolve(projectorID, expectedRootType);
        ISceneStatusProjection projection = projector.Project(context)
            ?? throw new InvalidOperationException(
                $"Scene-status projector '{projectorID}' returned a null projection.");
        return projection.GetType() != expectedRootType
            ? throw new InvalidOperationException(
                $"Scene-status projector '{projectorID}' returned incompatible root type "
                + $"'{projection.GetType().FullName}'; section requires exact type '{expectedRootType.FullName}'.")
            : projection;
    }

    private ISceneStatusProjector Resolve(string projectorID, Type expectedRootType)
    {
        ValidateProjectorID(projectorID);
        return !_projectors.TryGetValue(projectorID, out ISceneStatusProjector? projector)
            ? throw new InvalidOperationException(
                $"CurrentSceneStatus projection section requires direct projector ID '{projectorID}', but none is authored.")
            : projector.ProjectionType != expectedRootType
            ? throw new InvalidOperationException(
                $"CurrentSceneStatus projection section bound to projector '{projectorID}' requires exact root type "
                + $"'{expectedRootType.FullName}', but the projector declares '{projector.ProjectionType.FullName}'.")
            : projector;
    }

    private static void ValidateProjector(ISceneStatusProjector projector, Godot.Node node)
    {
        ValidateProjectorID(projector.ProjectorID);
        Type projectionType = projector.ProjectionType
            ?? throw new InvalidOperationException(
                $"Scene-status projector '{projector.ProjectorID}' at '{node.GetPath()}' declares a null projection type.");
        if (projectionType.IsAbstract
            || projectionType.IsInterface
            || !typeof(ISceneStatusProjection).IsAssignableFrom(projectionType))
        {
            throw new InvalidOperationException(
                $"Scene-status projector '{projector.ProjectorID}' at '{node.GetPath()}' must declare one concrete "
                + $"{nameof(ISceneStatusProjection)} root type, not '{projectionType.FullName}'.");
        }
    }

    private static void ValidateProjectorID(string? projectorID)
    {
        if (string.IsNullOrWhiteSpace(projectorID) || !string.Equals(projectorID, projectorID.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Scene-status projector IDs must be non-empty exact strings without leading or trailing whitespace.");
        }
    }
}
