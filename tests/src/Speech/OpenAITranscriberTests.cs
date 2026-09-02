using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using AlleyCat.Speech.Transcription;
using AlleyCat.Tests.Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the consolidated OpenAI-compatible REST speech transcriber.
/// </summary>
[Collection(PipelineDiagnosticsCollection.Name)]
public sealed class OpenAITranscriberTests : IDisposable
{
    private readonly CapturingLogProvider _logProvider = new();
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>
    /// Installs a capturing logger factory so the shared REST boundary can emit pipeline diagnostics in tests.
    /// </summary>
    public OpenAITranscriberTests()
    {
        _loggerFactory = new CapturingLoggerFactory(_logProvider);
        PipelineDebugLog.SetLoggerFactoryForTesting(_loggerFactory);
    }

    /// <summary>
    /// Clears the test logger override.
    /// </summary>
    public void Dispose()
    {
        PipelineDebugLog.SetLoggerFactoryForTesting(null);
        _loggerFactory.Dispose();
    }
    /// <summary>
    /// OpenAI-compatible backends without auth must still produce an SDK-safe credential value.
    /// </summary>
    [Fact]
    public void GetApiKeyOrDefault_ApiKeyMissing_UsesDummyCompatibleBackendKey()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings();

        string apiKey = settings.GetApiKeyOrDefault();

