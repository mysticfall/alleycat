using AlleyCat.Testing;
using Godot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AlleyCat.Core.Content;

/// <summary>
/// Resolves the start scene path from the active content pack, the default pack,
/// or a configured fallback.
/// </summary>
public partial class ContentResolver : IContentResolver
{
    private readonly ILogger<ContentResolver> _logger;
    private readonly ContentManifest? _manifest;

    /// <summary>
    /// Initialises a new <see cref="ContentResolver"/>, loading the content manifest if present.
    /// </summary>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    public ContentResolver(ILogger<ContentResolver>? logger = null)
    {
        _logger = logger ?? NullLogger<ContentResolver>.Instance;
        _manifest = LoadManifestIfPresent(_logger);
    }

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
            return sceneExists(path)
                ? path
                : throw CreateMissingRequestedPackSceneException(requestedPackId, path);
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

    private static InvalidOperationException CreateMissingRequestedPackSceneException(
        string requestedPackId,
        string expectedScenePath)
        => new(
            $"Requested content pack '{requestedPackId}' does not provide the expected start scene '{expectedScenePath}'.");

    private static ContentManifest? LoadManifestIfPresent(ILogger<ContentResolver> logger)
    {
        if (!ResourceLoader.Exists(ContentPaths.ManifestPath))
        {
            logger.LogInformation(
                "User content manifest {ManifestPath} is missing; fallback start-scene resolution will continue.",
                ContentPaths.ManifestPath);
            return null;
        }

        return GD.Load<ContentManifest>(ContentPaths.ManifestPath);
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
