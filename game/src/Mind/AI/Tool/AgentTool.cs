using AlleyCat.Character;
using Godot;
using Microsoft.Extensions.AI;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Godot-authored action resource that creates AI functions for an AgenticMind turn.
/// </summary>
[Tool]
[GlobalClass]
public abstract partial class AgentTool : Resource
{
    /// <summary>
    /// Function name exposed to the model.
    /// </summary>
    [Export]
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Function description exposed to the model.
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
        ValidateDelegateResultType(method);

        AIFunction inner = AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = static (result, _, _) => ValueTask.FromResult(result),
        });
        return new ServiceProviderFunction(inner, services);
    }

    /// <summary>
    /// Creates the delegate used by the action function.
    /// </summary>
    protected abstract Delegate CreateDelegate();

    private static void ValidateDelegateResultType(Delegate method)
    {
        Type returnType = method.Method.ReturnType;
        if (returnType != typeof(Task<AgentToolResult>)
            && returnType != typeof(ValueTask<AgentToolResult>))
        {
            throw new ArgumentException(
                $"Agent tool delegate '{method.Method.Name}' must return Task<AgentToolResult> or ValueTask<AgentToolResult>, but returns '{returnType.FullName}'.",
                nameof(method));
        }
    }

    private sealed class ServiceProviderFunction(AIFunction inner, IServiceProvider services) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            arguments.Context ??= new Dictionary<object, object?>();
            arguments.Services = services;

            object? rawResult = await base.InvokeCoreAsync(arguments, cancellationToken);
            if (rawResult is not AgentToolResult result)
            {
                throw new InvalidOperationException(
                    $"Agent tool '{Name}' returned an invalid result shape. Expected {nameof(AgentToolResult)}.");
            }

            if (services.GetService(typeof(MindBase)) is not MindBase mind)
            {
                throw new InvalidOperationException(
                    $"Agent tool '{Name}' requires an owning {nameof(MindBase)} invocation service.");
            }

            if (services.GetService(typeof(ICharacter)) is not ICharacter character
                || !ReferenceEquals(character, mind.OwningCharacter))
            {
                throw new InvalidOperationException(
                    $"Agent tool '{Name}' requires the owning character associated with its Mind invocation service.");
            }

            mind.IngestToolObservations(result.Observations);
            return result.Message;
        }

        public override object? GetService(Type serviceType, object? serviceKey = null)
            => services.GetService(serviceType) ?? base.GetService(serviceType, serviceKey);
    }
}
