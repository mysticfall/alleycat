namespace AlleyCat.Core.Configuration;

/// <summary>
/// Resolves Godot resource paths to filesystem paths accepted by .NET configuration providers.
/// </summary>
public interface IConfigurationPathResolver
{
    /// <summary>
    /// Converts a Godot or filesystem path to a physical filesystem path.
    /// </summary>
    /// <param name="path">Path using <c>res://</c>, <c>user://</c>, or an already-physical path.</param>
    /// <returns>Physical filesystem path.</returns>
    string ToPhysicalPath(string path);
}
