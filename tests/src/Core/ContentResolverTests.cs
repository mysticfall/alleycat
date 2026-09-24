using AlleyCat.Core.Content;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.Tests.Core;

/// <summary>
/// Tests the Godot-free content-pack selection logic, including the <c>--no-content-pack</c> skip switch.
/// </summary>
public sealed class ContentResolverTests
{
    private const string ContentRoot = "res://content/";
    private const string Fallback = "res://assets/scenes/empty.tscn";
    private const string RequestedPath = "res://content/req/start.tscn";
    private const string DefaultPath = "res://content/def/start.tscn";

    /// <summary>
    /// The switch literal is part of the CLI contract and must stay exactly <c>--no-content-pack</c>.
    /// </summary>
    [Fact]
    public void SkipContentPackSwitch_LiteralIsTheDocumentedCommandLineSwitch()
        => Assert.Equal("--no-content-pack", ContentPaths.SkipContentPackSwitch);

    /// <summary>
    /// An exact switch match among the user arguments activates the content-pack skip.
    /// </summary>
    [Fact]
    public void ContainsSkipContentPackSwitch_WithExactSwitch_ReturnsTrue()
        => Assert.True(ContentResolver.ContainsSkipContentPackSwitch(["--no-content-pack"]));

    /// <summary>
    /// The switch activates regardless of its position among other user arguments.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ContainsSkipContentPackSwitch_InAnyArgumentPosition_ReturnsTrue(int switchIndex)
    {
        string[] args = ["--integration-test-session", "foo", "--output-dir", "x", "--verbose"];
        args[switchIndex] = ContentPaths.SkipContentPackSwitch;

        Assert.True(ContentResolver.ContainsSkipContentPackSwitch(args));
    }

    /// <summary>
    /// An empty user-argument list never activates the content-pack skip.
    /// </summary>
    [Fact]
    public void ContainsSkipContentPackSwitch_WithoutArguments_ReturnsFalse()
        => Assert.False(ContentResolver.ContainsSkipContentPackSwitch([]));

    /// <summary>
    /// User arguments without the switch — mirroring the integration runner's arguments — never activate the
    /// content-pack skip.
    /// </summary>
    [Fact]
    public void ContainsSkipContentPackSwitch_WithOtherUserArguments_ReturnsFalse()
    {
        Assert.False(ContentResolver.ContainsSkipContentPackSwitch(
            ["--integration-test-session", "--probe-assembly", "/tmp/assembly.dll", "--output-dir", "x"]));
    }

    /// <summary>
    /// Only the exact switch matches: case differences, single-dash spellings, and suffixed variants never
    /// activate the content-pack skip.
    /// </summary>
    [Theory]
    [InlineData("--no-content-pack-extra")]
    [InlineData("--no-content-pack=")]
    [InlineData("-no-content-pack")]
    [InlineData("--No-Content-Pack")]
    public void ContainsSkipContentPackSwitch_WithNearMissSwitches_ReturnsFalse(string nearMiss)
        => Assert.False(ContentResolver.ContainsSkipContentPackSwitch([nearMiss]));

    /// <summary>
    /// A null user-argument list is a caller bug and must fail fast.
    /// </summary>
    [Fact]
    public void ContainsSkipContentPackSwitch_WithNullArguments_Throws()
        => Assert.Throws<ArgumentNullException>(
            () => _ = ContentResolver.ContainsSkipContentPackSwitch(null!));

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
            skipContentPack: false,
            sceneExists: _ => throw new InvalidOperationException("Integration-test bypass should not probe content."),
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: true,
            skipContentPack: false,
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
            skipContentPack: false,
            sceneExists: p => p == RequestedPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(RequestedPath, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            skipContentPack: false,
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
            skipContentPack: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(DefaultPath, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: null,
            defaultPackId: "def",
            isIntegrationTest: false,
            skipContentPack: false,
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
            skipContentPack: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            skipContentPack: false,
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
            skipContentPack: false,
            sceneExists: p => p == DefaultPath,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot));

        Assert.Contains("req", exception.Message, StringComparison.Ordinal);
        Assert.Contains(RequestedPath, exception.Message, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            skipContentPack: false,
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
            skipContentPack: false,
            sceneExists: _ => false,
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }

