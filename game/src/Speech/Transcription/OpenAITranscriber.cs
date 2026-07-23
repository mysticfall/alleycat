using System.ClientModel;
using System.Diagnostics;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using Godot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// OpenAI-compatible speech transcriber backed by the official OpenAI .NET SDK.
/// </summary>
[GlobalClass]
public partial class OpenAITranscriber : Transcriber
{
    private const string ConfigSection = "STT";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;
    private const string DefaultModel = "whisper-1";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";

    private OpenAITranscriberSettings? _settings;
    private ILogger<OpenAITranscriber>? _logger;
    private bool _pipelineDebugLoggingEnabled;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible speech settings.
    /// </summary>
    [Export(PropertyHint.File, "*.json")]
    public string ConfigPath
    {
        get;
        set;
    } = DefaultConfigPath;

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        _logger = GameLoggerResolver.ResolveRequired<OpenAITranscriber>();
        _pipelineDebugLoggingEnabled = AIPipelineDebugLog.IsEnabled;

        try
        {
            _settings = OpenAITranscriberSettings.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load STT configuration from {ConfigPath}.", ConfigPath);
            _settings = null;
        }
    }

    /// <inheritdoc />
    public override async Task<string> Transcribe(RecordedAudioData recording)
    {
        OpenAITranscriberSettings settings = _settings
            ?? throw new InvalidOperationException("OpenAI transcription settings were not initialised on the Godot thread.");

        Stopwatch preparationStopwatch = AIPipelineDebugLog.StartTimer();
        using PreparedTranscriptionRequest request = PrepareTranscriptionRequest(recording, settings);
        if (_pipelineDebugLoggingEnabled)
        {
            await LogLatencyOnGodotThreadAsync("STT request prepared in", preparationStopwatch, $"model {settings.Model}")
                .ConfigureAwait(false);
        }

        Stopwatch backendStopwatch = AIPipelineDebugLog.StartTimer();
        AudioTranscription response = await request.Client
            .TranscribeAudioAsync(request.WavStream, "alleycat-recording.wav", request.Options)
            .ConfigureAwait(false);
        if (_pipelineDebugLoggingEnabled)
        {
            await LogLatencyOnGodotThreadAsync("STT backend returned in", backendStopwatch, $"model {settings.Model}")
                .ConfigureAwait(false);
        }

        return GetTranscriptionTextOrThrow(response);
    }

    internal static PreparedTranscriptionRequest PrepareTranscriptionRequest(
        RecordedAudioData recordedAudio,
        OpenAITranscriberSettings settings,
        ILoggerFactory? loggerFactory = null)
        => PrepareTranscriptionRequestCore(recordedAudio, settings, loggerFactory);

    internal static AudioTranscriptionOptions CreateTranscriptionOptions(OpenAITranscriberSettings settings)
    {
        AudioTranscriptionOptions options = new();

        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            options.Language = settings.Language;
        }

        if (!string.IsNullOrWhiteSpace(settings.Prompt))
        {
            options.Prompt = settings.Prompt;
        }

        if (settings.Temperature is float temperature)
        {
            options.Temperature = temperature;
        }

        return options;
    }

    internal static string GetTranscriptionTextOrThrow(AudioTranscription response)
        => string.IsNullOrWhiteSpace(response.Text)
            ? throw new InvalidOperationException(
                "OpenAI transcription response did not contain a non-empty 'text' field.")
            : response.Text.Trim();

    private static PreparedTranscriptionRequest PrepareTranscriptionRequestCore(
        RecordedAudioData recordedAudio,
        OpenAITranscriberSettings settings,
        ILoggerFactory? loggerFactory)
    {
        WaveFileStream? wavStream = null;

        try
        {
            if (recordedAudio.PCMData.IsEmpty)
            {
                throw new InvalidOperationException("OpenAITranscriber requires non-empty microphone audio.");
            }

            wavStream = new WaveFileStream(
                recordedAudio.PCMData,
                recordedAudio.SampleRate,
                recordedAudio.ChannelCount);

            return new PreparedTranscriptionRequest(
                wavStream,
                loggerFactory is null ? settings.CreateAudioClient() : settings.CreateAudioClient(loggerFactory),
                CreateTranscriptionOptions(settings));
        }
        catch
        {
            wavStream?.Dispose();
            throw;
        }
    }

    private Task LogLatencyOnGodotThreadAsync(string stage, Stopwatch stopwatch, string detail)
        => DispatchDeferredGodotActionAsync(() => AIPipelineDebugLog.Latency(stage, stopwatch, detail));

    internal sealed record OpenAITranscriberSettings(
        string Host,
        string? ApiKey,
        string Model,
        string? Language,
        string? Prompt,
        float? Temperature,
        int? TimeoutSeconds)
    {
        public string GetApiKeyOrDefault()
            => string.IsNullOrWhiteSpace(ApiKey) ? DefaultCompatibleBackendApiKey : ApiKey.Trim();

        public AudioClient CreateAudioClient()
            => new(Model, new ApiKeyCredential(GetApiKeyOrDefault()), CreateClientOptions());

        internal AudioClient CreateAudioClient(ILoggerFactory loggerFactory)
            => new(Model, new ApiKeyCredential(GetApiKeyOrDefault()), CreateClientOptions(loggerFactory));

        public Uri CreateEndpointUri()
        {
            string endpointUrl = Host.Trim();
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                throw new InvalidOperationException(
                    $"Missing '{ConfigSection}/Host' in OpenAI transcriber config '{ConfigPathDescription}'.");
            }

            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out Uri? endpointUri))
            {
                throw new InvalidOperationException(
                    $"Config key '{ConfigSection}/Host' must be a valid absolute endpoint URL. Got '{endpointUrl}'.");
            }

            _ = endpointUri.AbsolutePath.Length == 0
                || string.Equals(endpointUri.AbsolutePath, "/", StringComparison.Ordinal)
                ? throw new InvalidOperationException(
                    $"Config key '{ConfigSection}/Host' must include the API base path (for example 'https://api.openai.com/v1'). Got '{endpointUrl}'.")
                : 0;

            return endpointUri;
        }

        private OpenAIClientOptions CreateClientOptions()
            => OpenAIClientOptionsFactory.Create(CreateEndpointUri(), TimeoutSeconds);

        private OpenAIClientOptions CreateClientOptions(ILoggerFactory loggerFactory)
            => OpenAIClientOptionsFactory.Create(CreateEndpointUri(), TimeoutSeconds, loggerFactory);

        private string ConfigPathDescription
        {
            get;
            init;
        } = DefaultConfigPath;

        public static OpenAITranscriberSettings Load(string configPath)
            => Load(LoadConfiguration(configPath), configPath);

        internal static OpenAITranscriberSettings Load(STTOptions options, string configPathDescription = DefaultConfigPath)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new OpenAITranscriberSettings(
                Clean(options.Host) ?? string.Empty,
                Clean(options.ApiKey),
                Clean(options.Model) ?? DefaultModel,
                Clean(options.Language),
                Clean(options.Prompt),
                options.Temperature,
                options.Timeout)
            {
                ConfigPathDescription = configPathDescription,
            };
        }

        internal static OpenAITranscriberSettings Load(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => Load(LoadConfiguration(configPath, defaultConfigurationLoader, customConfigurationLoader), configPath);

        internal static OpenAITranscriberSettings Load(IConfiguration configuration, string configPathDescription)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            STTOptions options = new();
            configuration.GetSection(ConfigSection).Bind(options);
            return Load(options, configPathDescription);
        }

        private static IConfiguration LoadConfiguration(string configPath)
            => LoadConfiguration(
                configPath,
                ResolveDefaultConfiguration,
                path => GameConfiguration.BuildFile(new GodotPathResolver(), path));

        private static IConfiguration ResolveDefaultConfiguration()
            => Game.Instance.GetRequiredService<IConfiguration>();

        private static IConfiguration LoadConfiguration(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => string.Equals(configPath, DefaultConfigPath, StringComparison.Ordinal)
                ? defaultConfigurationLoader()
                : customConfigurationLoader(configPath);

        private static string? Clean(string? value)
        {
            string? text = value?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

    }

    internal sealed class PreparedTranscriptionRequest(
        Stream wavStream,
        AudioClient client,
        AudioTranscriptionOptions options) : IDisposable
    {
        public Stream WavStream { get; } = wavStream;

        public AudioClient Client { get; } = client;

        public AudioTranscriptionOptions Options { get; } = options;

        public void Dispose() => WavStream.Dispose();
    }
}
