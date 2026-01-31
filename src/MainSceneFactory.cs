using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Io;
using AlleyCat.Service;
using AlleyCat.Service.Typed;
using AlleyCat.Ui;
using AlleyCat.Xr;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat;

[GlobalClass]
public partial class MainSceneFactory : NodeFactory<MainScene>, IServiceFactory
{
    [Export(PropertyHint.FilePath)] public string? StartScene { get; set; }

    [Export] public Node? SceneRoot { get; set; }

    Instantiation IServiceFactory.Instantiation => Instantiation.Singleton;

    protected override Eff<IEnv, MainScene> CreateService(ILoggerFactory loggerFactory) =>
        from startScene in ResourcePath.Create(StartScene).ToEff(identity)
        from sceneRoot in SceneRoot.Require("Scene root is not set.")
        from loadingScreen in service<ILoadingScreen>()
        from xr in service<XrDevices>()
        select new MainScene(
            startScene,
            sceneRoot,
            loadingScreen,
            xr.Interface,
            loggerFactory
        );
}