using System.Diagnostics;
using System.Text.Json.Serialization;
using AlleyCat.Body.Voice;
using AlleyCat.Character;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Templating;
using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI;

/// <summary>
/// NPC mind that reconstructs every agent turn from its complete subjective timeline.
/// </summary>
[GlobalClass]
public partial class AgenticMind : MindBase, IServiceProvider
{
    internal const string AgentDescription = "Character mind for in-world actions.";

    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private bool _deferredGodotActionFlushQueued;

    /// <summary>
    /// Editor-authored system prompt stack compiled and rendered afresh for every turn.
    /// </summary>
    [ExportGroup("Prompt")]
    [Export]
    public PromptStack? SystemInstruction
    {
        get;
        set;
    }

    /// <summary>
    /// Backend factory used to create a fresh chat client for each turn.
    /// </summary>
    [ExportGroup("Backend")]
    [Export]
    public ClientProvider? ClientProvider { get; set; } = new OpenAIClientProvider();

    /// <summary>
    /// Editor-authored Agent Framework tools resolved for each turn.
    /// </summary>
    [ExportGroup("Tools")]
    [Export]
    public Godot.Collections.Array<AgentTool> Tools { get; set; } = [];

    /// <inheritdoc />
    public override void ReceiveVoice(string speech, IVoice source)
    {
        if (!ShouldHandleVoice(speech, source))
        {
            return;
        }

        string trimmedSpeech = speech.Trim();
        if (AIPipelineDebugLog.IsEnabled)
        {
            AIPipelineDebugLog.Stage("LLM observation received", $"{trimmedSpeech.Length} chars");
        }

        string voiceID = source.Id;
        ISceneContext scene = Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
        _ = Observe(new ObservedSpeech(
            ResolveRecognisedCharacterID(voiceID, scene),
            voiceID,
            trimmedSpeech));
    }

    /// <summary>
    /// Resolves a configured voice ID to a character in the current scene.
    /// </summary>
    protected virtual string? ResolveRecognisedCharacterID(string voiceID, ISceneContext scene)
    {
        ArgumentNullException.ThrowIfNull(voiceID);
        ArgumentNullException.ThrowIfNull(scene);

        if (string.IsNullOrWhiteSpace(voiceID))
        {
            return null;
        }

        ICharacter? owner = null;
        foreach (ICharacter character in scene.Characters)
        {
            if (!character.TryGetVoice(out IVoice? characterVoice)
                || characterVoice is null
                || string.IsNullOrWhiteSpace(characterVoice.Id)
                || !string.Equals(characterVoice.Id, voiceID, StringComparison.Ordinal))
            {
                continue;
            }

            if (owner is not null)
            {
                throw new InvalidOperationException(
                    $"Voice ID '{voiceID}' ambiguously matches current-scene characters '{owner.Id}' and '{character.Id}'.");
            }

            owner = character;
        }

        return owner?.Id;
    }

    /// <inheritdoc />
    protected override async Task ProcessObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        IReadOnlyList<AgentObservation> timelineSnapshot,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
        {
            return;
        }

        try
        {
            await RunAgentTurnAsync(timelineSnapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogOptionalResponseFailure(ex);
        }
    }

