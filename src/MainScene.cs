using System.Reactive.Disposables;
using AlleyCat.Actor;
using AlleyCat.Common;
using AlleyCat.Control;
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
    IController<IActor> actorController,
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
            from _1 in env.Scene.LoadScene(startScene, sceneRoot, option)
            from actor in env.Scene.Player
            from _2 in actorController.Control(actor)
            select unit;

        return process
            .IfFail(e =>
            {
                _logger.LogCritical(e,"Failed to start the main scene.");

                return unit;
            })
            .ForkIO()
            .IgnoreF()
            .As()
            .Map(IDisposable (_) => new CompositeDisposable());
    }
}