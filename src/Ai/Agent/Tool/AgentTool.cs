using System.Text.Json;
using AlleyCat.Actor.Action;
using AlleyCat.Env;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Agent.Tool;

public interface IAgentTool
{
    AiFunctionName? Name { get; }

    Delegate Delegate { get; }

    Option<JsonSerializerOptions> SerialiserOptions => None;

    Eff<IEnv, AIFunction> CreateFunction(AgentContext context)
    {
        var serialiserOptions = SerialiserOptions.ValueUnsafe() ?? new JsonSerializerOptions();

        var inner = AIFunctionFactory.Create(Delegate, Name, serializerOptions: serialiserOptions);
        var func = new ContextProviderFunction(inner, context.Services);

        return SuccessEff<AIFunction>(func);
    }

    private class ContextProviderFunction(
        AIFunction inner,
        IServiceProvider services
    ) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Services = services;

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }

        public override object? GetService(
            Type serviceType,
            object? serviceKey = null
        ) => services.GetKeyedService(serviceType, serviceKey) ?? base.GetService(serviceType, serviceKey);
    }
}