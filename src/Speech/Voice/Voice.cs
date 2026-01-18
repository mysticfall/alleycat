using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Sense.Hearing;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Voice;

public readonly record struct VoiceId
{
    public string Value { get; }

    private VoiceId(string value)
    {
        Value = value;
    }

    public static implicit operator string(VoiceId id) => id.Value;

    public override string ToString() => Value;

    public static Either<ParseError, VoiceId> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Voice ID cannot be null or empty."))
            .Map(v => new VoiceId(v));
}

public interface IVoice : ISoundSource
{
    VoiceId Id { get; }

    IO<bool> IsSpeaking { get; }

    Eff<IEnv, Unit> Speak(DialogueText speech) =>
        from _1 in liftEff(() =>
        {
            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.LogDebug("{voice} says: \"{speech}\"", Id.Value, speech.Value);
            }
        })
        from _2 in EmitSound(
            new Speech
            {
                Dialogue = speech,
                Voice = Id,
                Timestamp = DateTime.Now
            }, 10.Metres()
        )
        select unit;
}