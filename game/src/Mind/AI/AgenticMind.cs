using System.Diagnostics;
using AlleyCat.Body.Voice;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Templating;
using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentObservation = AlleyCat.Mind.Observation.Observation;
using MindBase = AlleyCat.Mind.Mind;
using SpeechObservation = AlleyCat.Mind.Observation.SpeechObservation;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Speech-driven NPC mind that batches observations and delegates responses to an LLM backend.
/// </summary>
[GlobalClass]
public partial class AgenticMind : MindBase, IServiceProvider
{
    private readonly Lock _responseStateLock = new();
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private ResponseTurn? _activeTurn;
    private ClientProvider? _clientProviderForAgent;
    private PromptStack? _systemInstructionForAgent;
    private bool _enableRequestResponseDiagnosticsForAgent;
    private bool _agentRequestResponseDiagnosticsEnabled;
    private Func<AIDiagnosticsSettings> _diagnosticsSettingsLoader = AIDiagnosticsSettings.LoadOrDefault;
    private AIAgent? _agent;
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
        if (AIPipelineDebugLog.IsEnabled)
        {
            AIPipelineDebugLog.Stage("LLM observation received", $"{trimmedSpeech.Length} chars");
        }

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
            AIDiagnosticsSettings diagnosticsSettings = _diagnosticsSettingsLoader();
            _enableRequestResponseDiagnosticsForAgent = diagnosticsSettings.EnableRequestResponseLogging;
            _activeTurn = new ResponseTurn(diagnosticsSettings.EnableRequestResponseLogging);
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
                GameLoggerResolver.ResolveRequired<AgenticMind>().LogError("AgenticMind requires a configured NPC Voice.");
                return;
            }

            if (ClientProvider is null)
            {
                GameLoggerResolver.ResolveRequired<AgenticMind>().LogError("AgenticMind requires a configured ClientProvider.");
                return;
            }

            if (SystemInstruction is null)
            {
                GameLoggerResolver.ResolveRequired<AgenticMind>().LogError("AgenticMind requires a configured SystemInstruction prompt stack.");
                return;
            }

            await RunAgentTurnAsync(observations, turn.CancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (turn.HasSpoken)
        {
            // Treat cancellation after accepted speech as a completed response; duplicate speak calls remain ignored separately.
        }
        catch (Exception ex)
        {
            LogOptionalResponseFailure(ex);
        }
        finally
        {
            await WaitForReplyCooldownAsync(turn);
            EndResponse(turn);
        }
    }

    private bool TrySpeakFromTool(string speech)
        => TrySpeakFromToolForTool(speech) == SpeakToolResult.Spoken;

    internal SpeakToolResult TrySpeakFromToolForTool(string speech)
    {
        ResponseTurn? turn = GetActiveTurn();
        if (turn is null)
        {
            return SpeakToolResult.NoActiveTurn;
        }

        if (!turn.TrySetSpokenSpeech(speech))
        {
            LogOptionalDuplicateSpeakIgnored();
            return SpeakToolResult.DuplicateIgnored;
        }

        if (AIPipelineDebugLog.IsEnabled)
        {
            AIPipelineDebugLog.Latency("LLM first speak tool call after", turn.Stopwatch, $"{turn.SpokenSpeech.Length} chars");
        }

        if (!turn.AllowRunCompletionAfterFirstSpeak)
        {
            turn.CancellationTokenSource.Cancel();
        }

        _ = SpeakAsync(speech, CancellationToken.None).ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    GameLoggerResolver.ResolveRequired<AgenticMind>().LogError(
                        task.Exception?.GetBaseException(),
                        "AgenticMind speech dispatch failed.");
                    return;
                }

                AIPipelineDebugLog.Latency("LLM speech dispatched after", turn.Stopwatch);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return SpeakToolResult.Spoken;
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
        AIAgent agent = EnsureAgent();
        bool enableRequestResponseDiagnostics = _enableRequestResponseDiagnosticsForAgent;
        if (enableRequestResponseDiagnostics)
        {
            StartTemporaryActivityLogListenerIfAvailable();
        }

        if (_session is null)
        {
            Stopwatch sessionStopwatch = AIPipelineDebugLog.StartTimer();
            _session = await agent.CreateSessionAsync(cancellationToken);
            AIPipelineDebugLog.Latency("LLM session created in", sessionStopwatch);
        }

        Stopwatch runStopwatch = AIPipelineDebugLog.StartTimer();
        try
        {
            ChatClientAgentRunOptions options = new(new ChatOptions
            {
                Tools = CreateTurnTools(),
            });

            AgentResponse response = await agent.RunAsync(RenderObservationSummary(observations), _session, options, cancellationToken);
            LogSensitiveTrialAgentResponse(response, enableRequestResponseDiagnostics);
        }
        finally
        {
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("LLM turn returned in", runStopwatch, $"{observations.Count} observation(s)");
            }
        }
    }

    private static void StartTemporaryActivityLogListenerIfAvailable()
    {
        // Sensitive development/debug diagnostics only: starts an in-process listener that writes Agent Framework
        // OpenTelemetry tags/events/baggage, including prompt/response/tool payloads, into the AlleyCat runtime log
        // when Diagnostics:AI:EnableRequestResponseLogging is enabled. Missing logging infrastructure is intentionally
        // tolerated here so isolated AgenticMind tests/runtime contexts keep their existing backend failure containment
        // behaviour.
        if (GameLoggerResolver.TryResolveFactory(out ILoggerFactory? loggerFactory) && loggerFactory is not null)
        {
            AgenticMindActivityLogListener.Start(loggerFactory);
        }
    }

    private static void LogOptionalResponseFailure(Exception exception)
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogError(exception, "AgenticMind response failed.");
        }
    }

    private static void LogOptionalDuplicateSpeakIgnored()
    {
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger) && logger is not null)
        {
            logger.LogWarning(
                "AgenticMind ignored an additional speak tool call in the same turn after first speech was accepted.");
        }
    }

    private static void LogSensitiveTrialAgentResponse(AgentResponse response, bool enableRequestResponseDiagnostics)
    {
        string? diagnostics = CreateSensitiveAgentResponseDiagnosticsOrDefault(response, enableRequestResponseDiagnostics);
        if (diagnostics is null)
        {
            return;
        }

        // Sensitive development/debug diagnostics only: records Agent Framework response payloads through the general
        // AgentResponse result path rather than relying on the speak tool argument as the primary response source.
        if (GameLoggerResolver.TryResolve(out ILogger<AgenticMind>? logger)
            && logger is not null
            && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Sensitive development-only Agent Framework response diagnostics: {AgentResponseDiagnostics}",
                diagnostics);
        }
    }

    internal static string? CreateSensitiveAgentResponseDiagnosticsOrDefault(
        AgentResponse response,
        bool enableRequestResponseDiagnostics)
        => enableRequestResponseDiagnostics ? CreateSensitiveTrialAgentResponseDiagnostics(response) : null;

    internal static string CreateSensitiveTrialAgentResponseDiagnostics(AgentResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        List<string> diagnostics =
        [
            $"Text={FormatDiagnosticValue(response.Text)}",
            $"Messages={response.Messages.Count}",
        ];

        for (int index = 0; index < response.Messages.Count; index++)
        {
            ChatMessage message = response.Messages[index];
            diagnostics.Add($"Message[{index}].Role={message.Role}");
            diagnostics.Add($"Message[{index}].Text={FormatDiagnosticValue(message.Text)}");
            diagnostics.Add($"Message[{index}].Contents={message.Contents.Count}");
        }

        return string.Join("; ", diagnostics);
    }

    private static string FormatDiagnosticValue(string? value)
        => string.IsNullOrEmpty(value) ? "<empty>" : value;

    private AIAgent EnsureAgent()
    {
        AIDiagnosticsSettings diagnosticsSettings = _diagnosticsSettingsLoader();
        if (_agent is not null
            && ReferenceEquals(_clientProviderForAgent, ClientProvider)
            && ReferenceEquals(_systemInstructionForAgent, SystemInstruction)
            && _agentRequestResponseDiagnosticsEnabled == diagnosticsSettings.EnableRequestResponseLogging)
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
        _enableRequestResponseDiagnosticsForAgent = diagnosticsSettings.EnableRequestResponseLogging;
        _agentRequestResponseDiagnosticsEnabled = diagnosticsSettings.EnableRequestResponseLogging;
        ChatClientAgent agent = ClientProvider.CreateChatClient().AsAIAgent(
            instructions: definition.Instructions,
            name: definition.Name,
            description: definition.Description);
        _agent = ConfigureAgentDiagnostics(agent, this, diagnosticsSettings.EnableRequestResponseLogging);

        return _agent;
    }

    internal static AIAgent ConfigureAgentDiagnostics(
        ChatClientAgent agent,
        IServiceProvider serviceProvider,
        bool enableRequestResponseDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return enableRequestResponseDiagnostics
            ? agent
                .AsBuilder()
                .UseOpenTelemetry(
                    AgenticMindActivityLogListener.DefaultActivitySourceName,
                    // Sensitive development/debug content capture: emits prompts, responses, and tool data to subscribed
                    // OpenTelemetry listeners/exporters only when Diagnostics:AI:EnableRequestResponseLogging is enabled.
                    static telemetryAgent => telemetryAgent.EnableSensitiveData = true)
                .Build(serviceProvider)
            : agent;
    }

    internal void SetDiagnosticsSettingsLoaderForTesting(Func<AIDiagnosticsSettings> diagnosticsSettingsLoader)
    {
        ArgumentNullException.ThrowIfNull(diagnosticsSettingsLoader);

        _diagnosticsSettingsLoader = diagnosticsSettingsLoader;
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

    internal enum SpeakToolResult
    {
        Spoken,
        DuplicateIgnored,
        NoActiveTurn,
    }

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }

    private sealed class ResponseTurn(bool allowRunCompletionAfterFirstSpeak)
    {
        private readonly Lock _lock = new();

        public CancellationTokenSource CancellationTokenSource { get; } = new();

        public bool AllowRunCompletionAfterFirstSpeak { get; } = allowRunCompletionAfterFirstSpeak;

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
