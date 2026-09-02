using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>
/// Interprets completed speech: resolves the speaking character and emits one actor-aware observation.
/// </summary>
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
        Emit(new ObservedSpeech(
            recognised?.FullId,
            percept.SourceVoiceID,
            percept.Content,
            percept.SpeechGroupID,
            percept.SegmentIndex,
            percept.Continued));
        return ValueTask.CompletedTask;
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
