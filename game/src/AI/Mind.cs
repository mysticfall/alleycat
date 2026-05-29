using System.ComponentModel;
using AlleyCat.Body.Voice;
using Godot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AlleyCat.AI;

/// <summary>
/// Speech-driven NPC mind that listens to the player voice and delegates responses to an LLM backend.
/// </summary>
[GlobalClass]
public partial class Mind : Node, IVoiceListener
{
    private const string AlleyInstructions = """
        You are Alley, a warm, observant person standing with the player in a VR room.
        Reply naturally and briefly, as if speaking aloud in real time.
        You must not answer with normal chat text. For every response, call the speak tool exactly once with the
        words Alley should say aloud. Do not describe tool use and do not include stage directions.
        """;

    private readonly Lock _responseStateLock = new();
    private readonly Queue<DeferredGodotAction> _deferredGodotActions = [];
    private readonly Lock _deferredGodotActionsLock = new();
    private ResponseTurn? _activeTurn;
    private ChatClientAgent? _agent;
    private AgentSession? _session;
    private bool _deferredGodotActionFlushQueued;
    private bool _isResponding;

    /// <summary>
    /// Enables player speech handling.
    /// </summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Voice ID accepted as player speech input.
    /// </summary>
    [Export]
    public string PlayerVoiceId { get; set; } = "player";

    /// <summary>
    /// Minimum time to keep player listening paused after Alley starts speaking.
    /// </summary>
    [Export(PropertyHint.Range, "0,10,0.1")]
    public float PostReplyListenCooldownSeconds { get; set; } = 1f;

    /// <summary>
    /// NPC voice used for spoken responses.
    /// </summary>
    [Export]
    public Voice? Voice
    {
        get;
        set;
    }

    /// <summary>
    /// Backend factory used to create the Mind agent.
    /// </summary>
    [Export]
    public MindAgentProvider? AgentProvider { get; set; } = new OpenAIMindAgentProvider();

    /// <inheritdoc />
    public override void _Ready() => AddToGroup(IVoiceListener.GroupName);

    /// <inheritdoc />
    public override void _ExitTree() => RemoveFromGroup(IVoiceListener.GroupName);

    /// <inheritdoc />
    public void ReceiveVoice(string speech, IVoice source)
    {
        if (!ShouldHandleVoice(speech, source))
        {
            return;
        }

        ResponseTurn? turn = TryBeginResponse();
        if (turn is null)
        {
            return;
        }

        _ = RespondAsync(speech.Trim(), turn);
    }

    private bool ShouldHandleVoice(string speech, IVoice source)
        => Enabled
            && !string.IsNullOrWhiteSpace(speech)
            && !ReferenceEquals(source, Voice)
            && source is Voice sourceVoice
            && string.Equals(sourceVoice.Id, PlayerVoiceId, StringComparison.Ordinal);

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

    private async Task RespondAsync(string playerSpeech, ResponseTurn turn)
    {
        try
        {
            if (Voice is null)
            {
                GD.PushError("Mind requires a configured NPC Voice.");
                return;
            }

            if (AgentProvider is null)
            {
                GD.PushError("Mind requires a configured AgentProvider.");
                return;
            }

            ChatClientAgent agent = _agent ??= AgentProvider.CreateAgent(CreateAgentDefinition());
            _session ??= await agent.CreateSessionAsync();

            _ = await agent.RunAsync(playerSpeech, _session, cancellationToken: turn.CancellationTokenSource.Token);
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

    private async Task<string> SpeakFromToolAsync(string speech, CancellationToken cancellationToken)
    {
        ResponseTurn? turn = GetActiveTurn();
        if (turn is null)
        {
            return "No active player turn.";
        }

        if (!turn.TrySetSpokenSpeech(speech))
        {
            return "Already spoken. Wait for the next player speech before replying again.";
        }

        await SpeakAsync(speech, cancellationToken);
        turn.CancellationTokenSource.Cancel();

        return "Spoken. End this turn and wait for the next player speech.";
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

    private MindAgentDefinition CreateAgentDefinition()
    {
        SpeechTool tool = new(SpeakFromToolAsync);

        return new MindAgentDefinition(
            AlleyInstructions,
            "Alley",
            "Prototype NPC mind for in-world speech responses.",
            [AIFunctionFactory.Create(tool.Speak, "speak", "Speak the supplied text aloud to the player.")]);
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

    private sealed class DeferredGodotAction(Action action, TaskCompletionSource completionSource)
    {
        public Action Action { get; } = action;

        public TaskCompletionSource CompletionSource { get; } = completionSource;
    }

    private sealed class ResponseTurn
    {
        private readonly Lock _lock = new();

        public CancellationTokenSource CancellationTokenSource { get; } = new();

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

    private sealed class SpeechTool(Func<string, CancellationToken, Task<string>> speakAsync)
    {
        [Description("Speak a natural-language reply to the player.")]
        public Task<string> Speak(
            [Description("Exact words Alley should say aloud.")] string speech,
            CancellationToken cancellationToken)
            => speakAsync(speech, cancellationToken);
    }
}
