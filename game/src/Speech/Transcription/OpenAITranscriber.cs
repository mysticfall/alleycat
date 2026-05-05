using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AlleyCat.UI;
using Godot;

namespace AlleyCat.Speech.Transcription;

/// <summary>
/// OpenAI-compatible speech transcriber backed by the official OpenAI .NET SDK.
/// </summary>
[GlobalClass]
public partial class OpenAITranscriber : Transcriber
{
    private const string ConfigSection = "Speech";
    private const string DefaultConfigPath = "res://AlleyCat.cfg";
    private const string DefaultModel = "whisper-1";
    private const string DefaultCompatibleBackendApiKey = "unused-api-key";

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
            _settings = null;
        }
    }

    /// <inheritdoc />
    public override async Task<string> Transcribe(AudioStreamWav audioStream)
    {
        OpenAITranscriberSettings settings = _settings ?? OpenAITranscriberSettings.Load(ConfigPath);

        byte[] wavBytes = CreateWaveFileBytes(audioStream);
        using System.Net.Http.HttpClient httpClient = CreateHttpClient(settings);
        using HttpRequestMessage request = CreateTranscriptionRequest(settings, wavBytes, "alleycat-recording.wav");
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        string responseBody = await response.Content.ReadAsStringAsync();

        _ = response.IsSuccessStatusCode
            ? 0
            : throw new HttpRequestException(
                $"OpenAI transcription request failed with status {(int)response.StatusCode} ({response.StatusCode}). Response: {responseBody}");

        return ParseTranscriptionText(responseBody).Trim();
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

    internal static HttpRequestMessage CreateTranscriptionRequest(
        OpenAITranscriberSettings settings,
        byte[] wavBytes,
        string audioFilename)
    {
        HttpRequestMessage request = new(HttpMethod.Post, CreateTranscriptionEndpointUri(settings.CreateEndpointUri()));

        if (settings.TryGetApiKey(out string? apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        request.Content = CreateTranscriptionRequestContent(settings, wavBytes, audioFilename);
        return request;
    }

    internal static MultipartFormDataContent CreateTranscriptionRequestContent(
        OpenAITranscriberSettings settings,
        byte[] wavBytes,
        string audioFilename)
    {
        ByteArrayContent audioContent = new(wavBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        MultipartFormDataContent content = [];
        content.Add(audioContent, "file", audioFilename);
        content.Add(new StringContent(settings.Model), "model");

        if (!string.IsNullOrWhiteSpace(settings.Language))
        {
            content.Add(new StringContent(settings.Language), "language");
        }

        if (!string.IsNullOrWhiteSpace(settings.Prompt))
        {
            content.Add(new StringContent(settings.Prompt), "prompt");
        }

        if (settings.Temperature is float temperature)
        {
            content.Add(
                new StringContent(temperature.ToString(CultureInfo.InvariantCulture)),
                "temperature");
        }

        return content;
    }

    [SuppressMessage("ReSharper", "UseUtf8StringLiteral")]
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

    private static System.Net.Http.HttpClient CreateHttpClient(OpenAITranscriberSettings settings)
    {
        System.Net.Http.HttpClient client = new();

        if (settings.TimeoutSeconds is int timeoutSeconds)
        {
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return client;
    }

    private static Uri CreateTranscriptionEndpointUri(Uri endpointUri)
        => new($"{endpointUri.AbsoluteUri.TrimEnd('/')}/audio/transcriptions");

    internal static string ParseTranscriptionText(string responseBody)
    {
        TranscriptionResponse? response = JsonSerializer.Deserialize<TranscriptionResponse>(responseBody, JsonOptions);
        return string.IsNullOrWhiteSpace(response?.Text)
            ? throw new InvalidOperationException(
                "OpenAI transcription response did not contain a non-empty 'text' field.")
            : response.Text;
    }

    private static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    private sealed record TranscriptionResponse(string Text);

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

        public bool TryGetApiKey(out string? apiKey)
        {
            apiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
            return apiKey is not null;
        }

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

        private string ConfigPathDescription
        {
            get;
            init;
        } = DefaultConfigPath;

        public static OpenAITranscriberSettings Load(string configPath)
        {
            ConfigFile configFile = new();
            Error error = configFile.Load(configPath);
            _ = error != Error.Ok
                ? throw new InvalidOperationException(
                    $"Failed to load OpenAI transcriber config '{configPath}': {error}.")
                : 0;

            return new OpenAITranscriberSettings(
                GetString(configFile, nameof(Host)),
                GetOptionalString(configFile, nameof(ApiKey)),
                GetOptionalString(configFile, nameof(Model)) ?? DefaultModel,
                GetOptionalString(configFile, nameof(Language)),
                GetOptionalString(configFile, nameof(Prompt)),
                GetOptionalFloat(configFile, nameof(Temperature)),
                GetOptionalInt(configFile, "Timeout"))
            {
                ConfigPathDescription = configPath,
            };
        }

        private static string GetString(ConfigFile configFile, string key)
            => GetOptionalString(configFile, key) ?? string.Empty;

        private static string? GetOptionalString(ConfigFile configFile, string key)
        {
            Variant value = configFile.GetValue(ConfigSection, key, Variant.From(string.Empty));
            string text = value.AsString().Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static float? GetOptionalFloat(ConfigFile configFile, string key)
        {
            Variant value = configFile.GetValue(ConfigSection, key, Variant.From(string.Empty));
            string text = value.AsString().Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"Config key '{ConfigSection}/{key}' must be a valid float. Got '{text}'.");
        }

        private static int? GetOptionalInt(ConfigFile configFile, string key)
        {
            Variant value = configFile.GetValue(ConfigSection, key, Variant.From(string.Empty));
            string text = value.AsString().Trim();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                    ? parsed
                    : throw new InvalidOperationException(
                        $"Config key '{ConfigSection}/{key}' must be a valid integer. Got '{text}'.");
        }
    }
}
