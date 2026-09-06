using AlleyCat.Mind.AI.SceneStatus;
using AlleyCat.Templating;
using Godot;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Authored current-scene-status section bound to one stable projector ID and one exact immutable root type.
/// </summary>
[GlobalClass]
public partial class ProjectionPromptSection : PromptSection
{
    /// <summary>Stable exact ID of the required direct-child scene-status projector.</summary>
    [Export]
    public string ProjectorID { get; set; } = string.Empty;

    /// <summary>
    /// Fully qualified exact root type expected from <see cref="ProjectorID" />. The type must be defined by the game
    /// assembly and implement <see cref="ISceneStatusProjection" />.
    /// </summary>
    [Export]
    public string RootTypeName { get; set; } = string.Empty;

    /// <summary>Liquid source rendered with the projector output as the template root.</summary>
    [Export(PropertyHint.MultilineText)]
    public string TemplateSource { get; set; } = string.Empty;

    /// <inheritdoc />
    public override Task<string> GetContentAsync(
        PromptSectionBuildContext buildContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(buildContext);
        cancellationToken.ThrowIfCancellationRequested();
        _ = ResolveExpectedRootType();
        return Task.FromResult(TemplateSource ?? string.Empty);
    }

    /// <summary>Resolves and validates the authored exact projection-root type.</summary>
    internal Type ResolveExpectedRootType()
    {
        if (string.IsNullOrWhiteSpace(ProjectorID) || !string.Equals(ProjectorID, ProjectorID.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Projection prompt section requires a non-empty projector ID without leading or trailing whitespace.");
        }

        if (string.IsNullOrWhiteSpace(RootTypeName) || !string.Equals(RootTypeName, RootTypeName.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Projection prompt section bound to '{ProjectorID}' requires a non-empty exact projection root type name.");
        }

        Type? rootType = Type.GetType(RootTypeName, throwOnError: false, ignoreCase: false)
            ?? typeof(ProjectionPromptSection).Assembly.GetType(RootTypeName, throwOnError: false, ignoreCase: false);
        return rootType is null
            || rootType.IsAbstract
            || rootType.IsInterface
            || !typeof(ISceneStatusProjection).IsAssignableFrom(rootType)
            ? throw new InvalidOperationException(
                $"Projection prompt section bound to '{ProjectorID}' requires concrete {nameof(ISceneStatusProjection)} "
                + $"root type '{RootTypeName}'.")
            : rootType;
    }

    /// <summary>Renders one verified projection as the template root rather than as a dictionary value.</summary>
    internal static ValueTask<string> RenderAsync(ITemplate template, ISceneStatusProjection projection)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(projection);
        return template is IRootedTemplate rooted
            ? rooted.RenderRootedAsync(projection, EmptyNamedValues.Instance)
            : throw new InvalidOperationException(
                $"The compiled template type '{template.GetType().FullName}' cannot render a scene-status projection root.");
    }

    private static class EmptyNamedValues
    {
        public static IReadOnlyDictionary<string, object?> Instance
        {
            get;
        } =
            new Dictionary<string, object?>();
    }
}
