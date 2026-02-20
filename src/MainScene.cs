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

public class MainScene : IRunnable, IResourceLoader
{
    public Eff<IEnv, IDisposable> Run { get; }

    public MainScene(
        ResourcePath startScene,
        Node sceneRoot,
        ILoadingScreen loadingScreen,
        IController<IActor> actorController,
        XRInterface xr,
        ILoggerFactory? loggerFactory = null
    )
    {
        var logger = loggerFactory.GetLogger<MainScene>();

        var waitForReset = liftIO(async () =>
        {
            logger.LogDebug("Waiting for the headset position to reset.");

            await xr.ToSignal(xr, OpenXRInterface.SignalName.PoseRecentered);
        });

        var option = new SceneLoadingOptions(loadingScreen, waitForReset);

        var process =
            from env in runtime<IEnv>()
            from _1 in env.Scene.LoadScene(startScene, sceneRoot, option)
            from actor in env.Scene.Player
            from _2 in actorController.Control(actor)
            select unit;

        Run = process
            .IfFail(e =>
            {
                logger.LogCritical(e, "Failed to start the main scene.");

                return unit;
            })
            .ForkIO()
            .As()
            .Map(x => x.AsDisposable());
    }
}