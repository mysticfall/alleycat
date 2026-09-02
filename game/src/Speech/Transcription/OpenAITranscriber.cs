using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text.Json;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using Godot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// OpenAI-compatible speech transcriber backed by the official OpenAI .NET SDK, sending one multipart REST request
/// per recording so native backends receive every configured hint — including WhisperLive's <c>hotwords</c>.
/// </summary>
/// <remarks>
/// Manual push-to-talk and automatic utterance finalisation share the same request path: both retain whole-utterance
/// audio and settle through one authoritative REST transcription.
/// </remarks>
[GlobalClass]
public partial class OpenAITranscriber : Transcriber
{
    private const string ConfigSection = "STT";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;
    private const string DefaultModel = "whisper-1";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";
    internal const string ManualDispatchSource = "manual";
    internal const string AutomaticDispatchSource = "automatic";

    private OpenAITranscriberSettings? _settings;
    private AudioClient? _audioClient;
    private ILogger<OpenAITranscriber>? _logger;

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

        try
        {
            _settings = OpenAITranscriberSettings.Load(ConfigPath);
            _audioClient = _settings.CreateAudioClient(GameLoggerResolver.ResolveFactoryRequired());
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load STT configuration from {ConfigPath}.", ConfigPath);
            _settings = null;
            _audioClient = null;
        }
    }

    /// <inheritdoc />
    public override Task<string> Transcribe(RecordedAudioData recording)
        => TranscribeAsync(recording, CancellationToken.None, ManualDispatchSource);

    /// <inheritdoc />
    protected override Task<string> FinaliseAutomaticUtteranceAsync(RecordedAudioData recording, CancellationToken cancellationToken)
        => TranscribeAsync(recording, cancellationToken, AutomaticDispatchSource);

    private async Task<string> TranscribeAsync(
        RecordedAudioData recording,
        CancellationToken cancellationToken,
        string dispatchSourceMode)
    {
        OpenAITranscriberSettings settings = _settings
            ?? throw new InvalidOperationException("OpenAI transcription settings were not initialised on the Godot thread.");
        AudioClient audioClient = _audioClient
            ?? throw new InvalidOperationException("OpenAI transcription client was not initialised on the Godot thread.");

        Stopwatch backendStopwatch = PipelineDebugLog.StartTimer();
        string text = await TranscribeViaMultipartAsync(audioClient, recording, settings, cancellationToken, dispatchSourceMode)
            .ConfigureAwait(false);
        if (PipelineDebugLog.IsEnabled)
        {
            await LogOnlyLatencyOnGodotThreadAsync("STT backend returned in", backendStopwatch, $"model {settings.Model}")
                .ConfigureAwait(false);
        }

        return text;
    }

    /// <summary>
    /// Sends one multipart REST transcription request through the SDK's authenticated pipeline — whose retry policy
    /// replays the seekable WAV body — and trims the plain-JSON <c>text</c> field from the response. Immediately
    /// before the single SDK await, emits the one notification-eligible STT dispatch marker for the logical request.
    /// </summary>
#pragma warning disable SCME0004
    internal static async Task<string> TranscribeViaMultipartAsync(
        AudioClient audioClient,
        RecordedAudioData recording,
        OpenAITranscriberSettings settings,
        CancellationToken cancellationToken,
        string dispatchSourceMode)
    {
        ArgumentNullException.ThrowIfNull(audioClient);
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(settings);
        if (recording.PCMData.IsEmpty)
        {
            throw new InvalidOperationException("OpenAITranscriber requires non-empty microphone audio.");
        }

        using var wav = new WaveFileStream(recording.PCMData, recording.SampleRate, recording.ChannelCount);
        using MultiPartFormContent content = CreateTranscriptionContent(wav, settings);

        // Emitted once per logical request before the single SDK await, so the retry policy's body replays cannot
        // duplicate it; the marker's state carries only non-sensitive operational metadata.
        SttDispatchLog.Dispatch(
            dispatchSourceMode,
            TimeSpan.FromSeconds((double)recording.FrameCount / recording.SampleRate),
            recording.PCMData.Length);

        ClientResult response = await audioClient.TranscribeAudioAsync(
            content,
            content.MediaType,
            new RequestOptions { CancellationToken = cancellationToken }).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response.GetRawResponse().Content.ToString());
        return document.RootElement.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()?.Trim() ?? string.Empty
            : throw new InvalidOperationException("OpenAI transcription response did not contain a string 'text' field.");
    }

    /// <summary>
    /// Builds the multipart transcription body: <c>file</c>, <c>model</c>, optional <c>language</c>, optional
    /// <c>prompt</c>, optional <c>hotwords</c>, and a plain-JSON <c>response_format</c>.
    /// </summary>
    internal static MultiPartFormContent CreateTranscriptionContent(Stream wavStream, OpenAITranscriberSettings settings)
    {
        ArgumentNullException.ThrowIfNull(wavStream);
        ArgumentNullException.ThrowIfNull(settings);

        // AudioTranscriptionOptions cannot encode every compatible backend's native hotwords field. The SDK's
        // protocol operation still provides its authenticated pipeline, logging, timeout, and retry behaviour while
        // this content supplies the request body.
        var content = new MultiPartFormContent();
        var file = new FileBinaryContent(wavStream, "audio/wav")
        {
            Filename = "alleycat-recording.wav",
        };
        content.Add("file", file);
        content.Add("model", BinaryData.FromString(settings.Model.Trim()));
        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            content.Add("language", BinaryData.FromString(settings.Language.Trim()));
        }

        if (settings.Prompt is not null)
        {
            content.Add("prompt", BinaryData.FromString(settings.Prompt));
        }

        if (settings.Hotwords is not null)
        {
            content.Add("hotwords", BinaryData.FromString(settings.Hotwords));
        }

        // The explicit plain JSON result deliberately avoids the SDK's SSE helper, which omits hotwords for this backend.
        content.Add("response_format", BinaryData.FromString("json"));
        return content;
    }
#pragma warning restore SCME0004

    private Task LogOnlyLatencyOnGodotThreadAsync(string stage, Stopwatch stopwatch, string detail)
        => DispatchDeferredGodotActionAsync(() => PipelineDebugLog.LogOnlyLatency(stage, stopwatch, detail));

    internal sealed record OpenAITranscriberSettings(
        string Host,
        string? ApiKey,
        string Model,
        string? Language,
        string? Prompt,
        string? Hotwords,
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
                Clean(options.Hotwords),
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
}
