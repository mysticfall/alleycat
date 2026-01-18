using AlleyCat.Ai;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Transcriber.OpenAi;

public class OpenAiTranscriber(
    AudioClient client,
    Option<PromptText> prompt = default,
    ILoggerFactory? loggerFactory = null
) : Transcriber
{
    private readonly ILogger _logger = loggerFactory.GetLogger<OpenAiTranscriber>();

    public override Eff<IEnv, DialogueText> Transcribe(Stream audio) =>
        from env in runtime<IEnv>()
        from text in liftIO(async () =>
        {
            var options = prompt
                .Filter(x => !string.IsNullOrWhiteSpace(x))
                .Map(p => new AudioTranscriptionOptions
                {
                    Prompt = p.Value
                })
                .ValueUnsafe();

            var result = await client.TranscribeAudioAsync(
                audio,
                "speech.wav",
                options
            );

            var transcription = result.Value;

            var text = transcription.Text;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Transcribed audio: \"{text}\"", text);

                var duration = transcription.Duration?.Seconds;

                if (duration.HasValue)
                {
                    _logger.LogDebug("Took {duration} seconds", duration);
                }
            }

            return text;
        })
        from dialogue in DialogueText.Create(text).ToEff(identity)
        select dialogue;
}