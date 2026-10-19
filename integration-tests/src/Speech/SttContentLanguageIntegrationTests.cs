using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AlleyCat.Core.Content;
using AlleyCat.Speech.Transcription;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for the content-pack spoken-language wiring into prepared STT request settings (SPCH-003
/// TR-29): settings are prepared on the Godot thread with the resolved pack language overriding the configured
/// <c>STT</c> language, and the shipped model stays multilingual.
/// </summary>
public sealed partial class SttContentLanguageIntegrationTests
{
    /// <summary>
    /// Selected manifests reach both real request entry points after production settings preparation, without
    /// replacing the language wiring, SDK client, multipart construction, or automatic finalisation implementation.
    /// Only the context dependency and HTTP transport are supplied by the fixture.
    /// </summary>
    [Fact]
    [Headless]
    public async Task SelectedEnglishAndKorean_ReadyFeedsManualAndAutomaticMultipartRequests()
    {
        SceneTree sceneTree = GetSceneTree();
        foreach (string language in new[] { "en", "ko" })
        {
            ContentContext selected = SpeechContentSelection.Select(language);
            int contextReads = 0;
            using CapturingHandler handler = new();
            using System.Net.Http.HttpClient httpClient = new(handler);
            RequestTestTranscriber transcriber = new()
            {
                ConfigPath = "res://assets/testing/speech/stt-language-override.yaml",
                ContentContextLoader = () =>
                {
                    contextReads++;
                    return selected;
                },
                RequestTransport = new HttpClientPipelineTransport(httpClient),
            };
            sceneTree.Root.AddChild(transcriber);
            await WaitForFramesAsync(sceneTree, 2);

            try
            {
                Assert.Equal(language, transcriber.EffectiveRequestLanguage);
                selected = ContentContext.Default with
                {
                    Language = language == "en" ? "ko" : "en"
                };
                RecordedAudioData recording = new([1, 2, 3, 4], 16000, 1);
                Assert.Equal("captured", await transcriber.Transcribe(recording));
                Assert.Equal("captured", await transcriber.FinaliseAutomaticForTest(recording));

                Assert.Equal(1, contextReads);
                Assert.Equal(2, handler.Bodies.Count);
                foreach (string body in handler.Bodies)
                {
                    Assert.Contains($"name=language\r\n\r\n{language}\r\n", body, StringComparison.Ordinal);
                    Assert.Contains("name=model\r\n\r\nsmall\r\n", body, StringComparison.Ordinal);
                    Assert.Contains("name=prompt\r\n\r\nPreserve this hint\r\n", body, StringComparison.Ordinal);
                    Assert.Contains("name=hotwords\r\n\r\nAlleyCat, Mira\r\n", body, StringComparison.Ordinal);
                    Assert.Contains("name=response_format\r\n\r\njson\r\n", body, StringComparison.Ordinal);
                    Assert.Contains("RIFF", body, StringComparison.Ordinal);
                }
                Assert.All(handler.RequestUris, uri =>
                    Assert.Equal("https://stt-language-override.test/v1/audio/transcriptions", uri.ToString()));
            }
            finally
            {
                transcriber.QueueFree();
                await WaitForFramesAsync(sceneTree, 2);
            }
        }
    }

    private sealed partial class RequestTestTranscriber : OpenAITranscriber
    {
        public Task<string> FinaliseAutomaticForTest(RecordedAudioData recording)
            => FinaliseAutomaticUtteranceAsync(recording, CancellationToken.None);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<Uri> RequestUris { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(Encoding.UTF8.GetString(await request.Content!.ReadAsByteArrayAsync(cancellationToken)));
            RequestUris.Add(request.RequestUri!);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    text = "captured"
                }), Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// Request settings prepared in <c>_Ready</c> must carry the integration-test bypass language (English) over a
    /// configured non-English STT language, proving the pack-language precedence at the real lifecycle seam.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Ready_OverridesConfiguredLanguage_WithContentPackLanguage()
    {
        SceneTree sceneTree = GetSceneTree();
        OpenAITranscriber transcriber = new()
        {
            Name = "SttLanguageOverrideTranscriber",
            ConfigPath = "res://assets/testing/speech/stt-language-override.yaml",
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.Equal("en", transcriber.EffectiveRequestLanguage);
        }
        finally
        {
            transcriber.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }

    /// <summary>
    /// The shipped default configuration must prepare English request settings through the merged configuration
    /// path, exercising the default lifecycle seam (SPCH-003 TR-29).
    /// </summary>
    [Fact]
    [Headless]
    public async Task Ready_WithShippedConfig_PreparesEnglishRequestSettings()
    {
        SceneTree sceneTree = GetSceneTree();
        OpenAITranscriber transcriber = new()
        {
            Name = "SttShippedConfigTranscriber"
        };

        sceneTree.Root.AddChild(transcriber);
        await WaitForFramesAsync(sceneTree, 2);

        try
        {
            Assert.Equal("en", transcriber.EffectiveRequestLanguage);
        }
        finally
        {
            transcriber.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }
    }
}
