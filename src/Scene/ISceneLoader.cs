using AlleyCat.Env;
using AlleyCat.Io;
using AlleyCat.Ui;
using Godot;
using LanguageExt;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Scene;

public readonly record struct SceneLoadingOptions(
    ILoadingScreen? LoadingScreen = null,
    IO<Unit>? BeforeLoad = null,
    Func<Node, Node>? Prepare = null
);

public interface ISceneLoader : IResourceLoader
{
    Eff<IEnv, Unit> LoadScene(
        ResourcePath path,
        Node sceneRoot,
        SceneLoadingOptions? options = null
    ) =>
        from _1 in Optional(options?.LoadingScreen).Match(
            x => callDeferred(x.SetVisible(true)),
            () => unitIO
        )
        from resource in LoadResource(path, options?.LoadingScreen)
        from _2 in guard(
            resource is PackedScene,
            Error.New($"{path} is not a PackedScene ({resource.GetType().FullName})")
        )
        let scene = (PackedScene)resource
        from _3 in options?.BeforeLoad ?? unitIO
        from _4 in callDeferred(liftEff(() =>
        {
            var content = scene.Instantiate();
            var preparedContent = options?.Prepare?.Invoke(content) ?? content;

            sceneRoot.AddChild(preparedContent);
        }))
        from _5 in Optional(options?.LoadingScreen).Match(
            x => callDeferred(x.SetVisible(false)),
            () => unitIO
        )
        select unit;
}