using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Scene;
using Godot;
using LanguageExt;
using static Godot.ResourceLoader.ThreadLoadStatus;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;
using Array = Godot.Collections.Array;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Io;

public interface IResourceLoader
{
    Eff<IEnv, Resource> LoadResource(ResourcePath path, IProgressReporter? reporter = null)
    {
        var interval = Schedule.spaced(200.Milliseconds());

        var check =
            from x in IO.lift(() =>
            {
                var arg = new Array();
                var status = ResourceLoader.LoadThreadedGetStatus(path, arg);
                var progress = NormalisedRatio.Coerce(arg[0].AsSingle());

                return (status, progress);
            })
            from _ in reporter?.Report(x.progress) ?? unitIO
            select x.status;

        return
            from env in runtime<IEnv>()
            from request in IO.lift(() => ResourceLoader.LoadThreadedRequest(path))
            from _1 in guard<Error>(
                request == Godot.Error.Ok,
                new SceneLoadingError($"Failed to load resource: {path}, error={request}")
            )
            from status in repeatUntil(
                interval,
                callDeferred(check).RunIO(env),
                x => x != InProgress
            )
            from _2 in guard(status == Loaded,
                status == InvalidResource
                    ? new InvalidSceneError($"Invalid resource: {path}")
                    : new SceneLoadingError($"Failed to load resource: {path}")
            )
            select ResourceLoader.LoadThreadedGet(path);
    }
}