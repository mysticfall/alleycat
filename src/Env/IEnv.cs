using AlleyCat.Async;
using AlleyCat.Scene;
using AlleyCat.Service;
using LanguageExt;
using LanguageExt.Effects;
using Microsoft.Extensions.FileProviders;
using static LanguageExt.Prelude;

namespace AlleyCat.Env;

public interface IEnv : IServiceProvider
{
    IScene Scene { get; }

    IFileProvider FileProvider { get; }

    ITaskQueue TaskQueue { get; }
}

public static class Prelude
{
    // ReSharper disable once InconsistentNaming
    public static Eff<IEnv, T> service<T>() =>
        from env in runtime<IEnv>()
        from service in env.RequireService<T>()
        select service;

    // ReSharper disable once InconsistentNaming
    public static Eff<IEnv, T> callDeferred<T>(Eff<T> task) =>
        from env in runtime<IEnv>()
        from result in env.TaskQueue.Enqueue(task)
        select result;

    // ReSharper disable once InconsistentNaming
    public static Eff<IEnv, T> callDeferred<T>(IO<T> task) =>
        from env in runtime<IEnv>()
        from result in env.TaskQueue.Enqueue(Eff.lift(task))
        select result;

    // ReSharper disable once InconsistentNaming
    public static Eff<TEnv, T> callDeferred<TEnv, T>(Eff<TEnv, T> task) =>
        from env in runtime<TEnv>()
        from result in localEff<MinRT, TEnv, T>(_ => env, task).As()
        select result;
}