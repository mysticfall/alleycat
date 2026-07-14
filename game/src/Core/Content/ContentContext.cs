namespace AlleyCat.Core.Content;

/// <summary>
/// Identifies the active content set and the root path used for content-relative resources.
/// </summary>
/// <param name="ContentID">Mandatory content identity.</param>
/// <param name="RootPath">Godot resource root for the active content.</param>
public sealed record ContentContext(string ContentID, string RootPath)
{
    /// <summary>
    /// Built-in fallback content context.
    /// </summary>
    public static ContentContext Default { get; } = new(ContentPaths.DefaultContentID, ContentPaths.DefaultRootPath);

    /// <summary>
    /// Creates a content context for an optional pack.
    /// </summary>
    public static ContentContext ForPack(string packID) => new(packID, ContentPaths.GetPackRootPath(packID));
}
