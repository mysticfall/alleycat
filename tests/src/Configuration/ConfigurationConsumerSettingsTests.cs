using AlleyCat.Mind.AI.Provider;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.Transcription;
using Xunit;

namespace AlleyCat.Tests.Configuration;

/// <summary>
/// Unit coverage for consumers that bind from typed .NET options.
/// </summary>
public sealed class ConfigurationConsumerSettingsTests
{
    /// <summary>
    /// STT options are normalised before constructing runtime settings.
    /// </summary>
    [Fact]
    public void OpenAITranscriberSettings_LoadFromOptions_TrimsValuesAndAppliesDefaults()
    {
        var settings = OpenAITranscriber.OpenAITranscriberSettings.Load(
            new STTOptions
            {
                Host = " https://stt.example/v1 ",
                Language = " en ",
                Hotwords = "  alleycat felis catus  ",
                Timeout = 12,
            },
            "typed-options");

        Assert.Equal("https://stt.example/v1", settings.Host);
        Assert.Equal("whisper-1", settings.Model);
        Assert.Equal("en", settings.Language);
        Assert.Equal("alleycat felis catus", settings.Hotwords);
        Assert.Null(settings.ApiKey);
        Assert.Null(settings.Prompt);
        Assert.Equal(12, settings.TimeoutSeconds);
    }

    /// <summary>
    /// Blank STT hint values normalise to null so the request omits them entirely.
    /// </summary>
    [Fact]
    public void OpenAITranscriberSettings_LoadFromOptions_BlankHintsNormaliseToNull()
    {
        var settings = OpenAITranscriber.OpenAITranscriberSettings.Load(
            new STTOptions
            {
                Host = "https://stt.example/v1",
                Hotwords = " \t ",
            },
            "typed-options");

        Assert.Null(settings.Hotwords);
    }

    /// <summary>
    /// TTS options are normalised before constructing runtime settings.
    /// </summary>
    [Fact]
    public void OpenAISpeechGeneratorSettings_LoadFromOptions_TrimsValuesAndAppliesDefaults()
    {
        var settings = OpenAISpeechGenerator.OpenAISpeechGeneratorSettings.Load(
            new TTSOptions
            {
                Host = " https://tts.example/v1 ",
                Voice = " nova ",
                SpeedRatio = 1.1f,
            },
            "typed-options");

        Assert.Equal("https://tts.example/v1", settings.Host);
        Assert.Equal("tts-1", settings.Model);
        Assert.Equal("nova", settings.Voice);
        Assert.Equal("wav", settings.Format);
        Assert.Equal(1.1f, settings.SpeedRatio);
    }

    /// <summary>
    /// AI options are normalised before constructing runtime settings.
    /// </summary>
    [Fact]
    public void OpenAIClientProviderSettings_LoadFromOptions_TrimsValuesAndAppliesDefaults()
    {
        var settings = OpenAIClientProvider.OpenAIClientProviderSettings.Load(
            new AIOptions
            {
                Host = " https://ai.example/v1 ",
                ApiKey = " test-key ",
            },
            "typed-options");

        Assert.Equal("https://ai.example/v1", settings.Host);
        Assert.Equal("gpt-4o-mini", settings.Model);
        Assert.Equal("test-key", settings.ApiKey);
    }
}
