using AlleyCat.Motion;
using Godot;
using FileAccess = Godot.FileAccess;

namespace AlleyCat.Animations;

/// <summary>
/// Production resource checks for animation packs, backed by the Godot resource system and the existing ANIM-002
/// reviewed-set loader.
/// </summary>
public sealed class GodotAnimationPackResources : IAnimationPackResources
{
    private const string CalibrationPath = "res://data/motionbricks/poses.json";

    // Reviewed sets are immutable shipped data; verify each one once per process rather than once per character.
    private static readonly Dictionary<string, string?> _reviewedSetResults = new(StringComparer.Ordinal);
    private static readonly Lock _reviewedSetLock = new();
    private static SomaMotionData? _calibration;

    /// <summary>Shared production instance.</summary>
    public static GodotAnimationPackResources Instance { get; } = new();

    /// <inheritdoc />
    public string ReadText(string resourcePath)
    {
        using var file = FileAccess.Open(resourcePath, FileAccess.ModeFlags.Read);
        return file is null
            ? throw new FileNotFoundException($"Cannot open '{resourcePath}': {FileAccess.GetOpenError()}.", resourcePath)
            : file.GetAsText();
    }

    /// <inheritdoc />
    public bool Exists(string resourcePath)
        => FileAccess.FileExists(resourcePath) || ResourceLoader.Exists(resourcePath);

    /// <inheritdoc />
    public string? CheckClip(string resourcePath)
    {
        Resource? resource = ResourceLoader.Load(resourcePath);
        return resource is Animation
            ? null
            : $"clip must be a loadable native Animation resource, found {(resource is null ? "nothing loadable" : resource.GetClass())}";
    }

    /// <inheritdoc />
    public string? CheckReviewedSet(string resourcePath)
    {
        lock (_reviewedSetLock)
        {
            if (_reviewedSetResults.TryGetValue(resourcePath, out string? cached))
            {
                return cached;
            }

            string? result;
            try
            {
                _calibration ??= new SomaMotionData(ProjectSettings.GlobalizePath(CalibrationPath));
                _ = new ReviewedRoleSampler(ProjectSettings.GlobalizePath(resourcePath), _calibration);
                result = null;
            }
            catch (Exception error) when (error is InvalidDataException or IOException or System.Text.Json.JsonException
                                              or KeyNotFoundException or InvalidOperationException or FormatException
                                              or ArgumentException)
            {
                result = $"the ANIM-002 reviewed-set loader rejects the set: {error.Message}";
            }

            _reviewedSetResults[resourcePath] = result;
            return result;
        }
    }
}
