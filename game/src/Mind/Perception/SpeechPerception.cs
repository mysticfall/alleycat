using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Interprets speech into an actor-aware observation emission.</summary>
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

        ICharacter? recognised = null;
        if (!string.IsNullOrWhiteSpace(percept.SourceVoiceID))
        {
            foreach (ICharacter candidate in context.Scene.Characters)
            {
                if (!candidate.TryGetVoice(out IVoice? voice)
                    || voice is null
                    || string.IsNullOrWhiteSpace(voice.Id)
                    || !string.Equals(voice.Id, percept.SourceVoiceID, StringComparison.Ordinal))
                {
                    continue;
                }

                if (recognised is not null)
                {
                    throw new InvalidOperationException($"Voice ID '{percept.SourceVoiceID}' ambiguously matches current-scene characters '{recognised.FullId}' and '{candidate.FullId}'.");
                }

                recognised = candidate;
            }
        }

        Emit(recognised is null
            ? new ObservedSpeech(null, percept.SourceVoiceID, percept.Content)
            : new ObservedSpeech(recognised.FullId, percept.SourceVoiceID, percept.Content));
        return ValueTask.CompletedTask;
    }
}
