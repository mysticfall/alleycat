using AlleyCat.Character;
using AlleyCat.Mind.Observation;
using AlleyCat.Speech;
using AlleyCat.Speech.Voice;
using Godot;

namespace AlleyCat.Mind.Perception;

/// <summary>Interprets speech into an actor-aware observation and recognised-actor attention.</summary>
[GlobalClass]
public sealed partial class SpeechPerception : Perception<SpeechPercept>
{
    private const float Contribution = 0.5f;

    /// <inheritdoc/>
    public override PerceptionResult Perceive(SpeechPercept percept, PerceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(percept);
        ArgumentNullException.ThrowIfNull(context);
        IVoice observerVoice = context.Character.RequireVoice();
        if (string.Equals(percept.SourceVoiceID, observerVoice.Id, StringComparison.Ordinal))
        {
            return new PerceptionResult([], []);
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

        return recognised is null
            ? new PerceptionResult([], [new ObservedSpeech(null, percept.SourceVoiceID, percept.Content)])
            : new PerceptionResult(
                [new AttentionEffect(recognised.FullId, Contribution)],
                [new ObservedSpeech(recognised.FullId, percept.SourceVoiceID, percept.Content)]);
    }
}
