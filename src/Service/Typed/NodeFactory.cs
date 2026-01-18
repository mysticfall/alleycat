using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Env.Godot;
using AlleyCat.Logging;
using Godot;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static AlleyCat.Env.Prelude;
using static LanguageExt.Prelude;

namespace AlleyCat.Service.Typed;

public abstract partial class NodeFactory<TService> : NodeFactory, IServiceFactory<TService>
{
    public Eff<IEnv, TService> TypedService
    {
        get
        {
            var instantiation = ((IServiceFactory)this).Instantiation;

            return instantiation switch
            {
                Instantiation.Singleton => _instance,
                Instantiation.LazySingleton => _instance.RunIfNotReady(
                    from instance in CreateService()
                    from _1 in liftEff(() => { _instance = instance; })
                    select instance
                ),
                Instantiation.Factory => CreateService(),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
    }

    // ReSharper disable once RedundantSuppressNullableWarningExpression
    public override Eff<IEnv, object> Service => TypedService.Map(object (x) => x!);

    public override Type ServiceType => typeof(TService);

    private Fin<TService> _instance = new NotReadyError(
        $"The service({typeof(TService).FullName}) hasn't initialised yet."
    );

    private readonly List<IDisposable> _disposables = [];

    private Eff<IEnv, TService> CreateService() =>
        from loggerFactory in service<ILoggerFactory>()
        from instance in
            from service in CreateService(loggerFactory)
                .MapFail(e => new ServiceCreationError(
                    $"Failed to initialise service: {GetPath()}",
                    e
                ))
            from cleanup in service is IRunnable i
                ? Some(i.Run()).Traverse(identity).As()
                : SuccessEff<Option<IDisposable>>(None)
            from _ in liftEff(() => { cleanup.Iter(_disposables.Add); })
            select service
        from _1 in liftEff(() =>
        {
            var logger = loggerFactory.GetLogger(GetType());

            logger.LogDebug("Service initialised successfully.");
        })
        select instance;

    protected abstract Eff<IEnv, TService> CreateService(ILoggerFactory loggerFactory);

    public override void _Ready()
    {
        base._Ready();

        if (((IServiceFactory)this).Instantiation != Instantiation.Singleton)
        {
            return;
        }

        _instance = _instance.RunIfNotReady(
            from instance in CreateService()
            from _1 in liftEff(() => { _instance = instance; })
            select instance
        );

        _instance.IfFail(e =>
        {
            var logger =
                from env in Optional(GodotEnv.Instance)
                from loggerFactory in env.FindService<ILoggerFactory>()
                select loggerFactory.GetLogger(GetType());

            logger.Match(log =>
            {
                if (log.IsEnabled(LogLevel.Error))
                {
                    log.LogError(e.ToException(), "Service failed to initialise.");
                }
            }, () =>
            {
                GD.PushWarning("Logging service is unavailable.");
                GD.PushError("[", GetType().Name, "] Service failed to initialise: ", e.ToException());
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _disposables.Clear();

        _instance = new AlreadyDisposedError(
            $"The service({typeof(TService).FullName}) was disposed."
        );
    }
}