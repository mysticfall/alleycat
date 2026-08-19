using AlleyCat.Core.Threading;
using AlleyCat.Core.Time;
using AlleyCat.Mind.AI.Prompting;
using Godot;
using Microsoft.Extensions.AI;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Session-scoped services bound to an <see cref="AgentTool"/> when it is attached to an agent session.
/// </summary>
/// <param name="Context">Trusted session binding captured once at session start.</param>
/// <param name="Mind">Mind boundary owning the session's timeline, waits, and attended-speaker cues.</param>
/// <param name="HistoryRenderer">
/// Event-history renderer for on-demand observation rendering under the AI-003 contract, or null when unavailable.
/// </param>
/// <param name="Clock">Game clock backing every time-sensitive tool result, or null when unavailable.</param>
internal sealed record AgentToolSession(
    ScenarioContext Context,
    MindBase Mind,
    ObservationHistoryRenderer? HistoryRenderer,
    IGameClock? Clock);

/// <summary>
/// Godot-authored action tool that creates AI functions for an AgenticMind session.
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
    /// Session binding available to derived tools between
    /// <see cref="CreateFunction(ScenarioContext, MindBase, IMainThreadDispatcher)" /> and invocation.
    /// </summary>
    /// <remarks>
    /// Mind and the dispatcher remain private to the common wrapper: this binding is internal to the game assembly
    /// and its friend test assemblies, never model-visible, and never part of <see cref="ScenarioContext"/>.
    /// </remarks>
    internal AgentToolSession? Session
    {
        get;
        private set;
    }

    /// <summary>
    /// Creates an AI function bound to the trusted session context and owning runtime boundary.
    /// </summary>
    public AIFunction CreateFunction(
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mind);
        ArgumentNullException.ThrowIfNull(dispatcher);

        return CreateFunction(context, mind, dispatcher, sessionServices: null);
    }

    /// <summary>
    /// Creates an AI function bound to the trusted session context, owning runtime boundary, and session services.
    /// </summary>
    internal AIFunction CreateFunction(
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher,
        AgentToolSession? sessionServices)
    {
        Delegate method;
        Session = sessionServices ?? new AgentToolSession(context, mind, HistoryRenderer: null, Clock: null);
        try
        {
            method = CreateDelegate();
            ArgumentNullException.ThrowIfNull(method);
        }
        catch
        {
            Session = null;
            throw;
        }

        string? name = string.IsNullOrWhiteSpace(ToolName) ? null : ToolName.Trim();
        string? description = string.IsNullOrWhiteSpace(ToolDescription) ? null : ToolDescription.Trim();
        return CreateFunction(method, context, mind, dispatcher, name, description);
    }

    /// <summary>
    /// Creates an AI function for non-Resource tests and helpers using the same trusted binding.
    /// </summary>
    public static AIFunction CreateFunction(
        Delegate method,
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher,
        string? name = null,
        string? description = null)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mind);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ValidateDelegateResultType(method);

        AIFunction inner = AIFunctionFactory.Create(method, new AIFunctionFactoryOptions
        {
            Name = name,
            Description = description,
            ExcludeResultSchema = true,
            MarshalResult = static (result, _, _) => ValueTask.FromResult(result),
            ConfigureParameterBinding = parameter => parameter.ParameterType == typeof(ScenarioContext)
                ? new AIFunctionFactoryOptions.ParameterBindingOptions
                {
                    BindParameter = (_, _) => context,
                    ExcludeFromSchema = true,
                }
                : default,
        });
        return new RuntimeBoundFunction(inner, context, mind, dispatcher);
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

    private sealed class RuntimeBoundFunction(
        AIFunction inner,
        ScenarioContext context,
        MindBase mind,
        IMainThreadDispatcher dispatcher) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!ReferenceEquals(context.Character, mind.OwningCharacter))
            {
                throw new InvalidOperationException(
                    $"Agent tool '{Name}' context character does not own its Mind.");
            }

            object? rawResult = null;
            await dispatcher.InvokeAsync(
                async invocationCancellationToken => rawResult = await base.InvokeCoreAsync(arguments, invocationCancellationToken),
                cancellationToken);
            if (rawResult is not AgentToolResult result)
            {
                throw new InvalidOperationException(
                    $"Agent tool '{Name}' returned an invalid result shape. Expected {nameof(AgentToolResult)}.");
            }

            mind.IngestToolObservations(result.Observations);
            return result.Message;
        }
    }
}
