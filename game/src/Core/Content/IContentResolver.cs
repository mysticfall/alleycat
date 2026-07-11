namespace AlleyCat.Core.Content;

/// <summary>
/// Resolves the start scene path for the active content pack.
/// </summary>
public interface IContentResolver
{
    /// <summary>
    /// Resolves the start scene path. Never returns null; returns
    /// <paramref name="fallbackStartScenePath"/> when no content pack applies.
    /// </summary>
    /// <param name="fallbackStartScenePath">Start scene used when no content pack resolves.</param>
    /// <returns>The resolved start scene path.</returns>
    string ResolveStartScenePath(string fallbackStartScenePath);
}
