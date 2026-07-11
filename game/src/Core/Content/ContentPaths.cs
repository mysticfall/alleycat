namespace AlleyCat.Core.Content;

/// <summary>
/// Provides well-known paths and identifiers for content-pack resolution.
/// </summary>
public static class ContentPaths
{
    /// <summary>Root directory holding all content packs, each in its own sub-folder.</summary>
    public const string ContentRoot = "res://content/";

    /// <summary>Resource path of the content manifest describing the default pack.</summary>
    public const string ManifestPath = "res://content/manifest.tres";

    /// <summary>File name of the start scene inside a content-pack folder.</summary>
    public const string StartSceneFileName = "start.tscn";

    /// <summary>Command-line argument used to request a specific content pack.</summary>
    public const string CommandLineArgument = "--content-pack";
}