    private async Task RunAgentTurnAsync(
        IReadOnlyList<AgentObservation> timeline,
        CancellationToken cancellationToken)
    {
        ClientProvider clientProvider = ClientProvider
            ?? throw new InvalidOperationException("AgenticMind requires a configured ClientProvider.");
        PromptStack systemInstruction = SystemInstruction
            ?? throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");

        ISceneContext scene = Game.Instance.GetRequiredService<ISceneContextProvider>().GetCurrent();
        ICharacter character = ResolveOwningCharacter();
        PromptSectionBuildContext buildContext = new(Game.Instance, scene, character);
        ITemplate template = await systemInstruction.CompileAsync(buildContext, cancellationToken);
        IReadOnlyDictionary<string, object?> renderContext = CreateSystemInstructionContext(character, scene, timeline);
        string instructions = RenderSystemInstruction(template, renderContext);
        (string name, string description) = CreateAgentMetadata(character);

        TurnInvocationServices invocationServices = new(this, character, Voice);
        ChatClientAgentRunOptions options = new(new ChatOptions
        {
            Tools = CreateTurnTools(invocationServices),
        });

        // ChatClientAgent 1.8.0 exposes this typed no-message overload directly. Keeping the
        // concrete type avoids diagnostics wrappers obscuring the package's typed API.
        IChatClient chatClient = AIChatClientDiagnostics.Decorate(
            clientProvider.CreateChatClient(),
            _diagnosticsSettingsLoader(),
            GameLoggerResolver.ResolveFactoryRequired);
        ChatClientAgent agent = chatClient.AsAIAgent(
            instructions: instructions,
            name: name,
            description: description);
        Stopwatch sessionStopwatch = AIPipelineDebugLog.StartTimer();
        AgentSession session = await agent.CreateSessionAsync(cancellationToken);
        AIPipelineDebugLog.Latency("LLM session created in", sessionStopwatch);

        Stopwatch runStopwatch = AIPipelineDebugLog.StartTimer();
        try
        {
            _ = await agent.RunAsync<EndTurnResult>(
                session,
                serializerOptions: null,
                options,
                cancellationToken);
        }
        finally
        {
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("LLM turn returned in", runStopwatch, $"{timeline.Count} observation(s)");
            }
        }
    }

    private List<AITool> CreateTurnTools(IServiceProvider invocationServices)
    {
        List<AITool> tools = new(Tools.Count);
        foreach (AgentTool? tool in Tools)
        {
            if (tool is not null)
            {
                tools.Add(tool.CreateFunction(invocationServices));
            }
        }

        return tools;
    }

    internal static (string Name, string Description) CreateAgentMetadata(ICharacter character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return (character.Id, AgentDescription);
    }

    internal static IReadOnlyDictionary<string, object?> CreateSystemInstructionContext(
        ICharacter character,
        ISceneContext scene,
        IReadOnlyList<AgentObservation>? observations = null)
    {
        ArgumentNullException.ThrowIfNull(character);
        ArgumentNullException.ThrowIfNull(scene);

        ICharacter[] characters = [.. scene.Characters.OrderBy(subject => subject.Id, StringComparer.Ordinal)];
        if (!characters.Any(subject => ReferenceEquals(subject, character)))
        {
            throw new InvalidOperationException(
                $"AgenticMind owning character '{character.Id}' is absent from the current scene context.");
        }

        Dictionary<string, object?> characterContexts = new(StringComparer.Ordinal);
        IReadOnlyDictionary<string, object?>? owningCharacterContext = null;
        foreach (ICharacter subject in characters)
        {
            IReadOnlyDictionary<string, object?> subjectContext = subject.GetContext(scene, observer: character);
            characterContexts.Add(subject.Id, subjectContext);
            if (ReferenceEquals(subject, character))
            {
                owningCharacterContext = subjectContext;
            }
        }

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["character"] = owningCharacterContext!,
            ["characters"] = characterContexts,
            [EventHistoryPromptSection.ObservationsContextKey] = observations ?? [],
        };
    }

    internal static string RenderSystemInstruction(
        ITemplate template,
        IReadOnlyDictionary<string, object?> context)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);
        return template.Render(context);
    }

    private static void LogOptionalResponseFailure(Exception exception)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogError(exception, "AgenticMind response failed.");
        }
    }

    internal void SetDiagnosticsSettingsLoaderForTesting(Func<AIDiagnosticsSettings> diagnosticsSettingsLoader)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsSettingsLoader);
        _diagnosticsSettingsLoader = diagnosticsSettingsLoader;
    }

    /// <inheritdoc />
    protected override void OnNodeLifetimeEnding()
    {
        base.OnNodeLifetimeEnding();
        DeferredGodotAction[] actions;
        lock (_deferredGodotActionsLock)
        {
            actions = [.. _deferredGodotActions];
            _deferredGodotActions.Clear();
            _deferredGodotActionFlushQueued = false;
        }

        foreach (DeferredGodotAction action in actions)
        {
            action.Cancel(NodeLifetimeCancellationToken);
        }
    }

    private Task DispatchDeferredSpeechAsync(
        IVoice voice,
        string speech,
        CancellationToken cancellationToken)
    {
        var dispatchCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            NodeLifetimeCancellationToken);
        DeferredGodotAction action = new(
            voice,
            speech,
            dispatchCancellation);
        lock (_deferredGodotActionsLock)
        {
            if (IsNodeLifetimeEnded)
            {
                action.Cancel(NodeLifetimeCancellationToken);
                return action.Task;
            }

            _deferredGodotActions.Enqueue(action);
            if (!_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }

        return action.Task;
    }

    private void FlushDeferredGodotActions()
    {
        DeferredGodotAction[] actions;
        lock (_deferredGodotActionsLock)
        {
            actions = [.. _deferredGodotActions];
            _deferredGodotActions.Clear();
            _deferredGodotActionFlushQueued = false;
        }

        foreach (DeferredGodotAction action in actions)
        {
            if (IsNodeLifetimeEnded)
            {
                action.Cancel(NodeLifetimeCancellationToken);
            }
            else
            {
                action.Invoke();
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return IsNodeLifetimeEnded
            ? null
            : serviceType == typeof(ICharacter)
                ? ResolveOwningCharacter()
                : new TurnInvocationServices(this, character: null, Voice).GetService(serviceType);
    }

    private sealed class TurnInvocationServices(AgenticMind mind, ICharacter? character, IVoice? voice) : IServiceProvider
    {
        private readonly DeferredVoice? _voice = voice is null ? null : new DeferredVoice(mind, voice);

        public object? GetService(Type serviceType)
            => serviceType == typeof(IVoice) ? _voice
                : serviceType == typeof(ICharacter) ? character ?? mind.ResolveOwningCharacter()
                : serviceType.IsInstanceOfType(mind) ? mind
                : null;
    }

    private sealed class DeferredVoice(AgenticMind mind, IVoice voice) : IVoice
    {
        public string Id => voice.Id;

        public Vector3 Origin => voice.Origin;

        public void Speak(string speech) => _ = ObserveCompatibilitySubmissionAsync(SpeakAsync(speech));

        public ValueTask SpeakAsync(
            string speech,
            CancellationToken cancellationToken = default)
            => new(mind.DispatchDeferredSpeechAsync(voice, speech, cancellationToken));

        private static async Task ObserveCompatibilitySubmissionAsync(ValueTask submission)
        {
            try
            {
                await submission;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogOptionalResponseFailure(ex);
            }
        }
    }

    private sealed class DeferredGodotAction
    {
        private readonly IVoice _voice;
        private readonly string _speech;
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completionSource = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _settled;

        public DeferredGodotAction(
            IVoice voice,
            string speech,
            CancellationTokenSource cancellation)
        {
            _voice = voice;
            _speech = speech;
            _cancellation = cancellation;
            _cancellationRegistration = cancellation.Token.Register(
                () => _completionSource.TrySetCanceled(cancellation.Token));
        }

        public Task Task => _completionSource.Task;

        public void Cancel(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _settled) != 0)
            {
                return;
            }

            try
            {
                if (!_cancellation.IsCancellationRequested)
                {
                    _cancellation.Cancel();
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = _completionSource.TrySetCanceled(cancellationToken);
            Cleanup();
        }

        public void Invoke()
        {
            if (_completionSource.Task.IsCompleted || _cancellation.IsCancellationRequested)
            {
                Cleanup();
                return;
            }

            try
            {
                ValueTask invocation = _voice.SpeakAsync(
                    _speech,
                    _cancellation.Token);
                if (invocation.IsCompletedSuccessfully)
                {
                    _ = _completionSource.TrySetResult();
                    Cleanup();
                    return;
                }

                _ = CompleteAsync(invocation);
            }
            catch (Exception ex)
            {
                _ = _completionSource.TrySetException(ex);
                Cleanup();
            }
        }

        private async Task CompleteAsync(ValueTask invocation)
        {
            try
            {
                await invocation;
                _ = _completionSource.TrySetResult();
            }
            catch (OperationCanceledException ex)
            {
                _ = _completionSource.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _ = _completionSource.TrySetException(ex);
            }
            finally
            {
                Cleanup();
            }
        }

        private void Cleanup()
        {
            if (Interlocked.Exchange(ref _settled, 1) != 0)
            {
                return;
            }

            _ = _cancellationRegistration.Unregister();
            _cancellation.Dispose();
        }
    }
}

/// <summary>
/// Closed, property-free result that marks successful completion of an agent turn.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EndTurnResult;
