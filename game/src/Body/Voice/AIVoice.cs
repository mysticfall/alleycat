using System.Buffers.Binary;
using System.Text;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using Godot;

namespace AlleyCat.Body.Voice;

/// <summary>
/// Voice implementation that generates speech audio and hands it off to lip-sync playback.
/// </summary>
[GlobalClass]
public partial class AIVoice : Voice
{
    private const int ExpectedWaveFormatCode = 1;
    private const short ExpectedChannelCount = 1;
    private const short ExpectedBitsPerSample = 16;
    private const int ExpectedSampleRate = 16000;
    private const string AudioFormatIncompatibleMessage = "Audio format incompatible";

    private readonly Lock _generationStateLock = new();
    private bool _isGenerating;

    /// <summary>
    /// Speech generator used to create spoken audio bytes.
    /// </summary>
    [Export]
    public SpeechGenerator? SpeechGenerator
    {
        get;
        set;
    }

    /// <summary>
    /// Lip-sync player that owns synchronised playback.
    /// </summary>
    [Export]
    public LipSyncPlayer? LipSyncPlayer
    {
        get;
        set;
    }

    /// <inheritdoc />
    public override void Speak(string dialogue)
        => _ = SpeakAsync(dialogue);

    internal async Task SpeakAsync(string dialogue)
    {
        if (!Enabled)
        {
            return;
        }

        if (!TryBeginGeneration())
        {
            GD.PushWarning("AIVoice ignored Speak() because a speech generation request is already in flight.");
            return;
        }

        try
        {
            if (SpeechGenerator is null)
            {
                await FailSpeechAsync("AIVoice requires a configured SpeechGenerator.");
                return;
            }

            if (LipSyncPlayer is null)
            {
                await FailSpeechAsync("AIVoice requires a configured LipSyncPlayer.");
                return;
            }

            byte[] generatedAudio = await GenerateSpeechAudioAsync(dialogue);
            AudioStreamWav speechStream = CreatePlayableSpeech(generatedAudio);
            await DispatchDeferredGodotActionAsync(() => PlayGeneratedSpeech(speechStream));
        }
        catch (AudioConversionException ex)
        {
            await FailSpeechAsync(AudioFormatIncompatibleMessage, $"Audio conversion failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            await FailSpeechAsync(ex.Message, ex.ToString());
        }
        finally
        {
            EndGeneration();
        }
    }

    internal static AudioStreamWav CreatePlayableSpeech(byte[] generatedAudio)
    {
        ParsedSpeechData speechData = ParsePlayableSpeechData(generatedAudio);

        AudioStreamWav audioStream = new()
        {
            Data = speechData.PcmData,
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = speechData.SampleRate,
            Stereo = speechData.Stereo,
        };

        return audioStream;
    }

    internal static ParsedSpeechData ParsePlayableSpeechData(byte[] generatedAudio)
    {
        WaveFileData waveFile = ParseWaveFile(generatedAudio);
        return new ParsedSpeechData(waveFile.PcmData, ExpectedSampleRate, Stereo: false, BitsPerSample: ExpectedBitsPerSample);
    }

    private static WaveFileData ParseWaveFile(byte[] audioBytes)
    {
        if (audioBytes.Length < 44)
        {
            throw new AudioConversionException("Generated audio was too short to contain a valid WAV file.");
        }

        if (!HasAscii(audioBytes, 0, "RIFF") || !HasAscii(audioBytes, 8, "WAVE"))
        {
            throw new AudioConversionException("Generated audio was not a RIFF/WAVE file.");
        }

        int offset = 12;
        FmtChunkData? fmtChunk = null;
        byte[]? pcmData = null;

        while (offset <= audioBytes.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(audioBytes, offset, 4);
            int chunkSize = BinaryPrimitives.ReadInt32LittleEndian(audioBytes.AsSpan(offset + 4, 4));
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > audioBytes.Length)
            {
                throw new AudioConversionException("Generated audio contained a malformed WAV chunk.");
            }

            ReadOnlySpan<byte> chunkData = audioBytes.AsSpan(offset, chunkSize);

            switch (chunkId)
            {
                case "fmt ":
                    fmtChunk = ParseFmtChunk(chunkData);
                    break;
                case "data":
                    pcmData = chunkData.ToArray();
                    break;
                default:
                    break;
            }

            offset += chunkSize;
            if ((chunkSize & 1) != 0)
            {
                offset++;
            }
        }

        if (fmtChunk is null)
        {
            throw new AudioConversionException("Generated audio was missing the WAV fmt chunk.");
        }

        if (pcmData is null || pcmData.Length == 0)
        {
            throw new AudioConversionException("Generated audio was missing the WAV data chunk.");
        }

        ValidateCompatibility(fmtChunk);
        return new WaveFileData(pcmData);
    }

