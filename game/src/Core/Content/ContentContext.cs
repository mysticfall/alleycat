namespace AlleyCat.Core.Content;

/// <summary>
/// Identifies the active content set, the root path used for content-relative resources, and the effective spoken
/// language of the selected content.
/// </summary>
/// <param name="ContentID">Mandatory content identity.</param>
/// <param name="RootPath">Godot resource root for the active content.</param>
/// <param name="Language">Canonical spoken-language code of the selected content; hand-built contexts default to English.</param>
public sealed record ContentContext(string ContentID, string RootPath, string Language = ContentPackManifest.DefaultLanguage)
{
    /// <summary>
    /// Built-in fallback content context. Built-in content speaks English.
    /// </summary>
    public static ContentContext Default { get; } = new(ContentPaths.DefaultContentID, ContentPaths.DefaultRootPath);

    /// <summary>
    /// Creates a content context for an optional pack.
    /// </summary>
    public static ContentContext ForPack(string packID) => new(packID, ContentPaths.GetPackRootPath(packID));
}
