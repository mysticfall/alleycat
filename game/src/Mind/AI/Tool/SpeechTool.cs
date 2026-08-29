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

    /// <summary>
    /// Creates a speech tool with the default model-facing metadata.
    /// </summary>
    public SpeechTool()
    {
        ToolName = "speak";
        ToolDescription = "Speak the supplied text aloud through your voice. The text must contain only the spoken "
            + "words themselves — no emotes, stage directions, narration, or markup. Keep each utterance short, "
            + "around twenty words; for longer speech, split it and call this tool once per part. Speaking is "
            + "optional and repeatable.";
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
            await voice.SpeakCancellableAsync(acceptedSpeech, cancellationToken);
        }
        catch (OperationCanceledException) when (!mind.HasNodeLifetimeEnded)
        {
            // Pre-hand-off withdrawal is silent (SPCH-005 TR-25): no observed speech, no failure broadcast.
            return new AgentToolResult(CutShortBeforeSpokenMessage);
        }

        return new AgentToolResult(
            "Spoken through the configured voice.",
            [new ObservedSpeech(ActorId: null, VoiceId: null, Content: acceptedSpeech)]);
    }
}
