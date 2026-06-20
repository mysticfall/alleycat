using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
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

namespace AlleyCat.Speech.Generation;

#pragma warning disable OPENAI001 // Streaming TTS APIs are required to minimise runtime speech latency.

/// <summary>
/// OpenAI-compatible speech generator backed by the official OpenAI .NET SDK.
/// </summary>
[GlobalClass]
public partial class OpenAISpeechGenerator : SpeechGenerator
{
    private const string ConfigSection = "TTS";
    private const string DefaultConfigPath = GameConfiguration.DefaultBaseConfigPath;
    private const string DefaultModel = "tts-1";
    private const string DefaultVoice = "alloy";
    private const string DefaultFormat = "wav";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";
    private const string ConfigLoadFailureNotification = "Speech generation is unavailable. Please check the TTS configuration.";

    private OpenAISpeechGeneratorSettings? _settings;
    private ILogger<OpenAISpeechGenerator>? _logger;

    /// <summary>
    /// Config file used to resolve OpenAI-compatible speech-generation settings.
    /// </summary>
    [Export(PropertyHint.File, "*.json")]
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
        _logger = GameLoggerResolver.ResolveRequired<OpenAISpeechGenerator>();

        try
        {
            _settings = OpenAISpeechGeneratorSettings.Load(ConfigPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load TTS configuration from {ConfigPath}.", ConfigPath);
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
        Stopwatch backendStopwatch = AIPipelineDebugLog.StartTimer();
        try
        {
            BinaryData audio = await client.GenerateSpeechAsync(text, settings.GetVoice(VoiceOverride), options);
            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("TTS backend returned in", backendStopwatch, $"model {settings.Model}");
            }

            return audio.ToArray();
        }
        catch (ClientResultException ex)
        {
            throw await OpenAISpeechErrorDiagnostics.CreateExceptionAsync(ex);
        }
    }

    /// <inheritdoc />
    protected override async Task<byte[]> GenerateStreamingCore(
        string text,
        string? instruction,
        Func<byte[], Task> audioChunkHandler)
    {
        OpenAISpeechGeneratorSettings settings = _settings ?? OpenAISpeechGeneratorSettings.Load(ConfigPath);
        AudioClient client = settings.CreateAudioClient();
        SpeechGenerationOptions options = CreateSpeechGenerationOptions(settings, instruction);
        Stopwatch backendStopwatch = AIPipelineDebugLog.StartTimer();

        using MemoryStream audioStream = new();
        try
        {
            await foreach (StreamingSpeechUpdate update in client.GenerateSpeechStreamingAsync(
                text,
                settings.GetVoice(VoiceOverride),
                options))
            {
                if (update is StreamingSpeechAudioDeltaUpdate audioDeltaUpdate)
                {
                    byte[] audioChunk = audioDeltaUpdate.AudioBytes.ToArray();
                    await audioStream.WriteAsync(audioChunk);
                    await audioChunkHandler(audioChunk);
                }
            }

            if (AIPipelineDebugLog.IsEnabled)
            {
                AIPipelineDebugLog.Latency("TTS backend stream completed in", backendStopwatch, $"model {settings.Model}");
            }

            return audioStream.ToArray();
        }
        catch (ClientResultException ex)
        {
            throw await OpenAISpeechErrorDiagnostics.CreateExceptionAsync(ex);
        }
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

    internal static class OpenAISpeechErrorDiagnostics
    {
        private const string FailurePrefix = "OpenAI-compatible TTS request failed";

        public static async Task<Exception> CreateExceptionAsync(ClientResultException ex)
        {
            string message = await FormatExceptionMessageAsync(ex);
            return new OpenAISpeechGenerationException(message, ex);
        }

        internal static async Task<string> FormatExceptionMessageAsync(ClientResultException ex)
        {
            PipelineResponse? response = ex.GetRawResponse();
            string status = response is null ? ex.Status.ToString(CultureInfo.InvariantCulture) : response.Status.ToString(CultureInfo.InvariantCulture);
            string? rawBody = response is null ? null : await ReadResponseBodyAsync(response);
            string? parsedDiagnostic = FormatResponseDiagnostic(rawBody);

            string message = $"{FailurePrefix}. Status: {status}.";
            if (!string.IsNullOrWhiteSpace(parsedDiagnostic))
            {
                message += $" Error: {parsedDiagnostic}.";
            }

            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                message += $" Raw response body: {rawBody}";
            }

            return message;
        }

        internal static string? FormatResponseDiagnostic(string? rawBody)
        {
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(rawBody);
                JsonElement root = document.RootElement;

                return root.ValueKind == JsonValueKind.Object
                    ? FormatObjectResponseDiagnostic(root)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static async Task<string?> ReadResponseBodyAsync(PipelineResponse response)
        {
            try
            {
                BinaryData content = response.Content;
                return content.ToString();
            }
            catch (InvalidOperationException)
            {
                try
                {
                    BinaryData content = await response.BufferContentAsync(CancellationToken.None);
                    return content.ToString();
                }
                catch (Exception)
                {
                    return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string? FormatObjectResponseDiagnostic(JsonElement root)
            => root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && TryGetNonEmptyString(error, "message", out string? openAIMessage)
                ? FormatOpenAIError(openAIMessage, error)
                : TryGetNonEmptyString(root, "detail", out string? detail)
                    ? detail
                    : TryGetNonEmptyString(root, "message", out string? message) ? message : null;

        private static string FormatOpenAIError(string message, JsonElement error)
        {
            List<string> attributes = [];
            AddOpenAIErrorAttribute(attributes, error, "type");
            AddOpenAIErrorAttribute(attributes, error, "code");
            AddOpenAIErrorAttribute(attributes, error, "param");

            return attributes.Count == 0
                ? message
                : $"{message} ({string.Join(", ", attributes)})";
        }

        private static void AddOpenAIErrorAttribute(List<string> attributes, JsonElement error, string propertyName)
        {
            if (TryGetNonEmptyString(error, propertyName, out string? value))
            {
                attributes.Add($"{propertyName}: {value}");
            }
        }

        private static bool TryGetNonEmptyString(
            JsonElement parent,
            string propertyName,
            [NotNullWhen(true)] out string? value)
        {
            value = null;
            if (!parent.TryGetProperty(propertyName, out JsonElement property) || property.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? text = property.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            value = text;
            return true;
        }
    }

    internal sealed class OpenAISpeechGenerationException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

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
            => Load(LoadConfiguration(configPath), configPath);

        internal static OpenAISpeechGeneratorSettings Load(TTSOptions options, string configPathDescription = DefaultConfigPath)
        {
            ArgumentNullException.ThrowIfNull(options);

            return new OpenAISpeechGeneratorSettings(
                Clean(options.Host) ?? string.Empty,
                Clean(options.ApiKey),
                Clean(options.Model) ?? DefaultModel,
                Clean(options.Voice) ?? DefaultVoice,
                Clean(options.Format) ?? DefaultFormat,
                options.SpeedRatio,
                options.Timeout)
            {
                ConfigPathDescription = configPathDescription,
            };
        }

        internal static OpenAISpeechGeneratorSettings Load(
            string configPath,
            Func<IConfiguration> defaultConfigurationLoader,
            Func<string, IConfiguration> customConfigurationLoader)
            => Load(LoadConfiguration(configPath, defaultConfigurationLoader, customConfigurationLoader), configPath);

        internal static OpenAISpeechGeneratorSettings Load(IConfiguration configuration, string configPathDescription)
        {
            ArgumentNullException.ThrowIfNull(configuration);

            TTSOptions options = new();
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
