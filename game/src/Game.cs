using AlleyCat.Common;
using AlleyCat.Core;
using AlleyCat.Testing;
using AlleyCat.UI;
using AlleyCat.XR;
using Godot;
using Microsoft.Extensions.DependencyInjection;

namespace AlleyCat;

/// <summary>
/// Represents the main entry point for the game logic in the AlleyCat namespace.
/// </summary>
[GlobalClass]
public partial class Game : Node, IServiceProvider
{
    private static Game? _instance;

    private readonly ServiceCollection _services = [];

    /// <summary>
    /// Scene path loaded after splash and XR startup complete.
    /// </summary>
    [Export(PropertyHint.File, "*.tscn")]
    public string StartScenePath { get; set; } = string.Empty;

    /// <summary>
    /// Splash scene instantiated during startup when splash is enabled.
    /// </summary>
    [Export]
    public PackedScene SplashScreenScene { get; set; } = null!;

    /// <summary>
    /// Resource-owned service registrars invoked before the global service provider is built.
    /// </summary>
    [Export]
    public Godot.Collections.Array<Resource> ServiceRegistrars { get; set; } = [];

    private readonly TaskCompletionSource<bool> _xrInitialisationCompletionSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private XRManager? _xrManager;
    private ServiceProvider? _serviceProvider;
    private SubViewport? _uiRoot;
    private SplashScreen? _splashScreen;
    private LoadingScreen? _loadingScreen;
    private Callable? _loadCompletedCallable;
    private bool _isSubscribedToXRInitialised;

    /// <summary>
    /// Gets the active game singleton for global service resolution.
    /// </summary>
    public static Game Instance => _instance
        ?? throw new InvalidOperationException("Game singleton is not available.");

    /// <inheritdoc />
    public override void _EnterTree()
    {
        SetInstance();
        if (_serviceProvider is null)
        {
            RegisterServices(_services);
            RegisterConfiguredServices(_services);
            RegisterSceneOwnedServices(_services);
            BuildServiceProvider();
            _xrManager = _serviceProvider!.GetRequiredService<XRManager>();
        }

        SceneTree? tree = GetTree();
        if (tree is not null && RuntimeContext.ShouldBypassGlobalStartup(tree))
        {
            return;
        }

        if (!_isSubscribedToXRInitialised)
        {
            _xrManager!.Initialised += OnXRInitialised;
            _isSubscribedToXRInitialised = true;
        }
    }

    /// <summary>
    /// Builds the service provider after startup registrations are complete.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the provider has already been built.</exception>
    public void BuildServiceProvider()
    {
        if (_serviceProvider is not null)
        {
            throw new InvalidOperationException("Game service provider has already been built.");
        }

        _serviceProvider = _services.BuildServiceProvider();
    }

    /// <summary>
    /// Resolves a registered service by type.
    /// </summary>
    /// <param name="serviceType">Service type to resolve.</param>
    /// <returns>The resolved service instance, or <c>null</c> when the service is not registered.</returns>
    public object? GetService(Type serviceType)
        => _serviceProvider is not null
            ? _serviceProvider.GetService(serviceType)
            : throw new InvalidOperationException("Game service provider has not been built.");

    /// <summary>
    /// Resolves a registered service by generic type.
    /// </summary>
    /// <typeparam name="T">Service type to resolve.</typeparam>
    /// <returns>The resolved service instance, or <c>null</c> when the service is not registered.</returns>
    public T? GetService<T>() => (T?)GetService(typeof(T));

    /// <summary>
    /// Registers startup services before the service provider is built.
    /// </summary>
    /// <param name="services">Service collection to populate.</param>
    protected virtual void RegisterServices(IServiceCollection services)
    {
    }

    private void RegisterConfiguredServices(IServiceCollection services)
    {
        foreach (IServiceRegistrar registrar in DiscoverResourceServiceRegistrars(ServiceRegistrars))
        {
            registrar.RegisterServices(services);
        }
    }

    private void RegisterSceneOwnedServices(IServiceCollection services)
    {
        foreach (IServiceRegistrar registrar in DiscoverServiceRegistrars(this))
        {
            registrar.RegisterServices(services);
        }
    }

