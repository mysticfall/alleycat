using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Animation.BlendShape;
using AlleyCat.Common;
using AlleyCat.Env;
using AlleyCat.Speech.Generator;
using AlleyCat.Logging;
using LanguageExt;
using Microsoft.Extensions.Logging;
using static LanguageExt.Prelude;
using HttpClient = System.Net.Http.HttpClient;

namespace AlleyCat.Speech.LipSync.NeuroSync;

internal record LipSyncData
{
    [JsonPropertyName("blendshapes")] public required float[][] Blendshapes { get; init; }
}

public class NeuroSyncGen(
    Uri endpoint,
    BlendShapeSet blendShapes,
    ILoggerFactory? loggerFactory = null
) : ILipSyncGenerator
{
    private readonly ILogger _logger = loggerFactory.GetLogger<NeuroSyncGen>();

    private readonly HttpClient _http = new()
    {
        BaseAddress = endpoint
    };

    public Eff<IEnv, BlendShapeAnim> Generate(SpeechAudio audio) =>
        from env in runtime<IEnv>()
        from defaultFps in FrameRate.Create(60).ToEff(identity)
        from anim in liftIO(async () =>
        {
            var names = blendShapes.BlendShapes;

            using var content = new ByteArrayContent(audio.Data);

            content.Headers.ContentType = new MediaTypeHeaderValue("audio/x-wav");

            var response = await _http.PostAsync("/audio_to_blendshapes", content);

            response.EnsureSuccessStatusCode();

            var jsonText = await response.Content.ReadAsStringAsync();

            var data = JsonSerializer.Deserialize<LipSyncData>(jsonText);

            if (data == null)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError("Failed to deserialize lip-sync data: {}", jsonText);
                }

                throw new InvalidDataContractException("Failed to deserialize lip-sync data.");
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Received lip-sync data: {} frames.", data.Blendshapes.Length);
            }

            var frames = data
                .Blendshapes
                .AsIterable()
                .Map(e => e
                    .AsIterable()
                    .Map((value, i) => (names[i], value))
                    .ToMap()
                )
                .ToSeq();

            return new BlendShapeAnim(frames, defaultFps);
        })
        select anim;
}