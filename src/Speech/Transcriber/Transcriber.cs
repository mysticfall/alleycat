using System.Text;
using AlleyCat.Env;
using Godot;
using LanguageExt;

namespace AlleyCat.Speech.Transcriber;

public abstract class Transcriber : ITranscriber
{
    public Eff<IEnv, DialogueText> Transcribe(AudioStreamWav audio) =>
        Transcribe(CreateWavStream(audio));

    public abstract Eff<IEnv, DialogueText> Transcribe(Stream audio);

    private static MemoryStream CreateWavStream(AudioStreamWav audio)
    {
        var stream = new MemoryStream();

        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);

        var sampleRate = (uint)audio.MixRate;
        var channels = (ushort)(audio.Stereo ? 2 : 1);
        var bitsPerSample = (ushort)(audio.Format == AudioStreamWav.FormatEnum.Format8Bits ? 8 : 16);
        var bytesPerSample = (ushort)(bitsPerSample / 8);
        var blockAlign = (ushort)(channels * bytesPerSample);
        var byteRate = sampleRate * blockAlign;
        var dataSize = (uint)audio.Data.Length;
        var fileSize = 36 + dataSize;

        // RIFF header
        writer.Write("RIFF".ToCharArray());
        writer.Write(fileSize);
        writer.Write("WAVE".ToCharArray());

        // fmt chunk
        writer.Write("fmt ".ToCharArray());
        writer.Write(16u); // chunk size
        writer.Write((ushort)1); // PCM format
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        // data chunk
        writer.Write("data".ToCharArray());
        writer.Write(dataSize);
        writer.Write(audio.Data);

        stream.Position = 0;

        return stream;
    }
}