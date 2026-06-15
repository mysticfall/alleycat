namespace AlleyCat.Core.Installer;

/// <summary>
/// Defines a composable scene setup module that installs itself into an explicit target context.
/// </summary>
/// <typeparam name="TContext">The strongly-typed installation context required by the installer.</typeparam>
public interface ISceneInstaller<in TContext>
    where TContext : SceneInstallationContext
{
    /// <summary>
    /// Installs this module into the supplied target context.
    /// </summary>
    /// <param name="context">The explicit installation target.</param>
    /// <returns>The installation outcome.</returns>
    SceneInstallationResult Install(TContext context);
}

/// <summary>
/// Defines a generic-purpose scene setup module that only requires the base scene installation context.
/// </summary>
public interface ISceneInstaller : ISceneInstaller<SceneInstallationContext>
{
}