    private static IEnumerable<IServiceRegistrar> DiscoverServiceRegistrars(Node root)
    {
        int childCount = root.GetChildCount();
        for (int i = 0; i < childCount; i++)
        {
            Node child = root.GetChild(i);
            if (child is IServiceRegistrar registrar)
            {
                yield return registrar;
            }

            foreach (IServiceRegistrar descendantRegistrar in DiscoverServiceRegistrars(child))
            {
                yield return descendantRegistrar;
            }
        }
    }

    private static IEnumerable<IServiceRegistrar> DiscoverResourceServiceRegistrars(IEnumerable<Resource?> resources)
    {
        foreach (Resource? resource in resources)
        {
            if (resource is IServiceRegistrar registrar)
            {
                yield return registrar;
            }
        }
    }

    /// <summary>
    /// Checks if the "--skip-splash" command-line argument was provided.
    /// </summary>
    /// <returns>True if the splash screen should be skipped.</returns>
    private static bool ShouldSkipSplashScreen() =>
        OS.GetCmdlineArgs().Contains("--skip-splash");

    /// <inheritdoc />
    public override void _Ready()
    {
        if (RuntimeContext.ShouldBypassGlobalStartup(GetTree()))
        {
            return;
        }

        _uiRoot = this.RequireNode<SubViewport>("XR/SubViewport");
        _loadingScreen = this.RequireNode<LoadingScreen>("XR/SubViewport/LoadingScreen");

        if (!ShouldSkipSplashScreen())
        {
            Node splashNode = SplashScreenScene.Instantiate()
                ?? throw new InvalidOperationException("Splash scene is not configured on Game.");

            _splashScreen = splashNode as SplashScreen
                ?? throw new InvalidOperationException($"Splash scene root '{splashNode.GetType().FullName}' must be a SplashScreen.");

            _uiRoot.AddChild(_splashScreen);
        }

        _ = RunStartupFlowAsync();
    }

    /// <inheritdoc />
    public override void _ExitTree()
    {
        if (_xrManager is XRManager xrManager && _isSubscribedToXRInitialised)
        {
            xrManager.Initialised -= OnXRInitialised;
            _isSubscribedToXRInitialised = false;
        }

        _xrManager = null;

        if (ReferenceEquals(_instance, this))
        {
            _instance = null;
        }

        _serviceProvider?.Dispose();
        _serviceProvider = null;
        _services.Clear();
    }

    private void SetInstance()
    {
        if (_instance is not null && !ReferenceEquals(_instance, this))
        {
            throw new InvalidOperationException("Only one Game instance can be active at a time.");
        }

        _instance = this;
    }

    private async Task RunStartupFlowAsync()
    {
        if (_splashScreen is not null)
        {
            _ = await ToSignal(_splashScreen, SplashScreen.SignalName.SplashFinished);
        }

        bool xrInitialised = await _xrInitialisationCompletionSource.Task;
        if (!xrInitialised)
        {
            GD.PushError("XR initialisation failed. Quitting the game.");
            QuitGame(1);
            return;
        }

        _loadingScreen!.Show();

        if (_splashScreen is not null)
        {
            Node? splashParent = _splashScreen.GetParent();
            splashParent?.RemoveChild(_splashScreen);
            _splashScreen.QueueFree();
            _splashScreen = null;
        }

        Callable loadCompletedCallable = _loadCompletedCallable ??= Callable.From(OnLoadCompleted);
        if (!_loadingScreen.IsConnected("LoadCompleted", loadCompletedCallable))
        {
            _ = _loadingScreen.Connect("LoadCompleted", loadCompletedCallable);
        }

        Error loadStartError = _loadingScreen.LoadSceneAsync(StartScenePath);
        if (loadStartError != Error.Ok)
        {
            GD.PushError($"Failed to start loading start scene '{StartScenePath}' with error '{loadStartError}'. Quitting the game.");
            QuitGame(1);
        }
    }

    /// <summary>
    /// Requests game shutdown with the supplied exit code.
    /// </summary>
    /// <param name="exitCode">Process exit code to return to the host.</param>
    protected virtual void QuitGame(int exitCode)
        => GetTree().Quit(exitCode);

    private void OnXRInitialised(bool succeeded)
        => _xrInitialisationCompletionSource.TrySetResult(succeeded);

    private void OnLoadCompleted()
    {
        if (_loadingScreen is null)
        {
            return;
        }

        _loadingScreen.Hide();
    }
}
