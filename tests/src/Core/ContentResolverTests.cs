using AlleyCat.Core.Content;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Tests the Godot-free content-pack start-scene selection logic.
/// </summary>
public sealed class ContentResolverTests
{
    private const string ContentRoot = "res://content/";
    private const string Fallback = "res://assets/scenes/empty.tscn";
    private const string RequestedPath = "res://content/req/start.tscn";
    private const string DefaultPath = "res://content/def/start.tscn";

    /// <summary>
    /// Integration-test context must always return the fallback regardless of packs.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenIntegrationTest()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            sceneExists: _ => throw new InvalidOperationException("Integration-test bypass should not probe content."),
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            sceneExists: _ => throw new InvalidOperationException("Integration-test bypass should not probe content."),
            contentRoot: ContentRoot);
        Assert.Equal("default", context.ContentID);
        Assert.Equal("res://", context.RootPath);
    }

    /// <summary>
    /// A present requested pack must take precedence over the default pack.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsRequestedPath_WhenRequestedPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == RequestedPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(RequestedPath, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == RequestedPath,
            contentRoot: ContentRoot);
        Assert.Equal("req", context.ContentID);
        Assert.Equal("res://content/req/", context.RootPath);
    }

    /// <summary>
    /// With no requested pack, the present default pack must be used.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsDefaultPath_WhenOnlyDefaultPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(DefaultPath, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            contentRoot: ContentRoot);
        Assert.Equal("def", context.ContentID);
        Assert.Equal("res://content/def/", context.RootPath);
    }

    /// <summary>
    /// With neither pack present, the fallback must be returned.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenNoPackPresent()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            sceneExists: _ => false,
            contentRoot: ContentRoot);
        Assert.Equal("default", context.ContentID);
        Assert.Equal("res://", context.RootPath);
    }

    /// <summary>
    /// A requested pack whose scene is missing must fail explicitly instead of falling through.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_Throws_WhenRequestedPackSceneMissing()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot));

        Assert.Contains("req", exception.Message, StringComparison.Ordinal);
        Assert.Contains(RequestedPath, exception.Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: p => p == DefaultPath,
            contentRoot: ContentRoot));
    }

    /// <summary>
    /// A default pack whose scene is missing must fall through to the fallback.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenDefaultPackSceneMissing()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }

    /// <summary>
    /// The current content context must be resolved once and then reused from the instance cache.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_ResolvesOnce_AndReturnsSameCachedInstance()
    {
        StubContentResolver resolver = new(requestedPackId: "req", sceneExists: _ => true);

        ContentContext first = resolver.GetCurrentContentContext();
        ContentContext second = resolver.GetCurrentContentContext();
        ContentContext third = resolver.GetCurrentContentContext();

        Assert.Equal(1, resolver.RuntimeInputReadCount);
        Assert.Same(first, second);
        Assert.Same(first, third);
        Assert.Equal("req", first.ContentID);
    }

    /// <summary>
    /// A resolver running in integration-test mode must resolve the built-in context, ignoring packs.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_ReturnsBuiltInContext_WhenIntegrationTestMode()
    {
        StubContentResolver resolver = new(
            isIntegrationTest: true,
            requestedPackId: "req",
            defaultPackId: "def",
            sceneExists: _ => throw new InvalidOperationException("Integration-test bypass should not probe content."));

        ContentContext first = resolver.GetCurrentContentContext();
        ContentContext second = resolver.GetCurrentContentContext();

        Assert.Equal(ContentContext.Default, first);
        Assert.Same(first, second);
        Assert.Equal(1, resolver.RuntimeInputReadCount);
    }

    /// <summary>
    /// A failed resolution must not poison the cache; the next call retries the resolution.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_RetriesResolution_WhenRequestedPackSceneIsMissing()
    {
        int probeCallCount = 0;
        StubContentResolver resolver = new(requestedPackId: "req", sceneExists: _ => ++probeCallCount > 1);

        _ = Assert.Throws<InvalidOperationException>(resolver.GetCurrentContentContext);

        ContentContext context = resolver.GetCurrentContentContext();

        Assert.Equal(2, resolver.RuntimeInputReadCount);
        Assert.Equal("req", context.ContentID);
    }

    /// <summary>
    /// The resolution log must fire once, with the unchanged templated message including null-pack rendering.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_LogsResolutionOnce_WithUnchangedMessage()
    {
        CapturingLogger logger = new();
        StubContentResolver resolver = new(
            requestedPackId: null,
            defaultPackId: "def",
            sceneExists: p => p == "res://content/def/start.tscn",
            logger: logger);

        _ = resolver.GetCurrentContentContext();
        _ = resolver.GetCurrentContentContext();

        (LogLevel Level, string Message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, Level);
        Assert.Equal(
            "Resolved content context def at res://content/def/ (requested=(null), default=def, integrationTest=False).",
            Message);
    }

    /// <summary>
    /// The debug log must be skipped entirely, without formatting, when debug level is filtered out.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_SkipsDebugLog_WhenDebugDisabled()
    {
        CapturingLogger logger = new()
        {
            DebugEnabled = false
        };
        StubContentResolver resolver = new(
            requestedPackId: null,
            defaultPackId: "def",
            sceneExists: _ => true,
            logger: logger);

        _ = resolver.GetCurrentContentContext();
        _ = resolver.GetCurrentContentContext();

        Assert.Empty(logger.Entries);
        Assert.Equal(0, logger.FormatCount);
    }

    private sealed class StubContentResolver(
        bool isIntegrationTest = false,
        string? requestedPackId = null,
        string? defaultPackId = null,
        Func<string, bool>? sceneExists = null,
        ILogger<ContentResolver>? logger = null) : ContentResolver(logger, defaultPackId)
    {
        public int RuntimeInputReadCount
        {
            get; private set;
        }

        internal override (bool IsIntegrationTest, string? RequestedPackId, Func<string, bool> SceneExists) ReadRuntimeContentInputs()
        {
            RuntimeInputReadCount++;
            return (isIntegrationTest, requestedPackId, sceneExists ?? (static _ => false));
        }
    }

    private sealed class CapturingLogger : ILogger<ContentResolver>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public bool DebugEnabled { get; set; } = true;

        public int FormatCount
        {
            get; private set;
        }

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel)
            => logLevel is not LogLevel.None && (DebugEnabled || logLevel > LogLevel.Debug);

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            FormatCount++;
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
