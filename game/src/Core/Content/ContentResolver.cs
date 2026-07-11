using AlleyCat.Testing;
using Godot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Core.Content;

/// <summary>
/// Resolves the start scene path from the active content pack, the default pack,
/// or a configured fallback.
/// </summary>
/// <remarks>
/// Initialises a new <see cref="ContentResolver"/>, loading the content manifest if present.
/// </remarks>
/// <param name="logger">Optional logger; defaults to a no-op logger.</param>
public partial class ContentResolver(ILogger<ContentResolver>? logger = null) : IContentResolver
{
    private readonly ContentManifest? _manifest = GD.Load<ContentManifest>(ContentPaths.ManifestPath);
    private readonly ILogger<ContentResolver> _logger = logger ?? NullLogger<ContentResolver>.Instance;

    /// <inheritdoc />
    public string ResolveStartScenePath(string fallbackStartScenePath)
    {
        bool isIntegrationTest = RuntimeContext.IsIntegrationTest();
        string? requestedPackId = ReadRequestedPackId();
        string? defaultPackId = _manifest?.DefaultPackId;

        string resolved = SelectStartScenePath(
            requestedPackId,
            defaultPackId,
            isIntegrationTest,
            p => ResourceLoader.Exists(p),
            fallbackStartScenePath);

        _logger.LogDebug(
            "Resolved start scene {ResolvedPath} (requested={RequestedPack}, default={DefaultPack}, integrationTest={IntegrationTest}).",
            resolved,
            requestedPackId,
            defaultPackId,
            isIntegrationTest);

        return resolved;
    }

    /// <summary>
    /// Pure, Godot-free selection logic used to pick the start scene path.
    /// </summary>
    public static string SelectStartScenePath(
        string? requestedPackId,
        string? defaultPackId,
        bool isIntegrationTest,
        Func<string, bool> sceneExists,
        string fallbackStartScenePath,
        string contentRoot = ContentPaths.ContentRoot,
        string startSceneFileName = ContentPaths.StartSceneFileName)
    {
        if (isIntegrationTest)
        {
            return fallbackStartScenePath;
        }

        if (!string.IsNullOrEmpty(requestedPackId))
        {
            string path = contentRoot + requestedPackId + "/" + startSceneFileName;
            if (sceneExists(path))
            {
                return path;
            }
        }

        if (!string.IsNullOrEmpty(defaultPackId))
        {
            string path = contentRoot + defaultPackId + "/" + startSceneFileName;
            if (sceneExists(path))
            {
                return path;
            }
        }

        return fallbackStartScenePath;
    }

    private static string? ReadRequestedPackId()
    {
        string[] args = OS.GetCmdlineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!string.Equals(arg, ContentPaths.CommandLineArgument, StringComparison.Ordinal))
            {
                if (arg.StartsWith(ContentPaths.CommandLineArgument + "=", StringComparison.Ordinal))
                {
                    return arg[(ContentPaths.CommandLineArgument.Length + 1)..];
                }

                continue;
            }

            return i + 1 < args.Length ? args[i + 1] : null;
        }

        return null;
    }
}
