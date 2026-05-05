using AlleyCat.Speech;
using Godot;
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
}
