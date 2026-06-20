using System.ClientModel;
using System.Diagnostics;
using System.Text;
using AlleyCat.Core.Configuration;
using AlleyCat.Core.Logging;
using AlleyCat.Diagnostics;
using AlleyCat.UI;
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
    private const string ConfigLoadFailureNotification = "Speech transcription is unavailable. Please check the STT configuration.";

    private OpenAITranscriberSettings? _settings;
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

    /// <summary>
    /// When enabled, posts speech lifecycle debug notifications to the player UI.
    /// </summary>
    [Export]
    public bool DebugNotificationOutputEnabled
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void _Ready()
    {
        base._Ready();
        _logger = GameLoggerResolver.ResolveRequired<OpenAITranscriber>();

        try
        {
            _settings = OpenAITranscriberSettings.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load STT configuration from {ConfigPath}.", ConfigPath);
            _ = this.PostNotification(ConfigLoadFailureNotification);
            _settings = null;
        }
    }

    /// <inheritdoc />
    public override async Task<string> Transcribe(AudioStreamWav audioStream)
    {
        OpenAITranscriberSettings settings = _settings ?? OpenAITranscriberSettings.Load(ConfigPath);
        RecordedAudioData recordedAudio = CaptureRecordedAudio(audioStream);

        Stopwatch preparationStopwatch = AIPipelineDebugLog.StartTimer();
        using PreparedTranscriptionRequest request = await PrepareTranscriptionRequestAsync(recordedAudio, settings)
            .ConfigureAwait(false);
        if (AIPipelineDebugLog.IsEnabled)
        {
            await LogLatencyOnGodotThreadAsync("STT request prepared in", preparationStopwatch, $"model {settings.Model}")
                .ConfigureAwait(false);
        }

        Stopwatch backendStopwatch = AIPipelineDebugLog.StartTimer();
        AudioTranscription response = await request.Client
            .TranscribeAudioAsync(request.WavStream, "alleycat-recording.wav", request.Options)
            .ConfigureAwait(false);
        if (AIPipelineDebugLog.IsEnabled)
        {
            await LogLatencyOnGodotThreadAsync("STT backend returned in", backendStopwatch, $"model {settings.Model}")
                .ConfigureAwait(false);
        }

        return GetTranscriptionTextOrThrow(response);
    }

    /// <inheritdoc />
    protected override void OnRecordingStarted()
        => PostDebugNotification("Speech debug: Recording started.");

    /// <inheritdoc />
    protected override void OnRecordingStopped()
        => PostDebugNotification("Speech debug: Recording stopped.");

    /// <inheritdoc />
    protected override void OnTranscriptionCompleted(string text)
        => PostDebugNotification($"Speech debug: Transcription result: {FormatDebugTranscript(text)}");

    internal static byte[] CreateWaveFileBytes(AudioStreamWav audioStream)
    {
        using MemoryStream stream = CreateWaveFileStream(audioStream);
        return stream.ToArray();
    }

    internal static MemoryStream CreateWaveFileStream(AudioStreamWav audioStream)
    {
        RecordedAudioData recordedAudio = CaptureRecordedAudio(audioStream);
        return CreateWaveFileStream(recordedAudio.Data, recordedAudio.MixRate, recordedAudio.Stereo, recordedAudio.Format);
    }

    internal static Task<PreparedTranscriptionRequest> PrepareTranscriptionRequestAsync(
        RecordedAudioData recordedAudio,
        OpenAITranscriberSettings settings)
        => Task.Run(() => PrepareTranscriptionRequest(recordedAudio, settings));

    internal static byte[] CreateWaveFileBytes(
        byte[] data,
        int mixRate,
        bool stereo,
        AudioStreamWav.FormatEnum format)
    {
        using MemoryStream stream = CreateWaveFileStream(data, mixRate, stereo, format);
        return stream.ToArray();
    }

    internal static MemoryStream CreateWaveFileStream(
        byte[] data,
        int mixRate,
        bool stereo,
        AudioStreamWav.FormatEnum format)
    {
        MemoryStream stream = CreateWaveFileStream(data, mixRate, stereo, format, data.Length + 44);
        stream.Position = 0;
        return stream;
    }

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

    internal static RecordedAudioData CaptureRecordedAudio(AudioStreamWav audioStream)
        => new(audioStream.Data, audioStream.MixRate, audioStream.Stereo, audioStream.Format);

    private static MemoryStream CreateWaveFileStream(
        byte[] data,
        int mixRate,
        bool stereo,
        AudioStreamWav.FormatEnum format,
        int capacity)
    {
        if (data.Length == 0)
        {
            throw new InvalidOperationException("OpenAITranscriber requires non-empty microphone audio.");
        }

        if (format is not AudioStreamWav.FormatEnum.Format8Bits and not AudioStreamWav.FormatEnum.Format16Bits)
        {
            throw new InvalidOperationException(
                $"OpenAITranscriber only supports PCM WAV input. Got format '{format}'.");
        }

        int bitsPerSample = format == AudioStreamWav.FormatEnum.Format8Bits ? 8 : 16;
        short channelCount = (short)(stereo ? 2 : 1);
        int sampleRate = mixRate;
        int byteRate = sampleRate * channelCount * bitsPerSample / 8;
        short blockAlign = (short)(channelCount * bitsPerSample / 8);

        MemoryStream stream = new(capacity: capacity);
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write((short)bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream;
    }

    private static PreparedTranscriptionRequest PrepareTranscriptionRequest(
        RecordedAudioData recordedAudio,
        OpenAITranscriberSettings settings)
    {
        MemoryStream? wavStream = null;

        try
        {
            wavStream = CreateWaveFileStream(
                recordedAudio.Data,
                recordedAudio.MixRate,
                recordedAudio.Stereo,
                recordedAudio.Format);

            return new PreparedTranscriptionRequest(
                wavStream,
                settings.CreateAudioClient(),
                CreateTranscriptionOptions(settings));
        }
        catch
        {
            wavStream?.Dispose();
            throw;
        }
    }

    private void PostDebugNotification(string message)
    {
        if (!DebugNotificationOutputEnabled)
        {
            return;
        }

        _ = DispatchDeferredGodotActionAsync(() => _ = this.PostNotification(message));
    }

    private Task LogLatencyOnGodotThreadAsync(string stage, Stopwatch stopwatch, string detail)
        => DispatchDeferredGodotActionAsync(() => AIPipelineDebugLog.Latency(stage, stopwatch, detail));

    private static string FormatDebugTranscript(string text)
        => string.IsNullOrWhiteSpace(text) ? "<empty>" : text.Trim();

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
        {
            OpenAIClientOptions options = new()
            {
                Endpoint = CreateEndpointUri(),
            };

            if (TimeoutSeconds is int timeoutSeconds)
            {
                options.NetworkTimeout = TimeSpan.FromSeconds(timeoutSeconds);
            }

            return options;
        }

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

    internal readonly record struct RecordedAudioData(
        byte[] Data,
        int MixRate,
        bool Stereo,
        AudioStreamWav.FormatEnum Format);

    internal sealed class PreparedTranscriptionRequest(
        MemoryStream wavStream,
        AudioClient client,
        AudioTranscriptionOptions options) : IDisposable
    {
        public MemoryStream WavStream { get; } = wavStream;

        public AudioClient Client { get; } = client;

        public AudioTranscriptionOptions Options { get; } = options;

        public void Dispose() => WavStream.Dispose();
    }
}
