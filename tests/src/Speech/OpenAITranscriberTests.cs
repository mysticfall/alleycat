using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using AlleyCat.Core.Configuration;
using AlleyCat.Speech.Transcription;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for OpenAI-compatible speech transcription helpers.
/// </summary>
public sealed class OpenAITranscriberTests
{
    /// <summary>
    /// OpenAI-compatible backends without auth must still produce an SDK-safe credential value.
    /// </summary>
    [Fact]
    public void GetApiKeyOrDefault_ApiKeyMissing_UsesDummyCompatibleBackendKey()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: null,
            Model: "whisper-1",
            Language: null,
            Prompt: null,
            Temperature: null,
            TimeoutSeconds: null);

        string apiKey = settings.GetApiKeyOrDefault();

        Assert.Equal("unused-api-key", apiKey);
    }

    /// <summary>
    /// Full endpoint URLs must be preserved as configured.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_FullEndpointConfig_PreservesConfiguredUri()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: null,
            Prompt: null,
            Temperature: null,
            TimeoutSeconds: null);

        Uri endpoint = settings.CreateEndpointUri();

        Assert.Equal("https://api.openai.com/v1", endpoint.ToString().TrimEnd('/'));
    }

    /// <summary>
    /// Host-only values must fail fast so config stays aligned with the full endpoint URL contract.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_HostOnlyConfig_Throws()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "api.openai.com",
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: null,
            Prompt: null,
            Temperature: null,
            TimeoutSeconds: null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(settings.CreateEndpointUri);

        Assert.Contains("must be a valid absolute endpoint URL", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Endpoint URLs without an API base path must fail fast so compatible backends remain explicit.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_EndpointWithoutPath_Throws()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com",
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: null,
            Prompt: null,
            Temperature: null,
            TimeoutSeconds: null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(settings.CreateEndpointUri);

        Assert.Contains("must include the API base path", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SDK transcription options must reflect the configured optional request fields.
    /// </summary>
    [Fact]
    public void CreateTranscriptionOptions_WithConfiguredFields_MapsSdkOptions()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: "en",
            Prompt: "Transcribe clearly.",
            Temperature: 0.35f,
            TimeoutSeconds: 30);

        AudioTranscriptionOptions options = OpenAITranscriber.CreateTranscriptionOptions(settings);

        Assert.Equal("en", options.Language);
        Assert.Equal("Transcribe clearly.", options.Prompt);
        Assert.Equal(0.35f, options.Temperature);
    }

    /// <summary>
    /// Merged configuration must preserve base values while allowing user overrides for STT settings.
    /// </summary>
    [Fact]
    public void Load_MergedConfiguration_UsesMergedSttValues()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
                ["Model"] = "whisper-1",
                ["Prompt"] = "Base prompt",
            },
        };
        Dictionary<string, IReadOnlyDictionary<string, string>> overrideSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ApiKey"] = "sk-user",
                ["Temperature"] = "0.25",
            },
        };

        IConfiguration configuration = CreateConfiguration(baseSections, overrideSections);

        var settings =
            OpenAITranscriber.OpenAITranscriberSettings.Load(configuration, "merged-test-config");

        Assert.Equal("https://base.example/v1", settings.Host);
        Assert.Equal("whisper-1", settings.Model);
        Assert.Equal("Base prompt", settings.Prompt);
        Assert.Equal("sk-user", settings.ApiKey);
        Assert.Equal(0.25f, settings.Temperature);
    }

    /// <summary>
    /// The default config path must route through merged loading so user STT overrides apply.
    /// </summary>
    [Fact]
    public void Load_DefaultConfigPath_UsesMergedConfigRouting()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
                ["Model"] = "whisper-1",
            },
        };
        Dictionary<string, IReadOnlyDictionary<string, string>> overrideSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ApiKey"] = "sk-user",
                ["Temperature"] = "0.4",
            },
        };

        bool mergedLoaderCalled = false;

        var settings = OpenAITranscriber.OpenAITranscriberSettings.Load(
            GameConfiguration.DefaultBaseConfigPath,
            defaultConfigurationLoader: () =>
            {
                mergedLoaderCalled = true;
                return CreateConfiguration(baseSections, overrideSections);
            },
            customConfigurationLoader: _ => throw new Xunit.Sdk.XunitException(
                "Single-file loader should not be used for the default config path."));

        Assert.True(mergedLoaderCalled);
        Assert.Equal("https://base.example/v1", settings.Host);
        Assert.Equal("sk-user", settings.ApiKey);
        Assert.Equal(0.4f, settings.Temperature);
    }

    /// <summary>
    /// Custom config paths must load only the requested file without implicit user override merging.
    /// </summary>
    [Fact]
    public void Load_CustomConfigPath_UsesDirectConfigRoutingWithoutImplicitMerge()
    {
        const string customConfigPath = "res://custom-stt.json";
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://custom.example/v1",
                ["Model"] = "whisper-custom",
            },
        };

        string? loadedPath = null;

        var settings = OpenAITranscriber.OpenAITranscriberSettings.Load(
            customConfigPath,
            defaultConfigurationLoader: () => throw new Xunit.Sdk.XunitException(
                "Merged loader should not be used for a custom config path."),
            customConfigurationLoader: path =>
            {
                loadedPath = path;
                return CreateConfiguration(baseSections);
            });

        Assert.Equal(customConfigPath, loadedPath);
        Assert.Equal("https://custom.example/v1", settings.Host);
        Assert.Equal("whisper-custom", settings.Model);
        Assert.Null(settings.ApiKey);
        Assert.Null(settings.Temperature);
    }

    /// <summary>
    /// Empty transcription payloads must fail fast instead of surfacing blank transcripts.
    /// </summary>
    [Fact]
    public void GetTranscriptionTextOrThrow_EmptyText_Throws()
    {
        AudioTranscription response = OpenAIAudioModelFactory.AudioTranscription(
            text: string.Empty,
            duration: null,
            language: "en",
            words: [],
            segments: []);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => OpenAITranscriber.GetTranscriptionTextOrThrow(response));

        Assert.Contains("did not contain a non-empty 'text' field", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// OpenAI request preparation must produce a rewound WAV stream and configured SDK options off the caller path.
    /// </summary>
    [Fact]
    public void PrepareTranscriptionRequest_ConfiguredAudioAndSettings_CreatesUploadRequest()
    {
        RecordedAudioData recordedAudio = new([0x34, 0x12, 0x78, 0x56], sampleRate: 16000, channelCount: 1);
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: "en",
            Prompt: "Transcribe clearly.",
            Temperature: 0.35f,
            TimeoutSeconds: 30);

        using ILoggerFactory loggerFactory = new TestLoggerFactory();
        using OpenAITranscriber.PreparedTranscriptionRequest request =
            OpenAITranscriber.PrepareTranscriptionRequest(recordedAudio, settings, loggerFactory);
        WaveFileStream wavStream = Assert.IsType<WaveFileStream>(request.WavStream);
        Assert.True(MemoryMarshal.TryGetArray(recordedAudio.PCMData, out ArraySegment<byte> recordingSegment));
        Assert.True(MemoryMarshal.TryGetArray(wavStream.PCMData, out ArraySegment<byte> streamSegment));
        byte[] bytes = new byte[request.WavStream.Length];
        _ = request.WavStream.Read(bytes);

        Assert.NotNull(request.Client);
        Assert.Equal(48, request.WavStream.Length);
        Assert.Same(recordingSegment.Array, streamSegment.Array);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal("en", request.Options.Language);
        Assert.Equal("Transcribe clearly.", request.Options.Prompt);
        Assert.Equal(0.35f, request.Options.Temperature);

        request.WavStream.Position = 0;
        byte[] replay = new byte[request.WavStream.Length];
        Assert.Equal(replay.Length, request.WavStream.Read(replay));
        Assert.Equal(bytes, replay);
    }

    /// <summary>
    /// Caller-owned arrays cannot mutate the PCM retained by the production recording/request route.
    /// </summary>
    [Fact]
    public void PrepareTranscriptionRequest_PublicRecordingCopy_IsImmutableFromSource()
    {
        byte[] source = [0x34, 0x12];
        RecordedAudioData recording = new(source, sampleRate: 16000, channelCount: 1);
        source[0] = 0xff;
        OpenAITranscriber.OpenAITranscriberSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: null,
            Model: "whisper-1",
            Language: null,
            Prompt: null,
            Temperature: null,
            TimeoutSeconds: null);

        using ILoggerFactory loggerFactory = new TestLoggerFactory();
        using OpenAITranscriber.PreparedTranscriptionRequest request =
            OpenAITranscriber.PrepareTranscriptionRequest(recording, settings, loggerFactory);
        request.WavStream.Position = WaveFileStream.HeaderLength;

        Assert.Equal(0x34, request.WavStream.ReadByte());
    }

    /// <summary>
    /// The pinned SDK must serialise and replay the complete composite WAV stream through its real multipart path.
    /// </summary>
    [Fact]
    public async Task TranscribeAudioAsync_WaveFileStream_RetriesWithIdenticalMultipartWavBody()
    {
        byte[] pcmData = [0x34, 0x12, 0x78, 0x56, 0xbc, 0x9a];
        using WaveFileStream expectedStream = new(pcmData, sampleRate: 16000, channelCount: 1);
        byte[] expectedWave = new byte[expectedStream.Length];
        Assert.Equal(expectedWave.Length, expectedStream.Read(expectedWave));
        using CapturingTranscriptionHandler handler = new();
        using HttpClient httpClient = new(handler);
        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            RetryPolicy = new ImmediateRetryPolicy(),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        AudioClient client = new("whisper-1", new ApiKeyCredential("unit-test-key"), clientOptions);
        using WaveFileStream uploadStream = new(pcmData, sampleRate: 16000, channelCount: 1);

        AudioTranscription transcription = await client.TranscribeAudioAsync(
            uploadStream,
            "alleycat-recording.wav",
            new AudioTranscriptionOptions { Language = "en" });

        Assert.Equal("synthetic transcript", transcription.Text);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        Assert.All(handler.ContentTypes, value => Assert.StartsWith("multipart/form-data; boundary=", value));
        Assert.All(handler.RequestBodies, body => Assert.Equal(expectedWave, ExtractFileContent(body)));
    }

    private static IConfiguration CreateConfiguration(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> baseSections,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? overrideSections = null)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        AddSections(values, baseSections);
        if (overrideSections is not null)
        {
            AddSections(values, overrideSections);
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static void AddSections(
        Dictionary<string, string?> values,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> sections)
    {
        foreach ((string section, IReadOnlyDictionary<string, string> sectionValues) in sections)
        {
            foreach ((string key, string value) in sectionValues)
            {
                values[$"{section}:{key}"] = value;
            }
        }
    }

    private static byte[] ExtractFileContent(byte[] multipartBody)
    {
        ReadOnlySpan<byte> body = multipartBody;
        ReadOnlySpan<byte> fileMarker = "alleycat-recording.wav"u8;
        int fileMarkerIndex = body.IndexOf(fileMarker);
        Assert.True(fileMarkerIndex >= 0, "The SDK multipart body did not contain the named WAV file part.");

        ReadOnlySpan<byte> filePart = body[fileMarkerIndex..];
        ReadOnlySpan<byte> headerTerminator = "\r\n\r\n"u8;
        int headerLength = filePart.IndexOf(headerTerminator);
        Assert.True(headerLength >= 0, "The SDK multipart file part had no header terminator.");

        ReadOnlySpan<byte> contentAndBoundary = filePart[(headerLength + headerTerminator.Length)..];
        int contentLength = contentAndBoundary.IndexOf("\r\n--"u8);
        Assert.True(contentLength >= 0, "The SDK multipart file part had no closing boundary.");
        return contentAndBoundary[..contentLength].ToArray();
    }

    private sealed class TestLoggerFactory : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider provider)
            => ArgumentNullException.ThrowIfNull(provider);

        public ILogger CreateLogger(string categoryName)
            => new TestLogger();

        public void Dispose()
        {
        }
    }

    private sealed class TestLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel)
            => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => ArgumentNullException.ThrowIfNull(formatter);
    }

    private sealed class CapturingTranscriptionHandler : HttpMessageHandler
    {
        public List<byte[]> RequestBodies { get; } = [];

        public List<string> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://unit.test/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.NotNull(request.Content);
            ContentTypes.Add(request.Content.Headers.ContentType?.ToString() ?? string.Empty);
            RequestBodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));

            if (RequestBodies.Count == 1)
            {
                HttpResponseMessage retryResponse = CreateJsonResponse(
                    HttpStatusCode.InternalServerError,
                    new
                    {
                        error = new
                        {
                            message = "retry once"
                        }
                    });
                retryResponse.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
                return retryResponse;
            }

            return CreateJsonResponse(
                HttpStatusCode.OK,
                new
                {
                    text = "synthetic transcript"
                });
        }

        private static HttpResponseMessage CreateJsonResponse<T>(HttpStatusCode statusCode, T body)
        {
            ByteArrayContent content = new(JsonSerializer.SerializeToUtf8Bytes(body));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpResponseMessage(statusCode)
            {
                Content = content,
            };
        }
    }

    private sealed class ImmediateRetryPolicy() : ClientRetryPolicy(maxRetries: 1)
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount)
            => TimeSpan.Zero;
    }
}
