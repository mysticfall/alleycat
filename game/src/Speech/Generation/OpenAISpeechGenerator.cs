using System.ClientModel;
using System.Globalization;
using AlleyCat.Core;
using AlleyCat.UI;
using Godot;
using OpenAI;
using OpenAI.Audio;

namespace AlleyCat.Speech.Generation;

/// <summary>
/// OpenAI-compatible speech generator backed by the official OpenAI .NET SDK.
/// </summary>
[GlobalClass]
public partial class OpenAISpeechGenerator : SpeechGenerator
{
    private const string ConfigSection = "TTS";
    private const string DefaultConfigPath = ConfigProvider.DefaultBaseConfigPath;
    private const string DefaultModel = "tts-1";
    private const string DefaultVoice = "alloy";
    private const string DefaultFormat = "wav";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";
    private const string ConfigLoadFailureNotification = "Speech generation is unavailable. Please check the TTS configuration.";

    private OpenAISpeechGeneratorSettings? _settings;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible speech-generation settings.
    /// </summary>
    [Export(PropertyHint.File, "*.cfg")]
    public string ConfigPath
    {
        get;
        set;
    } = DefaultConfigPath;

    /// <summary>
    /// Optional per-node voice override. When unset, the configured backend voice is used.
    /// </summary>
    [Export]
    public string VoiceOverride
    {
        get;
        set;
    } = string.Empty;

    /// <inheritdoc />
    public override void _Ready()
    {
        try
        {
            _settings = OpenAISpeechGeneratorSettings.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            GD.PushError(ex.ToString());
            _ = this.PostNotification(ConfigLoadFailureNotification);
            _settings = null;
        }
    }

    /// <inheritdoc />
    protected override async Task<byte[]> GenerateCore(string text, string? instruction = null)
    {
        OpenAISpeechGeneratorSettings settings = _settings ?? OpenAISpeechGeneratorSettings.Load(ConfigPath);
        AudioClient client = settings.CreateAudioClient();
        SpeechGenerationOptions options = CreateSpeechGenerationOptions(settings, instruction);
        BinaryData audio = await client.GenerateSpeechAsync(text, settings.GetVoice(VoiceOverride), options);
        return audio.ToArray();
    }

    internal static SpeechGenerationOptions CreateSpeechGenerationOptions(
        OpenAISpeechGeneratorSettings settings,
        string? instruction)
    {
        SpeechGenerationOptions options = new()
        {
            ResponseFormat = settings.GetFormat(),
        };

        if (settings.SpeedRatio is float speedRatio)
        {
            options.SpeedRatio = speedRatio;
        }

        // OpenAI .NET SDK 2.10 exposes no separate non-spoken instruction/prompt field for TTS requests.
        // Preserve the method parameter for spec compatibility and future backends, but do not inject the
        // instruction into the spoken input text because that would cause the instruction itself to be read aloud.
        _ = instruction;

        return options;
    }

    internal sealed record OpenAISpeechGeneratorSettings(
        string Host,
        string? ApiKey,
        string Model,
        string Voice,
        string Format,
        float? SpeedRatio,
        int? TimeoutSeconds)
    {
        public string GetApiKeyOrDefault()
            => string.IsNullOrWhiteSpace(ApiKey) ? DefaultCompatibleBackendApiKey : ApiKey.Trim();

        public AudioClient CreateAudioClient()
            => new(Model, new ApiKeyCredential(GetApiKeyOrDefault()), CreateClientOptions());

        public GeneratedSpeechVoice GetVoice(string? voiceOverride = null)
            => ResolveVoiceName(voiceOverride);

        public string ResolveVoiceName(string? voiceOverride = null)
            => string.IsNullOrWhiteSpace(voiceOverride)
                ? Voice.Trim()
                : voiceOverride.Trim();

        public GeneratedSpeechFormat GetFormat()
            => string.Equals(Format.Trim(), "wav", StringComparison.OrdinalIgnoreCase)
                ? GeneratedSpeechFormat.Wav
                : throw new InvalidOperationException(
                    $"Config key '{ConfigSection}/Format' must be 'wav' for OpenAI speech generation. Got '{Format.Trim()}'.");

        public Uri CreateEndpointUri()
        {
            string endpointUrl = Host.Trim();
            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                throw new InvalidOperationException(
                    $"Missing '{ConfigSection}/Host' in OpenAI speech-generator config '{ConfigPathDescription}'.");
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

        public static OpenAISpeechGeneratorSettings Load(string configPath)
            => Load(configPath, () => ConfigProvider.LoadMerged(), ConfigProvider.Load);

        internal static OpenAISpeechGeneratorSettings Load(
            string configPath,
            Func<ConfigProvider> mergedConfigLoader,
            Func<string, ConfigProvider> singleConfigLoader)
            => Load(LoadConfigProvider(configPath, mergedConfigLoader, singleConfigLoader), configPath);

        internal static OpenAISpeechGeneratorSettings Load(ConfigProvider configProvider, string configPathDescription)
        {
            return new OpenAISpeechGeneratorSettings(
                GetString(configProvider, nameof(Host)),
                GetOptionalString(configProvider, nameof(ApiKey)),
                GetOptionalString(configProvider, nameof(Model)) ?? DefaultModel,
                GetOptionalString(configProvider, nameof(Voice)) ?? DefaultVoice,
                GetOptionalString(configProvider, "Format") ?? DefaultFormat,
                GetOptionalFloat(configProvider, nameof(SpeedRatio)),
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
