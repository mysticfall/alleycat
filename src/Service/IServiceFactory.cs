using AlleyCat.Common;
using AlleyCat.Env;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;
using Error = LanguageExt.Common.Error;

namespace AlleyCat.Service;

public interface IServiceFactory
{
    Type ServiceType { get; }

    InstantiationOption Instantiation => InstantiationOption.LazySingleton;

    RunOption AutoRun => RunOption.OnCreation;

    Eff<IEnv, object> Service { get; }
}

public interface IServiceFactory<TService> : IServiceFactory
{
    Eff<IEnv, TService> TypedService { get; }
}

public static class ServiceFactoryExtensions
{
    extension(Node node)
    {
        public Eff<IEnv, Option<T>> FindService<T>() =>
            node
                .GetDescendants()
                .OfType<IServiceFactory<T>>()
                .AsIterable()
                .Head
                .Traverse(x => x.TypedService)
                .As();

        public Eff<IEnv, T> GetService<T>() =>
            node
                .FindService<T>()
                .Bind(x => x.ToEff(
                    Error.New(
                        $"No service of type {typeof(T).FullName} found under node {node.GetPath()}."
                    )
                ));
    }

    internal static Fin<T> RunIfNotReady<T>(this Fin<T> instance, Eff<IEnv, T> service) =>
        instance | @catch(e => e is NotReadyError,
            Optional(GodotEnv.Instance).Match<Fin<T>>(
                env => service.Run(env),
                () => Error.New(
                    $"GodotEnv.Instance is null, cannot create service {typeof(T)}."
                )
            )
        );
}