using AlleyCat.Env;
using LanguageExt;
using LanguageExt.Common;
using static LanguageExt.Prelude;

namespace AlleyCat.Service;

public interface IServiceFactory
{
    Type ServiceType { get; }

    Instantiation Instantiation => Instantiation.LazySingleton;

    Eff<IEnv, object> Service { get; }
}

public interface IServiceFactory<TService> : IServiceFactory
{
    Eff<IEnv, TService> TypedService { get; }
}

public static class ServiceFactoryExtensions
{
    internal static Fin<T> RunIfNotReady<T>(this Fin<T> instance, Eff<IEnv, T> service) =>
        instance | @catch(e => e is NotReadyError,
            Optional(GodotEnv.Instance)
                .Match(
                    env => service.Run(env),
                    () => Error.New(
                        $"GodotEnv.Instance is null, cannot create service {typeof(T)}."
                    )
                )
        );
}