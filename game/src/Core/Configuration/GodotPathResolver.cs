using Godot;

namespace AlleyCat.Core.Configuration;

/// <summary>
/// Uses Godot project settings to resolve resource and user paths for non-Godot .NET APIs.
/// </summary>
public sealed class GodotPathResolver : IConfigurationPathResolver
{
    /// <inheritdoc />
    public string ToPhysicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return IsGodotPath(path)
            ? ProjectSettings.GlobalizePath(path)
            : path;
    }

    private static bool IsGodotPath(string path)
        => path.StartsWith("res://", StringComparison.Ordinal)
            || path.StartsWith("user://", StringComparison.Ordinal);
}