    /// <summary>
    /// An active skip switch must return the fallback without probing any pack scene, even when a pack is
    /// requested and a default pack is configured.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenContentPackSkipped()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            skipContentPack: true,
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
    }

    /// <summary>
    /// An active skip switch must resolve the built-in context without probing any pack scene, even when a pack
    /// is requested and a default pack is configured.
    /// </summary>
    [Fact]
    public void SelectCurrentContentContext_ReturnsBuiltInContext_WhenContentPackSkipped()
    {
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: "req",
            defaultPackId: "def",
            isIntegrationTest: false,
            skipContentPack: true,
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            contentRoot: ContentRoot);

        Assert.Equal("default", context.ContentID);
        Assert.Equal("res://", context.RootPath);
    }

    /// <summary>
    /// An active skip switch with no requested pack and no default pack configured must behave identically to the
    /// no-pack baseline: the fallback start scene and the built-in content context, without probing any pack scene.
    /// </summary>
    [Fact]
    public void SelectStartScenePath_ReturnsFallback_WhenContentPackSkippedAndNothingConfigured()
    {
        string result = ContentResolver.SelectStartScenePath(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            skipContentPack: true,
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            fallbackStartScenePath: Fallback,
            contentRoot: ContentRoot);

        Assert.Equal(Fallback, result);
        ContentContext context = ContentResolver.SelectCurrentContentContext(
            requestedPackId: null,
            defaultPackId: null,
            isIntegrationTest: false,
            skipContentPack: true,
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            contentRoot: ContentRoot);
        Assert.Equal(ContentContext.Default, context);
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
    /// A resolver with an active skip switch must resolve the built-in context once and cache it, ignoring the
    /// requested and default packs.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_ReturnsBuiltInContext_AndCachesIt_WhenContentPackSkipped()
    {
        StubContentResolver resolver = new(
            skipContentPack: true,
            requestedPackId: "req",
            defaultPackId: "def",
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."));

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
    /// The resolution log must fire once, with the exact current template message including null-pack rendering;
    /// the only template amendment is the appended <c>contentPackSkipped</c> field, with no other rewording.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_LogsResolutionOnce_WithStableTemplateMessage()
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
            "Resolved content context def at res://content/def/ (requested=(null), default=def, integrationTest=False, contentPackSkipped=False).",
            Message);
    }

    /// <summary>
    /// An active skip switch must emit exactly one Information notice mentioning the switch — the context cache
    /// limits it to one across repeated reads — and render the skipped flag in the resolution debug message.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_LogsSkipNoticeAndSkippedField_WhenContentPackSkipped()
    {
        CapturingLogger logger = new();
        StubContentResolver resolver = new(
            skipContentPack: true,
            requestedPackId: "req",
            defaultPackId: "def",
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            logger: logger);

        _ = resolver.GetCurrentContentContext();
        _ = resolver.GetCurrentContentContext();

        string noticeMessage = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information).Message;
        Assert.Contains(ContentPaths.SkipContentPackSwitch, noticeMessage, StringComparison.Ordinal);
        string debugMessage = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug).Message;
        Assert.Equal(
            "Resolved content context default at res:// (requested=req, default=def, integrationTest=False, contentPackSkipped=True).",
            debugMessage);
    }

    /// <summary>
    /// Start-scene resolution with an active skip switch must emit exactly one Information notice mentioning the
    /// switch, render the skip flag in the exact start-scene debug message, and return the fallback.
    /// </summary>
    [Fact]
    public void ResolveStartScenePath_LogsSkipNotice_WhenContentPackSkipped()
    {
        CapturingLogger logger = new();
        StubContentResolver resolver = new(
            skipContentPack: true,
            requestedPackId: "req",
            defaultPackId: "def",
            sceneExists: _ => throw new InvalidOperationException("Skip bypass should not probe content."),
            logger: logger);

        string result = resolver.ResolveStartScenePath(Fallback);

        Assert.Equal(Fallback, result);
        string noticeMessage = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information).Message;
        Assert.Contains(ContentPaths.SkipContentPackSwitch, noticeMessage, StringComparison.Ordinal);
        string debugMessage = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Debug).Message;
        Assert.Equal(
            "Resolved start scene res://assets/scenes/empty.tscn (requested=req, default=def, integrationTest=False, contentPackSkipped=True).",
            debugMessage);
    }

    /// <summary>
    /// Without the skip switch, resolution must not emit the Information skip notice.
    /// </summary>
    [Fact]
    public void GetCurrentContentContext_DoesNotLogSkipNotice_WhenSkipInactive()
    {
        CapturingLogger logger = new();
        StubContentResolver resolver = new(
            requestedPackId: null,
            defaultPackId: "def",
            sceneExists: _ => true,
            logger: logger);

        _ = resolver.GetCurrentContentContext();

        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
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
        bool skipContentPack = false,
        string? requestedPackId = null,
        string? defaultPackId = null,
        Func<string, bool>? sceneExists = null,
        ILogger<ContentResolver>? logger = null) : ContentResolver(logger, defaultPackId)
    {
        public int RuntimeInputReadCount
        {
            get; private set;
        }

        internal override (bool IsIntegrationTest, string? RequestedPackId, bool SkipContentPack, Func<string, bool> SceneExists) ReadRuntimeContentInputs()
        {
            RuntimeInputReadCount++;
            return (isIntegrationTest, requestedPackId, skipContentPack, sceneExists ?? (static _ => false));
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
