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

    /// <summary>Model-facing name of the production speak tool (AI-002 TR-13).</summary>
    internal const string ProductionToolName = "speak";

    /// <summary>
    /// Creates a speech tool with the default model-facing metadata.
    /// </summary>
    public SpeechTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = "Speak the supplied text aloud through your voice. The text must contain only the spoken "
            + "words themselves — no emotes, stage directions, narration, or markup. Keep each utterance short, "
            + "around twenty words. When one contribution runs longer, split it into several short consecutive "
            + "speak calls with no wait in between — this is strongly recommended, because shorter parts begin "
            + "being spoken sooner and this allows later parts to be prepared while earlier speech plays. "
            + "These calls deliver a single contribution: once it is finished, normally yield and give the other "
            + "person room to reply as the situation warrants. This call remains in your context as the record of "
            + "what you said. Speaking is optional and repeatable. Pass `recipients` as the array of characters "
            + "you are addressing — the exact backticked character IDs appearing in your context, passed as bare "
            + "ID strings without backticks, such as \"char:rin\" — whenever your speech is addressed to specific "
            + "people; multiple recipients are allowed. Omit `recipients` or pass an empty array when the address "
            + "is unspecified; omitting does not mean everyone.";
    }

    /// <summary>
    /// Runner-owned admission arbitration typed-bound at the AgenticMind composition boundary (AI-002 TR-13,
    /// SPCH-005 TR-37),
    /// or null when this tool was authored or constructed outside that composition — such instances keep the
    /// ordinary cancellable submission path.
    /// </summary>
    internal ToolAdmissionBroker? Admission
    {
        get;
    }

    /// <summary>
    /// Creates a speech tool whose submissions arbitrate admission against the session runner's attended
    /// start/resume holds (AI-002 TR-13, SPCH-005 TR-37).
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
        [Description(
            "Characters you are addressing: the backticked character IDs appearing in your context, passed as "
            + "bare ID strings without backticks, such as \"char:rin\". Multiple recipients are allowed. "
            + "Omit or pass an empty array when the address is unspecified; omitting does not mean everyone.")]
        IReadOnlyList<string>? recipients = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(speech))
        {
            throw new ArgumentException("Speech request cannot be blank.", nameof(speech));
        }

        string acceptedSpeech = speech.Trim();
        MindBase mind = Session!.Mind;
        IVoice voice = context.Character.RequireVoice();
        if (!TryResolveDeclaredRecipients(recipients, context, mind, out IReadOnlyList<string>? resolvedRecipients, out string? recipientError))
        {
            // Model-correctable validation failure: nothing was submitted or observed, and the message names the
            // problem so the next call can fix it.
            return new AgentToolResult(recipientError);
        }

        if (resolvedRecipients is not null)
        {
            // Pre-admission intention signal: the declared address is valid before any turn-taking wait or admission
            // arbitration, so attention-driven systems may react to whom this speech will address.
            mind.NotifySpeakIntention(resolvedRecipients);
        }

        // Marks the reasoning-to-speech boundary before the turn-taking wait so the inferred reasoning gap is
        // not polluted by time spent waiting for another speaker.
        PipelineDebugLog.Marker("Speak tool invoked", $"{acceptedSpeech.Length} chars");

        // Turn-taking guard (SPCH-005 TR-37): block while an attended speaker's window is open. The owning
        // character's own voice never blocks, and unattributable voices never block.
        try
        {
            await mind.WaitUntilAttendedSpeakerIdleAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!mind.HasNodeLifetimeEnded)
        {
            // Interruption while blocked never throws (AI-002 TR-22): nothing was submitted or observed.
            return new AgentToolResult(CutShortBeforeSpokenMessage);
        }

        // Playback hand-off, not admission, is the successful action boundary (AI-002 TR-14): the cancellable
        // submission completes exactly at hand-off, so its successful completion is itself the commit signal,
        // while failure or cancellation before it surfaces here without a result. Hand-off commits the speech
        // irreversibly, so cancellation observed after it — including fresh-turn invalidation — neither cuts
        // playback nor withholds the self observation (AI-002 TR-14, SPCH-005 UR-14/TR-25).
        try
        {
            // TTS admission is arbitrated against attended start/resume holds (AI-002 TR-13/14; SPCH-005 TR-37):
            // the transaction commits voice queue admission and the runner's protected state atomically, and a
            // cue-first refusal admits nothing — no TTS request, queue item, hearing event, or self-observation —
            // surfacing here through the non-throwing not-delivered result (SPCH-005 TR-37). The capability is
            // discovered from the authored voice projection without any concrete-voice dependency (SPCH-005 TR-37),
            // and a voice without it — or a tool composed without the session's admission arbitration — keeps the
            // ordinary cancellable submission path with its ordinary silent pre-hand-off withdrawal semantics
            // (SPCH-005 TR-38).
            if (voice is IAdmissionCapableVoice capableVoice
                && Admission?.TryCreateTransaction() is { } admission)
            {
                if (!await capableVoice.SpeakCancellableAdmittedAsync(acceptedSpeech, resolvedRecipients, cancellationToken, admission))
                {
                    return new AgentToolResult(CutShortBeforeSpokenMessage);
                }
            }
            else
            {
                await voice.SpeakCancellableAsync(acceptedSpeech, resolvedRecipients, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!mind.HasNodeLifetimeEnded)
        {
            // Pre-hand-off withdrawal is silent (SPCH-005 TR-25): no observed speech, no failure broadcast.
            return new AgentToolResult(CutShortBeforeSpokenMessage);
        }

        // The self observation is pre-stamped with the owning character's canonical identity; Mind's ingest-side
        // owner stamping re-applies the same value idempotently. Declared recipients ride the observation so the
        // speaker's own record states whom it addressed.
        return new AgentToolResult(
            "Spoken through the configured voice.",
            [new ObservedSpeech(context.Character.FullId, acceptedSpeech, resolvedRecipients)]);
    }

    /// <summary>
    /// Validates the model-supplied recipient declarations against the live scene roster — the same roster source
    /// speech perception resolves speakers against — returning the canonical recipient set in declaration order
    /// with duplicates collapsed, or null when the address is unspecified.
    /// </summary>
    /// <param name="recipients">Model-supplied recipient declarations; null or empty means unspecified.</param>
    /// <param name="context">Trusted session binding owning the speaking character.</param>
    /// <param name="mind">Mind boundary whose scene roster validates the declarations.</param>
    /// <param name="resolvedRecipients">Canonical distinct recipients in declaration order, or null when unspecified.</param>
    /// <param name="error">Model-correctable validation failure naming the problem, or null when valid.</param>
    private static bool TryResolveDeclaredRecipients(
        IReadOnlyList<string>? recipients,
        ScenarioContext context,
        MindBase mind,
        out IReadOnlyList<string>? resolvedRecipients,
        out string? error)
    {
        resolvedRecipients = null;
        error = null;
        if (recipients is not { Count: > 0 })
        {
            return true;
        }

        List<string> resolved = [];
        foreach (string recipient in recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient))
            {
                error = "Cannot speak: the recipients array contains a blank entry. Pass each recipient as the "
                    + "exact backticked character ID from your context, as a bare ID string without backticks.";
                return false;
            }

            string candidate = recipient.Trim();
            if (string.Equals(candidate, context.Character.FullId, StringComparison.Ordinal))
            {
                error = $"Cannot speak: '{candidate}' is you, and a speaker cannot address themselves. Pass the "
                    + "character IDs of the people you are speaking to, or omit recipients when unspecified.";
                return false;
            }

            if (mind.GetCurrentSceneContext().Find(candidate) is null)
            {
                error = $"Cannot speak: '{candidate}' is not a character in the current scene. Pass each "
                    + "recipient as the exact backticked character ID appearing in your context, or omit "
                    + "recipients when unspecified.";
                return false;
            }

            if (!resolved.Contains(candidate))
            {
                resolved.Add(candidate);
            }
        }

        resolvedRecipients = resolved;
        return true;
    }
}
