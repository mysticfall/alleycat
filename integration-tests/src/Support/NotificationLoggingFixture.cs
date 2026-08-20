using AlleyCat.Core.Logging;
using AlleyCat.Core.Threading;
using AlleyCat.UI;
using Godot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Installs the real AlleyCat logging infrastructure — the console and notification logger providers registered
/// by <c>AddAlleyCatLogging</c> — against a controllable global UI overlay hierarchy, with the notification sink
/// bound to the production main-thread dispatcher.
/// </summary>
/// <remarks>
/// <para>
/// The logger factory is installed through <see cref="PipelineDebugLog.SetLoggerFactoryForTesting(ILoggerFactory?)" />
/// so <see cref="PipelineDebugLog" /> entries flow through the real providers, and the configuration opts the
/// pipeline category into trace logging the same way a player opts in through a user configuration override.
/// </para>
/// <para>
/// The production autoload already owns a node named <c>Global</c>; the fixture displaces it so the notification
/// sink's absolute overlay path resolves the fixture's hierarchy instead.
/// </para>
/// </remarks>
internal sealed class NotificationLoggingFixture
{
    private const string DisplacedGlobalName = "Global_NotificationLoggingFixture";
    private const string PipelineCategoryName = "AlleyCat.Pipeline";

    private readonly IConfigurationRoot _configuration;
    private readonly ServiceProvider _serviceProvider;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Node _hierarchyRoot;
    private readonly Node? _displacedGlobalRoot;
    private readonly VBoxContainer _messages;
    private readonly CapturingLoggerProvider _capturingProvider;

    private NotificationLoggingFixture(
        IConfigurationRoot configuration,
        ServiceProvider serviceProvider,
        ILoggerFactory loggerFactory,
        Node hierarchyRoot,
        Node? displacedGlobalRoot,
        NotificationWidget notificationWidget,
        CapturingLoggerProvider capturingProvider)
    {
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _loggerFactory = loggerFactory;
        _hierarchyRoot = hierarchyRoot;
        _displacedGlobalRoot = displacedGlobalRoot;
        _messages = notificationWidget.GetNode<VBoxContainer>("Messages");
        _capturingProvider = capturingProvider;
    }

    /// <summary>
    /// Creates the overlay hierarchy and installs the real logging infrastructure against it.
    /// </summary>
    /// <param name="sceneTree">The scene tree to attach the overlay hierarchy to.</param>
    /// <param name="enablePipelineTraceLogging">
    /// Whether the configuration opts the <c>AlleyCat.Pipeline</c> category into trace logging, mirroring a player's
    /// user configuration override. When false, the category stays at the default information floor, which keeps
    /// pipeline diagnostics — console and notification alike — switched off.
    /// </param>
    public static async Task<NotificationLoggingFixture> CreateAsync(
        SceneTree sceneTree,
        bool enablePipelineTraceLogging = true)
    {
        Node? displacedGlobalRoot = sceneTree.Root.GetNodeOrNull<Node>("Global");
        if (displacedGlobalRoot is not null)
        {
            displacedGlobalRoot.Name = DisplacedGlobalName;
            await WaitForNextFrameAsync(sceneTree);
        }

        Node hierarchyRoot = CreateGlobalHierarchy(out NotificationWidget notificationWidget);
        sceneTree.Root.AddChild(hierarchyRoot);
        await WaitForFramesAsync(sceneTree, 2);

        Dictionary<string, string?> configurationValues = [];
        if (enablePipelineTraceLogging)
        {
            configurationValues["Logging:LogLevel:AlleyCat.Pipeline"] = "Trace";
        }

        ConfigurationBuilder configurationBuilder = new();
        _ = configurationBuilder.AddInMemoryCollection(configurationValues);
        IConfigurationRoot configuration = configurationBuilder.Build();

        ILogNotificationSink sink = new GodotUINotificationSink(
            sceneTree.Root,
            Game.Instance.GetRequiredService<IMainThreadDispatcher>());

        ServiceCollection services = [];
        CapturingLoggerProvider capturingProvider = new();
        _ = services.AddAlleyCatLogging(configuration, sink);
        _ = services.AddLogging(builder => builder.AddProvider(capturingProvider));
        ServiceProvider serviceProvider = services.BuildServiceProvider();
        ILoggerFactory loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();

        PipelineDebugLog.SetLoggerFactoryForTesting(loggerFactory);

        return new NotificationLoggingFixture(
            configuration,
            serviceProvider,
            loggerFactory,
            hierarchyRoot,
            displacedGlobalRoot,
            notificationWidget,
            capturingProvider);
    }

    /// <summary>
    /// Creates a logger from the installed real logger factory.
    /// </summary>
    public ILogger CreateLogger(string categoryName) => _loggerFactory.CreateLogger(categoryName);

    /// <summary>
    /// Reads the notification texts currently displayed by the fixture's notification widget.
    /// </summary>
    public IReadOnlyList<string> GetNotificationTexts()
    {
        List<string> texts = [];
        for (int index = 0; index < _messages.GetChildCount(); index++)
        {
            if (_messages.GetChild(index) is Label label)
            {
                texts.Add(label.Text);
            }
        }

        return texts;
    }

    /// <summary>
    /// Reads the captured console-side messages logged under the pipeline category, so tests can verify log-only
    /// stages keep full console coverage without posting toasts.
    /// </summary>
    public IReadOnlyList<string> GetPipelineLogMessages()
        => _capturingProvider.GetMessages(PipelineCategoryName);

    /// <summary>
    /// Removes the logger override and restores the displaced global node.
    /// </summary>
    public async Task DestroyAsync(SceneTree sceneTree)
    {
        PipelineDebugLog.SetLoggerFactoryForTesting(null);
        _serviceProvider.Dispose();
        if (_configuration is IDisposable disposableConfiguration)
        {
            disposableConfiguration.Dispose();
        }

        _hierarchyRoot.QueueFree();
        await WaitForFramesAsync(sceneTree, 2);

        if (_displacedGlobalRoot is not null && GodotObject.IsInstanceValid(_displacedGlobalRoot))
        {
            _displacedGlobalRoot.Name = "Global";
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    private static Node CreateGlobalHierarchy(out NotificationWidget notificationWidget)
    {
        Node global = new()
        {
            Name = "Global",
        };

        Node xr = new()
        {
            Name = "XR",
        };

        SubViewport subViewport = new()
        {
            Name = "SubViewport",
            Disable3D = true,
        };

        UIOverlay overlay = new()
        {
            Name = "UIOverlay",
        };

        notificationWidget = new NotificationWidget()
        {
            Name = "NotificationOverlay",
        };

        VBoxContainer messages = new()
        {
            Name = "Messages",
        };

        notificationWidget.AddChild(messages);
        overlay.AddChild(notificationWidget);
        subViewport.AddChild(overlay);
        xr.AddChild(subViewport);
        global.AddChild(xr);

        return global;
    }

    /// <summary>
    /// Observes every entry the fixture's logger factory admits, mirroring the console provider's coverage.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<(string CategoryName, string Message)> _entries = [];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public IReadOnlyList<string> GetMessages(string categoryName)
        {
            lock (_lock)
            {
                return
                [
                    .. _entries
                        .Where(entry => string.Equals(entry.CategoryName, categoryName, StringComparison.Ordinal))
                        .Select(entry => entry.Message),
                ];
            }
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._lock)
                {
                    provider._entries.Add((categoryName, formatter(state, exception)));
                }
            }
        }
    }
}
