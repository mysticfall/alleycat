namespace AlleyCat.Core.Content;

/// <summary>
/// Provides well-known paths and identifiers for content-pack resolution.
/// </summary>
public static class ContentPaths
{
    /// <summary>Identity of the built-in fallback content.</summary>
    public const string DefaultContentID = "default";

    /// <summary>Root path of the built-in fallback content.</summary>
    public const string DefaultRootPath = "res://";

    /// <summary>Root directory holding all content packs, each in its own sub-folder.</summary>
    public const string ContentRoot = "res://content/";

    /// <summary>Resource path of the content manifest describing the default pack.</summary>
    public const string ManifestPath = "res://content/manifest.tres";

    /// <summary>File name of the start scene inside a content-pack folder.</summary>
    public const string StartSceneFileName = "start.tscn";

    /// <summary>Command-line argument used to request a specific content pack.</summary>
    public const string CommandLineArgument = "--content-pack";

    /// <summary>
    /// Returns the root resource path for an optional content pack.
    /// </summary>
    public static string GetPackRootPath(string packID) => ContentRoot + packID + "/";
}