    private static FmtChunkData ParseFmtChunk(ReadOnlySpan<byte> chunkData)
        => chunkData.Length < 16
            ? throw new AudioConversionException("Generated audio contained an incomplete WAV fmt chunk.")
            : new FmtChunkData(
                BinaryPrimitives.ReadInt16LittleEndian(chunkData[..2]),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(2, 2)),
                BinaryPrimitives.ReadInt32LittleEndian(chunkData.Slice(4, 4)),
                BinaryPrimitives.ReadInt16LittleEndian(chunkData.Slice(14, 2)));

    private static void ValidateCompatibility(FmtChunkData fmtChunk)
    {
        if (fmtChunk.FormatCode != ExpectedWaveFormatCode)
        {
            throw new AudioConversionException($"Expected PCM WAV audio, got format code {fmtChunk.FormatCode}.");
        }

        if (fmtChunk.ChannelCount != ExpectedChannelCount)
        {
            throw new AudioConversionException($"Expected mono WAV audio, got {fmtChunk.ChannelCount} channels.");
        }

        if (fmtChunk.SampleRate != ExpectedSampleRate)
        {
            throw new AudioConversionException($"Expected 16000 Hz WAV audio, got {fmtChunk.SampleRate} Hz.");
        }

        if (fmtChunk.BitsPerSample != ExpectedBitsPerSample)
        {
            throw new AudioConversionException($"Expected 16-bit WAV audio, got {fmtChunk.BitsPerSample}-bit.");
        }
    }

    private static bool HasAscii(IReadOnlyList<byte> data, int offset, string text)
    {
        if (offset < 0 || offset + text.Length > data.Count)
        {
            return false;
        }

        for (int index = 0; index < text.Length; index++)
        {
            if (data[offset + index] != text[index])
            {
                return false;
            }
        }

        return true;
    }

    private async Task FailSpeechAsync(string emittedError, string? loggedError = null)
    {
        string errorToLog = string.IsNullOrWhiteSpace(loggedError) ? emittedError : loggedError;
        await DispatchDeferredGodotActionAsync(() => ReportSpeechFailure(emittedError, errorToLog));
    }

    /// <summary>
    /// Generates raw speech audio bytes for the supplied dialogue.
    /// </summary>
    /// <param name="dialogue">Dialogue text to synthesise.</param>
    /// <returns>Generated speech audio bytes.</returns>
    protected virtual Task<byte[]> GenerateSpeechAudioAsync(string dialogue)
        => SpeechGenerator!.Generate(dialogue);

    /// <summary>
    /// Hands a prepared WAV stream off to the lip-sync playback boundary.
    /// </summary>
    /// <param name="speechStream">Prepared speech stream.</param>
    protected virtual void PlayGeneratedSpeech(AudioStreamWav speechStream)
        => LipSyncPlayer!.Play(speechStream);

    private void ReportSpeechFailure(string emittedError, string loggedError)
    {
        GD.PushError(loggedError);
        EmitSpeechFailedSignal(emittedError);
    }

    /// <summary>
    /// Emits the voice failure signal.
    /// </summary>
    /// <param name="error">Failure message payload.</param>
    protected virtual void EmitSpeechFailedSignal(string error)
        => _ = EmitSignal(new StringName("SpeechFailed"), error);

    private bool TryBeginGeneration()
    {
        lock (_generationStateLock)
        {
            if (_isGenerating)
            {
                return false;
            }

            _isGenerating = true;
            return true;
        }
    }

    private void EndGeneration()
    {
        lock (_generationStateLock)
        {
            _isGenerating = false;
        }
    }

    private sealed record WaveFileData(byte[] PcmData);

    private sealed record FmtChunkData(short FormatCode, short ChannelCount, int SampleRate, short BitsPerSample);

    internal sealed record ParsedSpeechData(byte[] PcmData, int SampleRate, bool Stereo, short BitsPerSample);

    internal sealed class AudioConversionException(string message) : Exception(message);
}
