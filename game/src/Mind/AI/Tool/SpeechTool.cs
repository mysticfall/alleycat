using System.ComponentModel;
using AlleyCat.Core.Logging;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech.Voice;
using Godot;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Action tool that speaks natural-language output through the owning character's voice.
/// </summary>
[Tool]
[GlobalClass]
public partial class SpeechTool : AgentTool
{
    private const string CutShortBeforeSpokenMessage =
        "Your speech was cut short by another event before it could be spoken.";

    /// <summary>Model-facing name of the production speak tool (AI-002 TR-16).</summary>
    internal const string ProductionToolName = "speak";

    /// <summary>
    /// Creates a speech tool with the default model-facing metadata.
    /// </summary>
    public SpeechTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = "Speak the supplied text aloud through your voice. The text must contain only the spoken "
            + "words themselves — no emotes, stage directions, narration, or markup. Keep each utterance short, "
            + "around twenty words; for longer speech, split it and call this tool once per part. Speaking is "
            + "optional and repeatable.";
        // Committed speech reaches the model through its persisted timeline event; the settled exchange itself is
        // protocol bookkeeping a later request never replays.
        DisposesExchangeOnCompletion = true;
    }

    /// <summary>
    /// Runner-owned admission arbitration typed-bound at the AgenticMind composition boundary (AI-002 TR-19/25/56),
    /// or null when this tool was authored or constructed outside that composition — such instances keep the
    /// ordinary cancellable submission path.
    /// </summary>
    internal ToolAdmissionBroker? Admission
    {
        get;
    }

    /// <summary>
    /// Creates a speech tool whose submissions arbitrate admission against the session runner's attended
    /// start/resume holds (AI-002 TR-19/25/56).
    /// </summary>
    internal SpeechTool(ToolAdmissionBroker admission) : this()
    {
        ArgumentNullException.ThrowIfNull(admission);
        Admission = admission;
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Speak;

    private async ValueTask<AgentToolResult> Speak(
        [Description("Exact words to say aloud.")] string speech,
        ScenarioContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(speech))
        {
            throw new ArgumentException("Speech request cannot be blank.", nameof(speech));
        }

        string acceptedSpeech = speech.Trim();
        MindBase mind = Session!.Mind;
        IVoice voice = context.Character.RequireVoice();

        // Marks the reasoning-to-speech boundary before the turn-taking wait so the inferred reasoning gap is
        // not polluted by time spent waiting for another speaker.
        PipelineDebugLog.Marker("Speak tool invoked", $"{acceptedSpeech.Length} chars");

        // Turn-taking guard (AI-002 TR-25): block while an attended speaker's window is open. The owning
        // character's own voice never blocks, and unattributable voices never block.
        try
        {
            await mind.WaitUntilAttendedSpeakerIdleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!mind.HasNodeLifetimeEnded)
        {
            // Interruption while blocked never throws (AI-002 TR-27): nothing was submitted or observed.
            return new AgentToolResult(CutShortBeforeSpokenMessage);
        }

        // Playback hand-off, not admission, is the successful action boundary (AI-002 TR-26): the cancellable
        // submission completes exactly at hand-off, so its successful completion is itself the commit signal,
        // while failure or cancellation before it surfaces here without a result. Hand-off commits the speech
        // irreversibly, so cancellation observed after it — including fresh-turn invalidation — neither cuts
        // playback nor withholds the self observation (AI-002 TR-27, SPCH-005 UR-14/TR-25).
        try
        {
            // TTS admission is arbitrated against attended start/resume holds (AI-002 TR-25/56; SPCH-005 TR-37):
            // the transaction commits voice queue admission and the runner's protected state atomically, and a
            // cue-first refusal admits nothing — no TTS request, queue item, hearing event, or self-observation —
            // surfacing here through the non-throwing not-delivered result (AI-002 TR-27). The capability is
            // discovered from the authored voice projection without any concrete-voice dependency (AI-002 TR-63),
            // and a voice without it — or a tool composed without the session's admission arbitration — keeps the
            // ordinary cancellable submission path with its ordinary silent pre-hand-off withdrawal semantics
            // (SPCH-005 TR-38, AI-002 TR-63).
            if (voice is IAdmissionCapableVoice capableVoice
                && Admission?.TryCreateTransaction() is { } admission)
            {
                if (!await capableVoice.SpeakCancellableAdmittedAsync(acceptedSpeech, cancellationToken, admission))
                {
                    return new AgentToolResult(CutShortBeforeSpokenMessage);
                }
            }
            else
            {
                await voice.SpeakCancellableAsync(acceptedSpeech, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!mind.HasNodeLifetimeEnded)
        {
            // Pre-hand-off withdrawal is silent (SPCH-005 TR-25): no observed speech, no failure broadcast.
            return new AgentToolResult(CutShortBeforeSpokenMessage);
        }

        return new AgentToolResult(
            "Spoken through the configured voice.",
            [new ObservedSpeech(ActorId: null, Content: acceptedSpeech)]);
    }
}
