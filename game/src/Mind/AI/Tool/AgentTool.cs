using Godot;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Godot-authored AI tool resource that creates Agent Framework functions for an AgenticMind turn.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class AgentTool : Resource
{
    /// <summary>
    /// Agent Framework function name exposed to the model.
    /// </summary>
    [Export]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Agent Framework function description exposed to the model.
    /// </summary>
    [Export(PropertyHint.MultilineText)]
    public string ToolDescription { get; set; } = string.Empty;

    /// <summary>
    /// Creates an AI function whose invocation arguments resolve services from the supplied turn context.
    /// </summary>
    public AIFunction CreateFunction(IServiceProvider services)
    {
        Delegate method = CreateDelegate();
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(services);

        string? name = string.IsNullOrWhiteSpace(ToolName) ? null : ToolName.Trim();
        string? description = string.IsNullOrWhiteSpace(ToolDescription) ? null : ToolDescription.Trim();
        return CreateFunction(method, services, name, description);
    }

    /// <summary>
    /// Creates an AI function for non-Resource tests and helpers using the same invocation-service wiring.
    /// </summary>
    public static AIFunction CreateFunction(
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

    /// <summary>
    /// Creates the delegate used by Agent Framework for this tool.
    /// </summary>
    protected abstract Delegate CreateDelegate();

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
