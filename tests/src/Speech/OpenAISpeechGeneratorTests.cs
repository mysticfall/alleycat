using AlleyCat.Speech.Generation;
using OpenAI.Audio;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for OpenAI-compatible speech-generation helpers.
/// </summary>
public sealed class OpenAISpeechGeneratorTests
{
    /// <summary>
    /// OpenAI-compatible backends without auth must still produce an SDK-safe credential value.
    /// </summary>
    [Fact]
    public void GetApiKeyOrDefault_ApiKeyMissing_UsesDummyCompatibleBackendKey()
    {
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: null,
            Model: "tts-1",
            Voice: "alloy",
            Format: "mp3",
            SpeedRatio: null,
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
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "alloy",
            Format: "mp3",
            SpeedRatio: null,
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
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "api.openai.com",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "alloy",
            Format: "mp3",
            SpeedRatio: null,
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
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "alloy",
            Format: "mp3",
            SpeedRatio: null,
            TimeoutSeconds: null);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(settings.CreateEndpointUri);

        Assert.Contains("must include the API base path", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Per-request instructions must not be injected into spoken text when the SDK lacks a dedicated field.
    /// </summary>
    [Fact]
    public void CreateSpeechGenerationOptions_InstructionProvided_DoesNotAlterConfiguredOptions()
    {
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "alloy",
            Format: "wav",
            SpeedRatio: 1.25f,
            TimeoutSeconds: null);

        SpeechGenerationOptions options = OpenAISpeechGenerator.CreateSpeechGenerationOptions(
            settings,
            "Speak cheerfully.");

        Assert.Equal(1.25f, options.SpeedRatio);
        Assert.Equal(GeneratedSpeechFormat.Wav, options.ResponseFormat);
    }

    /// <summary>
    /// Arbitrary OpenAI-compatible voice names must remain usable.
    /// </summary>
    [Fact]
    public void GetVoice_CustomCompatibleVoice_PreservesConfiguredName()
    {
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "vendor-voice-01",
            Format: "mp3",
            SpeedRatio: null,
            TimeoutSeconds: null);

        GeneratedSpeechVoice voice = settings.GetVoice();

        Assert.Equal("vendor-voice-01", voice.ToString());
    }

    /// <summary>
    /// Explicit node overrides must take precedence when provided.
    /// </summary>
    [Fact]
    public void ResolveVoiceName_OverrideProvided_UsesOverride()
    {
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: "alloy",
            Format: "mp3",
            SpeedRatio: null,
            TimeoutSeconds: null);

        string voice = settings.ResolveVoiceName(" custom-voice ");

        Assert.Equal("custom-voice", voice);
    }

    /// <summary>
    /// Blank overrides must fall back to the configured voice.
    /// </summary>
    [Fact]
    public void ResolveVoiceName_BlankOverride_FallsBackToConfiguredVoice()
    {
        OpenAISpeechGenerator.OpenAISpeechGeneratorSettings settings = new(
            Host: "https://api.openai.com/v1",
            ApiKey: string.Empty,
            Model: "tts-1",
            Voice: " vendor-default ",
            Format: "mp3",
            SpeedRatio: null,
            TimeoutSeconds: null);

        string voice = settings.ResolveVoiceName(" ");

        Assert.Equal("vendor-default", voice);
    }
}
