using Microsoft.Extensions.AI;

namespace AlleyCat.AI.Tool;

/// <summary>
/// Creates Agent Framework functions that receive the Mind's invocation services at tool-call time.
/// </summary>
public static class AgentTool
{
    /// <summary>
    /// Creates an AI function whose invocation arguments resolve services from the supplied service provider.
    /// </summary>
    public static AIFunction Create(
        Delegate method,
        IServiceProvider services,
        string? name = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(services);

        AIFunction inner = AIFunctionFactory.Create(method, name, description);
        return new ServiceProviderFunction(inner, services);
    }

    private sealed class ServiceProviderFunction(AIFunction inner, IServiceProvider services) : DelegatingAIFunction(inner)
    {
        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Services = services;

            return base.InvokeCoreAsync(arguments, cancellationToken);
        }

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => services.GetService(serviceType) ?? base.GetService(serviceType, serviceKey);
    }
}
