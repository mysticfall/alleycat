using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Scene;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Interprets completed speech: resolves the speaking character, carries the speaker's explicitly declared
/// recipients into one actor- and addressee-aware observation, and stamps player-authored speech with trusted
/// private provenance (AI-006 TR-4/11/12).
/// </summary>
/// <remarks>
/// Recipients are declarations, never a hearing filter: every listener receives the speech regardless of whom it
/// addressed. Recognised NPC-origin speech uses its declared recipients — empty when the speaker declared none,
/// leaving the addressee undecided — while player-origin speech never carries declared recipients and instead
/// stamps trusted player provenance on the observation.
/// </remarks>
[GlobalClass]
public sealed partial class SpeechPerception : Perception<SpeechPercept>
{
    /// <inheritdoc/>
    public override ValueTask PerceiveAsync(
        SpeechPercept percept,
        PerceptionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        IVoice observerVoice = context.Character.RequireVoice();
        if (string.Equals(percept.SourceVoiceID, observerVoice.Id, StringComparison.Ordinal))
        {
            return ValueTask.CompletedTask;
        }

        ICharacter? recognised = ResolveRecognisedCharacter(percept.SourceVoiceID, context);
        bool isPlayerAuthored = recognised is not null && IsPlayerSpeaker(recognised);
        // Player speech never declares recipients — its addressees stay blank (undecided) while trusted
        // provenance rides the observation privately; unknown speakers are always undecided because their
        // declared recipients cannot be attributed to a recognised speaker.
        IReadOnlyList<string> addressees = recognised is null || isPlayerAuthored
            ? []
            : CopyDistinctInOrder(percept.DeclaredRecipients);
        Emit(
            new ObservedSpeech(recognised?.FullId, percept.Content, addressees)
            {
                IsPlayerAuthored = isPlayerAuthored,
            },
            new SpeechObservationTransport(
                percept.SourceVoiceID,
                percept.SpeechGroupID,
                percept.SegmentIndex,
                percept.Continued));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Determines whether the speaker is the scene's player character, establishing trusted player provenance
    /// from scene membership (AI-006 TR-12).
    /// </summary>
    private static bool IsPlayerSpeaker(ICharacter speaker)
        => speaker is Node node && node.IsInGroup(ISceneContext.PlayerGroupName);

    /// <summary>
    /// Defensively copies a declared recipient set, preserving declaration order and collapsing duplicates so the
    /// observation's addressee-set invariant can never reject an ingested percept.
    /// </summary>
    private static IReadOnlyList<string> CopyDistinctInOrder(IReadOnlyList<string> declaredRecipients)
    {
        if (declaredRecipients.Count == 0)
        {
            return [];
        }

        List<string> copy = [];
        foreach (string recipient in declaredRecipients)
        {
            if (!copy.Contains(recipient))
            {
                copy.Add(recipient);
            }
        }

        return copy;
    }

    private static ICharacter? ResolveRecognisedCharacter(string sourceVoiceID, PerceptionContext context)
    {
        if (string.IsNullOrWhiteSpace(sourceVoiceID))
        {
            return null;
        }

        ICharacter? recognised = null;
        foreach (ICharacter candidate in context.Scene.Characters)
        {
            if (!candidate.TryGetVoice(out IVoice? voice)
                || voice is null
                || string.IsNullOrWhiteSpace(voice.Id)
                || !string.Equals(voice.Id, sourceVoiceID, StringComparison.Ordinal))
            {
                continue;
            }

            if (recognised is not null)
            {
                throw new InvalidOperationException($"Voice ID '{sourceVoiceID}' ambiguously matches current-scene characters '{recognised.FullId}' and '{candidate.FullId}'.");
            }

            recognised = candidate;
        }

        return recognised;
    }
}
