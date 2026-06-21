using AlleyCat.Core.Configuration;
using AlleyCat.Speech.Transcription;
using Godot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
    /// Recorded PCM data must be wrapped into a valid RIFF/WAVE payload for upload.
    /// </summary>
    [Fact]
    public void CreateWaveFileBytes_Pcm16Audio_WritesWaveHeader()
    {
        byte[] bytes = OpenAITranscriber.CreateWaveFileBytes(
            data: [0x34, 0x12, 0x78, 0x56],
            mixRate: 16000,
            stereo: false,
            format: AudioStreamWav.FormatEnum.Format16Bits);

        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal((byte)'W', bytes[8]);
        Assert.Equal((byte)'A', bytes[9]);
        Assert.Equal((byte)'V', bytes[10]);
        Assert.Equal((byte)'E', bytes[11]);
        Assert.Equal(48, bytes.Length);
        Assert.Equal(0x34, bytes[44]);
        Assert.Equal(0x12, bytes[45]);
        Assert.Equal(0x78, bytes[46]);
        Assert.Equal(0x56, bytes[47]);
    }

    /// <summary>
    /// Stream uploads must receive a rewound in-memory WAV stream.
    /// </summary>
    [Fact]
    public void CreateWaveFileStream_Pcm16Audio_ReturnsReadableRewoundWaveStream()
    {
        using MemoryStream stream = OpenAITranscriber.CreateWaveFileStream(
            data: [0x34, 0x12, 0x78, 0x56],
            mixRate: 16000,
            stereo: false,
            format: AudioStreamWav.FormatEnum.Format16Bits);

        byte[] bytes = stream.ToArray();

        Assert.True(stream.CanRead);
        Assert.Equal(0, stream.Position);
        Assert.Equal(48, stream.Length);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal(0x34, bytes[44]);
        Assert.Equal(0x12, bytes[45]);
        Assert.Equal(0x78, bytes[46]);
        Assert.Equal(0x56, bytes[47]);
    }

    /// <summary>
    /// OpenAI request preparation must produce a rewound WAV stream and configured SDK options off the caller path.
    /// </summary>
    [Fact]
    public async Task PrepareTranscriptionRequestAsync_ConfiguredAudioAndSettings_CreatesUploadRequest()
    {
        OpenAITranscriber.RecordedAudioData recordedAudio = new(
            Data: [0x34, 0x12, 0x78, 0x56],
            MixRate: 16000,
            Stereo: false,
            Format: AudioStreamWav.FormatEnum.Format16Bits);
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
            await OpenAITranscriber.PrepareTranscriptionRequestAsync(recordedAudio, settings, loggerFactory);
        byte[] bytes = request.WavStream.ToArray();

        Assert.NotNull(request.Client);
        Assert.Equal(0, request.WavStream.Position);
        Assert.Equal(48, request.WavStream.Length);
        Assert.Equal((byte)'R', bytes[0]);
        Assert.Equal((byte)'I', bytes[1]);
        Assert.Equal((byte)'F', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
        Assert.Equal("en", request.Options.Language);
        Assert.Equal("Transcribe clearly.", request.Options.Prompt);
        Assert.Equal(0.35f, request.Options.Temperature);
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
}
