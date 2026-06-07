using System.Diagnostics;
using AlleyCat.AI.Prompting;
using AlleyCat.AI.Provider;
using AlleyCat.AI.Tool;
using AlleyCat.Body.Voice;
using AlleyCat.Diagnostics;
using AlleyCat.Templating;
using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentObservation = AlleyCat.AI.Observation.Observation;
using SpeechObservation = AlleyCat.AI.Observation.SpeechObservation;

namespace AlleyCat.AI;

/// <summary>
/// Speech-driven NPC mind that batches observations and delegates responses to an LLM backend.
/// </summary>
[GlobalClass]
public partial class AgenticMind : Mind, IServiceProvider
{
    private readonly Lock _responseStateLock = new();
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private ResponseTurn? _activeTurn;
    private ClientProvider? _clientProviderForAgent;
    private PromptStack? _systemInstructionForAgent;
    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private bool _deferredGodotActionFlushQueued;
    private bool _isResponding;

    /// <summary>
    /// Minimum time to keep player listening paused after Alley starts speaking.
    /// </summary>
    [ExportGroup("Response")]
    [Export(PropertyHint.Range, "0,10,0.1")]
    public float PostReplyListenCooldownSeconds { get; set; } = 1f;

    /// <summary>
    /// Editor-authored system prompt stack compiled into the Agent Framework instructions for this mind.
    /// </summary>
    [ExportGroup("Prompt")]
    [Export]
    public PromptStack? SystemInstruction
    {
        get; set;
    }

    /// <summary>
    /// Backend factory used to create the chat client for Agent Framework turns.
    /// </summary>
    [ExportGroup("Backend")]
    [Export]
    public ClientProvider? ClientProvider { get; set; } = new OpenAIClientProvider();

    /// <summary>
    /// Editor-authored Agent Framework tools selected for each agent turn.
    /// </summary>
    [ExportGroup("Tools")]
    [Export]
    public Godot.Collections.Array<AgentTool> Tools { get; set; } = [];

    /// <inheritdoc />
    public override void ReceiveVoice(string speech, IVoice source)
    {
        if (!ShouldHandleVoice(speech, source) || IsResponding())
        {
            return;
        }

        string trimmedSpeech = speech.Trim();
        AIPipelineDebugLog.Stage("LLM observation received", $"{trimmedSpeech.Length} chars");
        _ = Observe(new SpeechObservation(source.Id, trimmedSpeech));
    }

    private bool IsResponding()
    {
        lock (_responseStateLock)
        {
            return _isResponding;
        }
    }

    private ResponseTurn? TryBeginResponse()
    {
        lock (_responseStateLock)
        {
            if (_isResponding)
            {
                return null;
            }

            _isResponding = true;
            _activeTurn = new ResponseTurn();
            return _activeTurn;
        }
    }

    private void EndResponse(ResponseTurn turn)
    {
        lock (_responseStateLock)
        {
            if (ReferenceEquals(_activeTurn, turn))
            {
                _activeTurn = null;
            }

            _isResponding = false;
        }
    }

    private async Task RespondAsync(IReadOnlyList<AgentObservation> observations, ResponseTurn turn)
    {
        try
        {
            if (Voice is null)
            {
                GD.PushError("AgenticMind requires a configured NPC Voice.");
                return;
            }

            if (ClientProvider is null)
            {
                GD.PushError("AgenticMind requires a configured ClientProvider.");
                return;
            }

            if (SystemInstruction is null)
            {
                GD.PushError("AgenticMind requires a configured SystemInstruction prompt stack.");
                return;
            }

            await RunAgentTurnAsync(observations, turn.CancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (turn.HasSpoken)
        {
            // The turn is cancelled after the first speak tool call so the prototype cannot keep talking to itself.
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
        }
        finally
        {
            await WaitForReplyCooldownAsync(turn);
            EndResponse(turn);
        }
    }

    private bool TrySpeakFromTool(string speech)
    {
        ResponseTurn? turn = GetActiveTurn();
        if (turn is null)
        {
            return false;
        }

        if (!turn.TrySetSpokenSpeech(speech))
        {
            return false;
        }

        AIPipelineDebugLog.Latency("LLM first speak tool call after", turn.Stopwatch, $"{turn.SpokenSpeech.Length} chars");
        _ = SpeakAsync(speech, CancellationToken.None).ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    GD.PushError(task.Exception?.GetBaseException().ToString());
                    return;
                }

                AIPipelineDebugLog.Latency("LLM speech dispatched after", turn.Stopwatch);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        turn.CancellationTokenSource.Cancel();

        return true;
    }

    private ResponseTurn? GetActiveTurn()
    {
        lock (_responseStateLock)
        {
            return _activeTurn;
        }
    }

    private Task SpeakAsync(string speech, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(speech))
        {
            return Task.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DispatchDeferredGodotActionAsync(() => Voice?.Speak(speech.Trim()));
    }

    private async Task WaitForReplyCooldownAsync(ResponseTurn turn)
    {
        if (!turn.HasSpoken)
        {
            return;
        }

        double cooldownSeconds = Math.Max(PostReplyListenCooldownSeconds, EstimateSpeechDurationSeconds(turn.SpokenSpeech));
        if (cooldownSeconds <= 0d)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(cooldownSeconds));
    }

