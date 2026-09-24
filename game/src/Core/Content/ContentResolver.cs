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
    private readonly string? _defaultPackId;
    private readonly Lock _contentContextGate = new();
    private ContentContext? _cachedContentContext;

    /// <summary>
    /// Initialises a new <see cref="ContentResolver"/>, loading the content manifest if present.
    /// </summary>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    public ContentResolver(ILogger<ContentResolver>? logger = null)
        : this(logger, LoadManifestIfPresent(logger ?? NullLogger<ContentResolver>.Instance)?.DefaultPackId)
    {
    }

    /// <summary>
    /// Initialises a new <see cref="ContentResolver"/> with an explicit default pack identifier, bypassing
    /// manifest discovery. Internal seam for Godot-free unit coverage of the instance resolution path.
    /// </summary>
    /// <param name="logger">Optional logger; defaults to a no-op logger.</param>
    /// <param name="defaultPackId">Manifest default pack identifier, or null when no manifest default applies.</param>
    internal ContentResolver(ILogger<ContentResolver>? logger, string? defaultPackId)
    {
        _logger = logger ?? NullLogger<ContentResolver>.Instance;
        _defaultPackId = defaultPackId;
    }

    /// <inheritdoc />
    public string ResolveStartScenePath(string fallbackStartScenePath)
    {
        (bool isIntegrationTest, string? requestedPackId, bool skipContentPack, Func<string, bool> sceneExists) = ReadRuntimeContentInputs();
        string? defaultPackId = _defaultPackId;

        string resolved = SelectStartScenePath(
            requestedPackId,
            defaultPackId,
            isIntegrationTest,
            skipContentPack,
            sceneExists,
            fallbackStartScenePath);

        if (skipContentPack)
        {
            _logger.LogInformation(
                "Content pack selection skipped by {Switch}; using built-in default content.",
                ContentPaths.SkipContentPackSwitch);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Resolved start scene {ResolvedPath} (requested={RequestedPack}, default={DefaultPack}, integrationTest={IntegrationTest}, contentPackSkipped={ContentPackSkipped}).",
                resolved,
                requestedPackId,
                defaultPackId,
                isIntegrationTest,
                skipContentPack);
        }

        return resolved;
    }

    /// <inheritdoc />
    public ContentContext GetCurrentContentContext()
    {
        if (_cachedContentContext is { } cachedContext)
        {
            return cachedContext;
        }

        lock (_contentContextGate)
        {
            return _cachedContentContext ??= ResolveContentContext();
        }
    }

    /// <summary>
    /// Resolves the active content context from the current runtime inputs.
    /// </summary>
    /// <remarks>
    /// Content inputs (command-line pack request, content-pack skip switch, manifest default pack, integration-test
    /// mode) are fixed for the process lifetime, so <see cref="GetCurrentContentContext"/> caches the resolved
    /// context instead of re-resolving it on every call. Initialisation is guarded because first access can race
    /// between the per-frame attention loop and perception handling.
    /// </remarks>
    private ContentContext ResolveContentContext()
    {
        (bool isIntegrationTest, string? requestedPackId, bool skipContentPack, Func<string, bool> sceneExists) = ReadRuntimeContentInputs();
        string? defaultPackId = _defaultPackId;

        ContentContext context = SelectCurrentContentContext(
            requestedPackId,
            defaultPackId,
            isIntegrationTest,
            skipContentPack,
            sceneExists);

        if (skipContentPack)
        {
            _logger.LogInformation(
                "Content pack selection skipped by {Switch}; using built-in default content.",
                ContentPaths.SkipContentPackSwitch);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Resolved content context {ContentID} at {RootPath} (requested={RequestedPack}, default={DefaultPack}, integrationTest={IntegrationTest}, contentPackSkipped={ContentPackSkipped}).",
                context.ContentID,
                context.RootPath,
                requestedPackId,
                defaultPackId,
                isIntegrationTest,
                skipContentPack);
        }

        return context;
    }

    /// <summary>
    /// Reads the process-level inputs used for content resolution. Internal virtual seam so Godot-free unit
    /// coverage can substitute the runtime reads and count resolution attempts.
    /// </summary>
    /// <returns>
    /// Integration-test flag, optional requested pack identifier, content-pack skip flag, and start-scene
    /// existence probe.
    /// </returns>
    internal virtual (bool IsIntegrationTest, string? RequestedPackId, bool SkipContentPack, Func<string, bool> SceneExists) ReadRuntimeContentInputs()
        => (RuntimeContext.IsIntegrationTest(), ReadRequestedPackId(), ContainsSkipContentPackSwitch(OS.GetCmdlineUserArgs()), static path => ResourceLoader.Exists(path));

    /// <summary>
    /// Determines whether the supplied command-line user arguments contain the content-pack skip switch.
    /// </summary>
    /// <remarks>
    /// Pure and free of Godot APIs so switch matching stays unit-testable without a Godot runtime.
    /// </remarks>
    public static bool ContainsSkipContentPackSwitch(IReadOnlyList<string> userArgs)
    {
        ArgumentNullException.ThrowIfNull(userArgs);
        return userArgs.Contains(ContentPaths.SkipContentPackSwitch, StringComparer.Ordinal);
    }

    /// <summary>
    /// Pure, Godot-free selection logic used to pick the start scene path.
    /// </summary>
    public static string SelectStartScenePath(
        string? requestedPackId,
        string? defaultPackId,
        bool isIntegrationTest,
        bool skipContentPack,
        Func<string, bool> sceneExists,
        string fallbackStartScenePath,
        string contentRoot = ContentPaths.ContentRoot,
        string startSceneFileName = ContentPaths.StartSceneFileName)
    {
        if (isIntegrationTest || skipContentPack)
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

    /// <summary>
    /// Pure, Godot-free selection logic used to resolve the active content context.
    /// </summary>
    public static ContentContext SelectCurrentContentContext(
        string? requestedPackId,
        string? defaultPackId,
        bool isIntegrationTest,
        bool skipContentPack,
        Func<string, bool> sceneExists,
        string contentRoot = ContentPaths.ContentRoot,
        string startSceneFileName = ContentPaths.StartSceneFileName)
    {
        ArgumentNullException.ThrowIfNull(sceneExists);

        if (isIntegrationTest || skipContentPack)
        {
            return ContentContext.Default;
        }

        if (!string.IsNullOrEmpty(requestedPackId))
        {
            string path = contentRoot + requestedPackId + "/" + startSceneFileName;
            return sceneExists(path)
                ? new ContentContext(requestedPackId, contentRoot + requestedPackId + "/")
                : throw CreateMissingRequestedPackSceneException(requestedPackId, path);
        }

        if (!string.IsNullOrEmpty(defaultPackId))
        {
            string path = contentRoot + defaultPackId + "/" + startSceneFileName;
            if (sceneExists(path))
            {
                return new ContentContext(defaultPackId, contentRoot + defaultPackId + "/");
            }
        }

        return ContentContext.Default;
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
