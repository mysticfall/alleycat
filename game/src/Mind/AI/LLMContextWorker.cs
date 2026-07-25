using AlleyCat.Character;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Scene;
using AlleyCat.Templating;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI;

/// <summary>Generic schema-only contextual worker with a lifetime-cached compiled template.</summary>
public abstract partial class LLMContextWorker<TResponse> : ContextWorker
{
    private PromptStack? _capturedPromptStack;
    private Task<ITemplate>? _templateTask;
    private int _compilationFailureLogged;

    /// <summary>Prompt stack captured when this worker attaches to its Mind.</summary>
    [ExportGroup("Prompt")]
    [Export]
    public PromptStack? PromptStack
    {
        get; set;
    }

    /// <summary>Provider used for schema-only worker requests.</summary>
    [ExportGroup("Backend")]
    [Export]
    public ClientProvider? ClientProvider
    {
        get; set;
    }

    /// <summary>Maps and validates a typed schema result into a context dictionary.</summary>
    protected abstract bool TryMapResponse(
        TResponse response,
        out IReadOnlyDictionary<string, object?> context);

    /// <inheritdoc />
    protected override void OnAttached(AgenticMind mind)
    {
        _capturedPromptStack = PromptStack
            ?? throw new InvalidOperationException("LLMContextWorker requires a worker-owned PromptStack.");
        ISceneContext scene = Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
        ICharacter character = mind.OwningCharacter;
        PromptSectionBuildContext buildContext = new(Game.Instance, scene, character);
        _templateTask = CompileOnceAsync(_capturedPromptStack, buildContext, LifetimeCancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyDictionary<string, object?>> RunAsync(
        IReadOnlyDictionary<string, object?> context,
        CancellationToken cancellationToken)
    {
        Task<ITemplate> templateTask = _templateTask
            ?? throw new InvalidOperationException("LLMContextWorker has not been attached to an AgenticMind.");
        ITemplate template;
        try
        {
            template = await templateTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (IsUnavailable)
        {
            throw new ContextWorkerUnavailableException();
        }
        ClientProvider provider = ClientProvider
            ?? throw new InvalidOperationException("LLMContextWorker requires a configured ClientProvider.");
        string instructions = AgenticMind.RenderSystemInstruction(template, context);
        IChatClient client = AIChatClientDiagnostics.Decorate(
            provider.CreateChatClient(),
            AIDiagnosticsSettings.LoadOrDefault(),
            GameLoggerResolver.ResolveFactoryRequired);
        ChatOptions options = new()
        {
            Instructions = instructions,
            Tools = [],
            ToolMode = null,
        };
        ChatResponse<TResponse> response = await client.GetResponseAsync<TResponse>(
            provider.CreateRunMessages(), options, useJsonSchemaResponseFormat: true, cancellationToken);
        return TryMapResponse(response.Result, out IReadOnlyDictionary<string, object?>? mappedContext)
            ? mappedContext
            : throw new InvalidOperationException("LLMContextWorker rejected the structured response.");
    }

    private async Task<ITemplate> CompileOnceAsync(
        PromptStack stack,
        PromptSectionBuildContext buildContext,
        CancellationToken lifetimeCancellationToken)
    {
        try
        {
            return await stack.CompileAsync(buildContext, lifetimeCancellationToken);
        }
        catch (OperationCanceledException) when (lifetimeCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkUnavailable();
            if (Interlocked.Exchange(ref _compilationFailureLogged, 1) == 0
                && GameLoggerResolver.TryResolve(out ILogger<LLMContextWorker<TResponse>>? logger)
                && logger is not null)
            {
                logger.LogError(ex, "Context worker {WorkerName} prompt compilation failed; worker remains inactive.", Name);
            }

            throw;
        }
    }
}
