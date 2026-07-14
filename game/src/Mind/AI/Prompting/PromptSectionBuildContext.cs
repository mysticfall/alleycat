using AlleyCat.Scene;

namespace AlleyCat.Mind.AI.Prompting;

/// <summary>
/// Services and scene state available while building runtime-backed prompt sections.
/// </summary>
public sealed record PromptSectionBuildContext
{
    /// <summary>
    /// Creates a build context after validating required values.
    /// </summary>
    /// <param name="services">Service provider used for prompt-section dependencies.</param>
    /// <param name="scene">Current scene context.</param>
    public PromptSectionBuildContext(IServiceProvider services, ISceneContext scene)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(scene);

        Services = services;
        Scene = scene;
    }

    /// <summary>
    /// Gets the service provider used for prompt-section dependencies.
    /// </summary>
    public IServiceProvider Services
    {
        get;
    }

    /// <summary>
    /// Gets the current scene context.
    /// </summary>
    public ISceneContext Scene
    {
        get;
    }
}
