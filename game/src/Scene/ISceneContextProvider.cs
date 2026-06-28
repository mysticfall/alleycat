namespace AlleyCat.Scene;

/// <summary>
/// Provides snapshots of the active scene context.
/// </summary>
public interface ISceneContextProvider
{
    /// <summary>
    /// Captures the current scene context membership.
    /// </summary>
    /// <returns>A snapshot whose collection membership remains fixed after creation.</returns>
    ISceneContext GetCurrent();
}
