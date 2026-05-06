using AlleyCat.Speech.Voice;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for AI voice orchestration and WAV compatibility handling.
/// </summary>
public sealed class AIVoiceTests
{
    /// <summary>
    /// Backends that already produce compliant WAV payloads must keep their parsed PCM bytes and metadata intact.
    /// </summary>
    [Fact]
    public void ParsePlayableSpeechData_WithCompatibleWaveFile_PreservesExpectedSpeechData()
    {
        byte[] pcmData = [0x10, 0x20, 0x30, 0x40];
        byte[] waveBytes = CreateWaveFileBytes(pcmData, sampleRate: 16000, channelCount: 1, bitsPerSample: 16);

        AIVoice.ParsedSpeechData speech = AIVoice.ParsePlayableSpeechData(waveBytes);

        Assert.Equal(16, speech.BitsPerSample);
        Assert.Equal(16000, speech.SampleRate);
        Assert.False(speech.Stereo);
        Assert.Equal(pcmData, speech.PcmData);
    }

    /// <summary>
    /// Unsupported WAV metadata must fail fast so the voice path never desynchronises playback.
    /// </summary>
    [Fact]
    public void ParsePlayableSpeechData_WithStereoWave_ThrowsAudioConversionException()
    {
        byte[] waveBytes = CreateWaveFileBytes([0x10, 0x20, 0x30, 0x40], sampleRate: 16000, channelCount: 2, bitsPerSample: 16);

        AIVoice.AudioConversionException ex = Assert.Throws<AIVoice.AudioConversionException>(() => AIVoice.ParsePlayableSpeechData(waveBytes));

        Assert.Contains("mono WAV audio", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] CreateWaveFileBytes(byte[] data, int sampleRate, short channelCount, short bitsPerSample)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        short blockAlign = (short)(channelCount * bitsPerSample / 8);
        int byteRate = sampleRate * blockAlign;

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + data.Length);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channelCount);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }
}
