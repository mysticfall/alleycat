using System.Reactive.Disposables;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Io;
using AlleyCat.Logging;
using AlleyCat.Scene;
using AlleyCat.Ui;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat;

public class MainScene(
    ResourcePath startScene,
    Node sceneRoot,
    ILoadingScreen loadingScreen,
    XRInterface xr,
    ILoggerFactory? loggerFactory = null
) : IRunnable, IResourceLoader
{
    private readonly ILogger _logger = loggerFactory.GetLogger<MainScene>();

    public Eff<IEnv, IDisposable> Run()
    {
        var waitForReset = liftIO(async () =>
        {
            _logger.LogDebug("Waiting for the headset position to reset.");

            await xr.ToSignal(xr, OpenXRInterface.SignalName.PoseRecentered);
        });

        var option = new SceneLoadingOptions(loadingScreen, waitForReset);

        var process =
            from env in runtime<IEnv>()
            from _ in env.Scene.LoadScene(startScene, sceneRoot, option)
            select unit;

        return process
            .ForkIO()
            .IgnoreF()
            .As()
            .Map(IDisposable (_) => new CompositeDisposable());
    }
}