    private static double EstimateSpeechDurationSeconds(string speech)
    {
        int characterCount = speech.Count(character => !char.IsWhiteSpace(character));
        return characterCount / 12d;
    }

    private AgentDefinition CreateAgentDefinition()
    {
        PromptStack systemInstruction = SystemInstruction
            ?? throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");

        string instructions = systemInstruction
            .Compile(new HandlebarsTemplateCompiler())
            .Render(new Dictionary<string, object?>());

        return new AgentDefinition(
            instructions,
            "Alley",
            "Prototype NPC mind for in-world speech responses.");
    }

    private List<AITool> CreateTurnTools()
    {
        List<AITool> tools = new(Tools.Count);

        foreach (AgentTool? tool in Tools)
        {
            if (tool is null)
            {
                continue;
            }

            tools.Add(tool.CreateFunction(this));
        }

        return tools;
    }

    private async Task RunAgentTurnAsync(IReadOnlyList<AgentObservation> observations, CancellationToken cancellationToken)
    {
        ChatClientAgent agent = EnsureAgent();
        if (_session is null)
        {
            Stopwatch sessionStopwatch = AIPipelineDebugLog.StartTimer();
            _session = await agent.CreateSessionAsync();
            AIPipelineDebugLog.Latency("LLM session created in", sessionStopwatch);
        }

        Stopwatch runStopwatch = AIPipelineDebugLog.StartTimer();
        try
        {
            ChatClientAgentRunOptions options = new(new ChatOptions
            {
                Tools = CreateTurnTools(),
            });

            _ = await agent.RunAsync(RenderObservationSummary(observations), _session, options, cancellationToken);
        }
        finally
        {
            AIPipelineDebugLog.Latency("LLM turn returned in", runStopwatch, $"{observations.Count} observation(s)");
        }
    }

    private ChatClientAgent EnsureAgent()
    {
        if (_agent is not null
            && ReferenceEquals(_clientProviderForAgent, ClientProvider)
            && ReferenceEquals(_systemInstructionForAgent, SystemInstruction))
        {
            return _agent;
        }

        if (ClientProvider is null)
        {
            throw new InvalidOperationException("AgenticMind requires a configured ClientProvider.");
        }

        if (SystemInstruction is null)
        {
            throw new InvalidOperationException("AgenticMind requires a configured SystemInstruction prompt stack.");
        }

        AgentDefinition definition = CreateAgentDefinition();
        _session = null;
        _clientProviderForAgent = ClientProvider;
        _systemInstructionForAgent = SystemInstruction;
        _agent = ClientProvider.CreateChatClient().AsAIAgent(
            instructions: definition.Instructions,
            name: definition.Name,
            description: definition.Description);

        return _agent;
    }

    private static string RenderObservationSummary(IReadOnlyList<AgentObservation> observations)
    {
        if (observations.Count == 0)
        {
            return "No new observations.";
        }

        List<string> lines = new(observations.Count + 1)
        {
            "New observations since the last turn:",
        };

        foreach (AgentObservation observation in observations)
        {
            lines.Add($"- {observation.ToPromptString()}");
        }

        return string.Join(System.Environment.NewLine, lines);
    }

    /// <inheritdoc />
    protected override async Task ProcessObservationsAsync(
        IReadOnlyList<AgentObservation> observations,
        CancellationToken cancellationToken)
    {
        if (observations.Count == 0)
        {
            return;
        }

        ResponseTurn? turn = TryBeginResponse();
        if (turn is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await RespondAsync(observations, turn);
    }

    private Task DispatchDeferredGodotActionAsync(Action action)
    {
        TaskCompletionSource completionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_deferredGodotActionsLock)
        {
            _deferredGodotActions.Enqueue(new DeferredGodotAction(action, completionSource));

            if (!_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }

        return completionSource.Task;
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
            try
            {
                action.Action();
                _ = action.CompletionSource.TrySetResult();
            }
            catch (Exception ex)
            {
                _ = action.CompletionSource.TrySetException(ex);
            }
        }

        lock (_deferredGodotActionsLock)
        {
            if (_deferredGodotActions.Count > 0 && !_deferredGodotActionFlushQueued)
            {
                _deferredGodotActionFlushQueued = true;
                _ = CallDeferred(nameof(FlushDeferredGodotActions));
            }
        }
    }

    private sealed record AgentDefinition(
        string Instructions,
        string Name,
        string Description);

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }

    private sealed class ResponseTurn
    {
        private readonly Lock _lock = new();

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public Stopwatch Stopwatch { get; } = AIPipelineDebugLog.StartTimer();

        public bool HasSpoken
        {
            get; private set;
        }

        public string SpokenSpeech { get; private set; } = string.Empty;

        public bool TrySetSpokenSpeech(string speech)
        {
            lock (_lock)
            {
                if (HasSpoken)
                {
                    return false;
                }

                SpokenSpeech = speech.Trim();
                HasSpoken = true;
                return true;
            }
        }
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this)
            ? this
            : serviceType == typeof(IVoice) ? new ToolVoice(this) : null;
    }

    private sealed class ToolVoice(AgenticMind mind) : IVoice
    {
        public string Id => mind.Voice?.Id ?? mind.Name;

        public Vector3 Origin => mind.Voice?.Origin ?? Vector3.Zero;

        public void Speak(string speech) => _ = mind.TrySpeakFromTool(speech);
    }
}
