using AlleyCat.Ai;
using AlleyCat.Env;
using AlleyCat.Logging;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using OpenAI.Audio;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech.Generator.OpenAi;

public class OpenAiSpeechGen(
    GeneratedSpeechVoice voice,
    AudioClient client,
    ILoggerFactory? loggerFactory = null
) : ISpeechGenerator
{
    private readonly ILogger _logger = loggerFactory.GetLogger<OpenAiSpeechGen>();

    public Eff<IEnv, SpeechAudio> Generate(
        DialogueText text, 
        Option<PromptText> instruction = default
    ) =>
        from env in runtime<IEnv>()
        from audio in liftIO(async () =>
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Generating speech using voice '{voice}': \"{text}\"",
                    voice,
                    text
                );
            }

            var response = await client.GenerateSpeechAsync(
                text.Value,
                voice,
                new SpeechGenerationOptions
                {
                    ResponseFormat = GeneratedSpeechFormat.Wav,
                    Instructions = instruction.ValueUnsafe()
                }
            );

            var data = response.Value.ToArray();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Finished generating speech: {:N} bytes.", data.Length);
            }

            return new SpeechAudio { Data = data };
        })
        select audio;
}