        Assert.Equal("unused-api-key", apiKey);
    }

    /// <summary>
    /// Full endpoint URLs must be preserved as configured.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_FullEndpointConfig_PreservesConfiguredUri()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(host: "https://api.openai.com/v1");

        Uri endpoint = settings.CreateEndpointUri();

        Assert.Equal("https://api.openai.com/v1", endpoint.ToString().TrimEnd('/'));
    }

    /// <summary>
    /// Host-only values must fail fast so config stays aligned with the full endpoint URL contract.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_HostOnlyConfig_Throws()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(host: "api.openai.com");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(settings.CreateEndpointUri);

        Assert.Contains("must be a valid absolute endpoint URL", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Endpoint URLs without an API base path must fail fast so compatible backends remain explicit.
    /// </summary>
    [Fact]
    public void CreateEndpointUri_EndpointWithoutPath_Throws()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(host: "https://api.openai.com");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(settings.CreateEndpointUri);

        Assert.Contains("must include the API base path", ex.Message, StringComparison.Ordinal);
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
                ["Hotwords"] = "  felis catus  ",
            },
        };

        IConfiguration configuration = CreateConfiguration(baseSections, overrideSections);

        var settings =
            OpenAITranscriber.OpenAITranscriberSettings.Load(configuration, "merged-test-config");

        Assert.Equal("https://base.example/v1", settings.Host);
        Assert.Equal("whisper-1", settings.Model);
        Assert.Equal("Base prompt", settings.Prompt);
        Assert.Equal("sk-user", settings.ApiKey);
        Assert.Equal("felis catus", settings.Hotwords);
    }

    /// <summary>
    /// Blank or missing STT hint values must bind to null so the request omits them entirely.
    /// </summary>
    [Fact]
    public void Load_BlankOrMissingHints_BindToNull()
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> baseSections = new(StringComparer.Ordinal)
        {
            ["STT"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "https://base.example/v1",
                ["Hotwords"] = " \t ",
            },
        };

        var settings = OpenAITranscriber.OpenAITranscriberSettings.Load(
            CreateConfiguration(baseSections),
            "blank-hints-config");

        Assert.Null(settings.Hotwords);
        Assert.Null(settings.Prompt);
        Assert.Null(settings.Language);
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
                ["Hotwords"] = "alleycat",
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
        Assert.Equal("alleycat", settings.Hotwords);
    }

    /// <summary>
    /// Custom config paths must load only the requested file without implicit user override merging.
    /// </summary>
    [Fact]
    public void Load_CustomConfigPath_UsesDirectConfigRoutingWithoutImplicitMerge()
    {
        const string customConfigPath = "res://custom-stt.yaml";
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
        Assert.Null(settings.Hotwords);
    }

    /// <summary>
    /// The multipart body must carry the file, model, every configured optional field — including the compatible
    /// backend's native hotwords — and the plain-JSON response format, while the trimmed text settles the request.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_WithConfiguredFields_SendsNativeHotwordsAndTrimsText()
    {
        byte[] pcmData = [0x34, 0x12, 0x78, 0x56];
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(
            language: "en",
            prompt: "Transcribe clearly.",
            hotwords: "felis catus");
        using CapturingTranscriptionHandler handler = new();
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);

        string text = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            new RecordedAudioData(pcmData, sampleRate: 16000, channelCount: 1),
            settings,
            CancellationToken.None,
            OpenAITranscriber.ManualDispatchSource);

        Assert.Equal("synthetic transcript", text);
        byte[] body = Assert.Single(handler.RequestBodies);
        Assert.StartsWith("multipart/form-data; boundary=", Assert.Single(handler.ContentTypes));
        Assert.Contains("name=model\r\n\r\nwhisper-1\r\n"u8, body);
        Assert.Contains("name=language\r\n\r\nen\r\n"u8, body);
        Assert.Contains("name=prompt\r\n\r\nTranscribe clearly.\r\n"u8, body);
        Assert.Contains("name=hotwords\r\n\r\nfelis catus\r\n"u8, body);
        Assert.Contains("name=response_format\r\n\r\njson\r\n"u8, body);
        Assert.Equal(CreateExpectedWaveBytes(pcmData), ExtractFileContent(body));
    }

    /// <summary>
    /// Blank optional fields must be omitted from the multipart body entirely.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_WithoutOptionalFields_OmitsThemFromBody()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings();
        using CapturingTranscriptionHandler handler = new();
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);

        string text = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            new RecordedAudioData([0x01, 0x00], sampleRate: 16000, channelCount: 1),
            settings,
            CancellationToken.None,
            OpenAITranscriber.ManualDispatchSource);

        Assert.Equal("synthetic transcript", text);
        byte[] body = Assert.Single(handler.RequestBodies);
        Assert.Contains("name=model\r\n\r\nwhisper-1\r\n"u8, body);
        Assert.Contains("name=response_format\r\n\r\njson\r\n"u8, body);
        Assert.DoesNotContain("name=language"u8, body);
        Assert.DoesNotContain("name=prompt"u8, body);
        Assert.DoesNotContain("name=hotwords"u8, body);
    }

    /// <summary>
    /// A response without a string <c>text</c> field must fail clearly instead of surfacing a blank transcript.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_WhenResponseLacksTextField_Throws()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings();
        using CapturingTranscriptionHandler handler = new()
        {
            RespondWith = /*lang=json,strict*/ """{"duration": 1.0}""",
        };
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => OpenAITranscriber.TranscribeViaMultipartAsync(
                client,
                new RecordedAudioData([0x01, 0x00], sampleRate: 16000, channelCount: 1),
                settings,
                CancellationToken.None,
                OpenAITranscriber.ManualDispatchSource));

        Assert.Contains("did not contain a string 'text' field", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SDK retry policy must replay the seekable WAV body: a retried request carries a byte-identical multipart
    /// body, including its native hotwords, while the logical dispatch marker still emits exactly once.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_OnTransientFailure_RetriesWithIdenticalMultipartBody()
    {
        byte[] pcmData = [0x34, 0x12, 0x78, 0x56, 0xbc, 0x9a];
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(hotwords: "felis catus");
        using CapturingTranscriptionHandler handler = new()
        {
            FailFirstAttemptWithRetryAfter = true,
        };
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, new ImmediateRetryPolicy());

        string text = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            new RecordedAudioData(pcmData, sampleRate: 16000, channelCount: 1),
            settings,
            CancellationToken.None,
            OpenAITranscriber.ManualDispatchSource);

        Assert.Equal("synthetic transcript", text);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(handler.RequestBodies[0], handler.RequestBodies[1]);
        Assert.All(handler.ContentTypes, value => Assert.StartsWith("multipart/form-data; boundary=", value));
        Assert.All(handler.RequestBodies, body => Assert.Equal(CreateExpectedWaveBytes(pcmData), ExtractFileContent(body)));
        Assert.All(handler.RequestBodies, body => Assert.Contains("name=hotwords\r\n\r\nfelis catus\r\n"u8, body));

        // Two HTTP attempts settle one logical request, so the dispatch marker fires exactly once despite the retry.
        CapturedLogEntry dispatchEntry = Assert.Single(_logProvider.Entries);
        Assert.Equal("AlleyCat.Pipeline.STT", dispatchEntry.CategoryName);
        Assert.Equal(LogLevel.Debug, dispatchEntry.Level);
    }

    /// <summary>
    /// Cancellation must abandon the in-flight request through the shared request path.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings();
        using CapturingTranscriptionHandler handler = new()
        {
            HoldUntilCancelled = true,
        };
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);
        using CancellationTokenSource cancellation = new();

        Task<string> request = OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            new RecordedAudioData([0x01, 0x00], sampleRate: 16000, channelCount: 1),
            settings,
            cancellation.Token,
            OpenAITranscriber.ManualDispatchSource);
        await handler.RequestEntered.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await request);
    }

    /// <summary>
    /// Manual and automatic requests share one transport: successive requests through the same client and settings —
    /// one without and one with a linked cancellation token — carry boundary-normalised identical multipart bodies
    /// to one endpoint, and each logical request emits its own dispatch marker with the correct source mode.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_ManualAndAutomaticRequests_ShareOneTransport()
    {
        byte[] pcmData = [0x34, 0x12, 0x78, 0x56];
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(hotwords: "alleycat");
        using CapturingTranscriptionHandler handler = new();
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);
        var recording = new RecordedAudioData(pcmData, sampleRate: 16000, channelCount: 1);

        string manualText = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            recording,
            settings,
            CancellationToken.None,
            OpenAITranscriber.ManualDispatchSource);
        using var automaticCancellation = new CancellationTokenSource();
        string automaticText = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            recording,
            settings,
            automaticCancellation.Token,
            OpenAITranscriber.AutomaticDispatchSource);

        Assert.Equal(manualText, automaticText);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal(
            NormaliseBoundary(handler.RequestBodies[0], handler.ContentTypes[0]),
            NormaliseBoundary(handler.RequestBodies[1], handler.ContentTypes[1]));
        Assert.All(
            handler.RequestUris,
            uri => Assert.Equal("https://unit.test/v1/audio/transcriptions", uri.ToString()));

        // One dispatch marker per logical request, labelled by the invoking source mode.
        Assert.Equal(2, _logProvider.Entries.Count);
        Assert.All(_logProvider.Entries, entry => Assert.Equal("AlleyCat.Pipeline.STT", entry.CategoryName));
        Assert.Equal(
            [OpenAITranscriber.ManualDispatchSource, OpenAITranscriber.AutomaticDispatchSource],
            _logProvider.Entries.Select(entry => Assert.IsType<SttDispatchEntry>(entry.State).SourceMode));
    }

    /// <summary>
    /// Each logical transcription request emits exactly one debug-level dispatch marker under the STT child
    /// category, carrying only non-sensitive operational metadata — the source mode, the frame-derived duration,
    /// and the PCM byte count.
    /// </summary>
    [Fact]
    public async Task TranscribeViaMultipartAsync_EmitsExactlyOneSttDispatchMarkerWithOperationalMetadataOnly()
    {
        // 37,600 mono frames at 16 kHz = 2.35 seconds of PCM, 75,200 bytes.
        byte[] pcmData = new byte[75200];
        OpenAITranscriber.OpenAITranscriberSettings settings = CreateSettings(
            prompt: "Transcribe clearly.",
            hotwords: "felis catus");
        using CapturingTranscriptionHandler handler = new();
        using HttpClient httpClient = new(handler);
        AudioClient client = CreateAudioClient(httpClient, retryPolicy: null);

        string text = await OpenAITranscriber.TranscribeViaMultipartAsync(
            client,
            new RecordedAudioData(pcmData, sampleRate: 16000, channelCount: 1),
            settings,
            CancellationToken.None,
            OpenAITranscriber.AutomaticDispatchSource);

        Assert.Equal("synthetic transcript", text);
        CapturedLogEntry entry = Assert.Single(_logProvider.Entries);
        Assert.Equal("AlleyCat.Pipeline.STT", entry.CategoryName);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Equal("Dispatching automatic audio to STT (2.35 seconds, 75200 PCM bytes)", entry.Message);

        SttDispatchEntry dispatchEntry = Assert.IsType<SttDispatchEntry>(entry.State);
        Assert.Equal(OpenAITranscriber.AutomaticDispatchSource, dispatchEntry.SourceMode);
        Assert.Equal(TimeSpan.FromSeconds(2.35), dispatchEntry.Duration);
        Assert.Equal(75200, dispatchEntry.PcmByteCount);

        // The marker never carries transcript, prompt, hotword, model, credential, endpoint, or request-body content.
        Assert.DoesNotContain("synthetic transcript", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Transcribe clearly.", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("felis catus", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("whisper-1", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unit.test", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("unit-test-key", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("multipart", entry.Message, StringComparison.Ordinal);
    }

    private static OpenAITranscriber.OpenAITranscriberSettings CreateSettings(
        string host = "https://api.openai.com/v1",
        string? language = null,
        string? prompt = null,
        string? hotwords = null)
        => new(
            Host: host,
            ApiKey: string.Empty,
            Model: "whisper-1",
            Language: language,
            Prompt: prompt,
            Hotwords: hotwords,
            TimeoutSeconds: null);

    private static AudioClient CreateAudioClient(HttpClient httpClient, ClientRetryPolicy? retryPolicy)
    {
        OpenAIClientOptions clientOptions = new()
        {
            Endpoint = new Uri("https://unit.test/v1"),
            Transport = new HttpClientPipelineTransport(httpClient),
        };
        if (retryPolicy is not null)
        {
            clientOptions.RetryPolicy = retryPolicy;
        }

        return new AudioClient("whisper-1", new ApiKeyCredential("unit-test-key"), clientOptions);
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

    private static byte[] CreateExpectedWaveBytes(byte[] pcmData)
    {
        using WaveFileStream expectedStream = new(pcmData, sampleRate: 16000, channelCount: 1);
        byte[] expectedWave = new byte[expectedStream.Length];
        Assert.Equal(expectedWave.Length, expectedStream.Read(expectedWave));
        return expectedWave;
    }

    /// <summary>Replaces every generated boundary token with a fixed marker so independently generated bodies compare.</summary>
    private static string NormaliseBoundary(byte[] body, string contentType)
    {
        string boundary = contentType.Split('=', 2)[1].Trim('"');
        return System.Text.Encoding.UTF8.GetString(body).Replace(boundary, "boundary", StringComparison.Ordinal);
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

    /// <summary>Captures every entry emitted through the pipeline diagnostics helper for dispatch-marker assertions.</summary>
    private sealed class CapturingLogProvider : ILoggerProvider
    {
        private readonly Lock _lock = new();
        private readonly List<CapturedLogEntry> _entries = [];

        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (_lock)
                {
                    return [.. _entries];
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string categoryName, CapturingLogProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel is not LogLevel.None;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (provider._lock)
                {
                    provider._entries.Add(new CapturedLogEntry(categoryName, logLevel, formatter(state, exception), state));
                }
            }
        }
    }

    private sealed record CapturedLogEntry(string CategoryName, LogLevel Level, string Message, object? State);

    private sealed class CapturingLoggerFactory(CapturingLogProvider provider) : ILoggerFactory
    {
        public void AddProvider(ILoggerProvider loggerProvider)
        {
        }

        public ILogger CreateLogger(string categoryName) => provider.CreateLogger(categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingTranscriptionHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> RequestBodies { get; } = [];

        public List<string> ContentTypes { get; } = [];

        public List<Uri> RequestUris { get; } = [];

        public bool FailFirstAttemptWithRetryAfter
        {
            get;
            set;
        }

        public bool HoldUntilCancelled
        {
            get;
            set;
        }

        public string RespondWith
        {
            get;
            set;
        } = /*lang=json,strict*/ """{"text": "  synthetic transcript  "}""";

        public Task RequestEntered => _requestEntered.Task;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://unit.test/v1/audio/transcriptions", request.RequestUri?.ToString());
            Assert.NotNull(request.Content);
            RequestUris.Add(request.RequestUri!);
            ContentTypes.Add(request.Content.Headers.ContentType?.ToString() ?? string.Empty);
            RequestBodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            _ = _requestEntered.TrySetResult();

            if (HoldUntilCancelled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (FailFirstAttemptWithRetryAfter && RequestBodies.Count == 1)
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

            return CreateRawJsonResponse(HttpStatusCode.OK, RespondWith);
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

        private static HttpResponseMessage CreateRawJsonResponse(HttpStatusCode statusCode, string json)
        {
            StringContent content = new(json, System.Text.Encoding.UTF8, "application/json");
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
