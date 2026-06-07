using System.ClientModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AlleyCat.Core;
using AlleyCat.Diagnostics;
using AlleyCat.UI;
using Godot;
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
    private const string DefaultConfigPath = ConfigProvider.DefaultBaseConfigPath;
    private const string DefaultModel = "whisper-1";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";
    private const string ConfigLoadFailureNotification = "Speech transcription is unavailable. Please check the STT configuration.";

    private OpenAITranscriberSettings? _settings;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible speech settings.
    /// </summary>
    [Export(PropertyHint.File, "*.cfg")]
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

        try
        {
            _settings = OpenAITranscriberSettings.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            _ = this.PostNotification(ConfigLoadFailureNotification);
            _settings = null;
        }
    }

    /// <inheritdoc />
    public override async Task<string> Transcribe(AudioStreamWav audioStream)
    {
        OpenAITranscriberSettings settings = _settings ?? OpenAITranscriberSettings.Load(ConfigPath);

        Stopwatch preparationStopwatch = AIPipelineDebugLog.StartTimer();
        using MemoryStream wavStream = CreateWaveFileStream(audioStream);
        AudioClient client = settings.CreateAudioClient();
        AudioTranscriptionOptions options = CreateTranscriptionOptions(settings);
        AIPipelineDebugLog.Latency("STT request prepared in", preparationStopwatch, $"model {settings.Model}");

        Stopwatch backendStopwatch = AIPipelineDebugLog.StartTimer();
        AudioTranscription response = await client.TranscribeAudioAsync(wavStream, "alleycat-recording.wav", options);
        AIPipelineDebugLog.Latency("STT backend returned in", backendStopwatch, $"model {settings.Model}");
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
        return CreateWaveFileStream(
            audioStream.Data,
            audioStream.MixRate,
            audioStream.Stereo,
            audioStream.Format);
    }

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

    private void PostDebugNotification(string message)
    {
        if (!DebugNotificationOutputEnabled)
        {
            return;
        }

        _ = DispatchDeferredGodotActionAsync(() => _ = this.PostNotification(message));
    }

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
            => Load(configPath, () => ConfigProvider.LoadMerged(), ConfigProvider.Load);

        internal static OpenAITranscriberSettings Load(
            string configPath,
            Func<ConfigProvider> mergedConfigLoader,
            Func<string, ConfigProvider> singleConfigLoader)
            => Load(LoadConfigProvider(configPath, mergedConfigLoader, singleConfigLoader), configPath);

        internal static OpenAITranscriberSettings Load(ConfigProvider configProvider, string configPathDescription)
        {
            return new OpenAITranscriberSettings(
                GetString(configProvider, nameof(Host)),
                GetOptionalString(configProvider, nameof(ApiKey)),
                GetOptionalString(configProvider, nameof(Model)) ?? DefaultModel,
                GetOptionalString(configProvider, nameof(Language)),
                GetOptionalString(configProvider, nameof(Prompt)),
                GetOptionalFloat(configProvider, nameof(Temperature)),
                GetOptionalInt(configProvider, "Timeout"))
            {
                ConfigPathDescription = configPathDescription,
            };
        }

        private static ConfigProvider LoadConfigProvider(
            string configPath,
            Func<ConfigProvider> mergedConfigLoader,
            Func<string, ConfigProvider> singleConfigLoader)
            => string.Equals(configPath, DefaultConfigPath, StringComparison.Ordinal)
                ? mergedConfigLoader()
                : singleConfigLoader(configPath);

        private static string GetString(ConfigProvider configProvider, string key)
            => GetOptionalString(configProvider, key) ?? string.Empty;

        private static string? GetOptionalString(ConfigProvider configProvider, string key)
        {
            string? text = configProvider.GetValue(ConfigSection, key)?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static float? GetOptionalFloat(ConfigProvider configProvider, string key)
        {
            string? text = configProvider.GetValue(ConfigSection, key)?.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"Config key '{ConfigSection}/{key}' must be a valid float. Got '{text}'.");
        }

        private static int? GetOptionalInt(ConfigProvider configProvider, string key)
        {
            string? text = configProvider.GetValue(ConfigSection, key)?.Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"Config key '{ConfigSection}/{key}' must be a valid integer. Got '{text}'.");
        }
    }
}
