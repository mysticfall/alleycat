using System.IO.Compression;
using System.Text.Json;

namespace AlleyCat.Navigation.Recording;

/// <summary>Reads only a manifest's sealed prefix; never infers completeness from partial files.</summary>
internal static class NavigationSessionReader
{
    internal sealed record Entry(JsonElement Event, float[] Values);

    internal static IEnumerable<Entry> ReadCheckpoint(string directory)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
        if (manifest.RootElement.GetProperty("schema").GetString() != "alleycat-navigation-session-v1")
        {
            throw new InvalidDataException("Unsupported navigation recording schema.");
        }
        long previous = 0;
        foreach (JsonElement segment in manifest.RootElement.GetProperty("segments").EnumerateArray())
        {
            string Resolve(string key)
            {
                string name = segment.GetProperty(key).GetString()!;
                return name != Path.GetFileName(name) || name.EndsWith(".partial", StringComparison.Ordinal)
                    ? throw new InvalidDataException("Manifest must reference sealed local segments.")
                    : Path.Combine(directory, name);
            }
            using var gzip = new GZipStream(File.OpenRead(Resolve("motion")), CompressionMode.Decompress);
            using var binary = new BinaryReader(gzip);
            if (!binary.ReadBytes(8).AsSpan().SequenceEqual("ACNAV001"u8) || binary.ReadInt32() != 1)
            {
                throw new InvalidDataException("Invalid motion segment header.");
            }
            long records = 0;
            foreach (string line in File.ReadLines(Resolve("events")))
            {
                using var document = JsonDocument.Parse(line);
                JsonElement value = document.RootElement;
                long sequence = value.GetProperty("sequence").GetInt64();
                int count = value.GetProperty("bulkFloats").GetInt32();
                if (sequence <= previous || count < 0 || count > 8 * 1024 * 1024)
                {
                    throw new InvalidDataException("Invalid motion sequence/length.");
                }
                float[] values = new float[count];
                if (count != 0)
                {
                    if (binary.ReadInt64() != sequence || binary.ReadInt32() != count)
                    {
                        throw new InvalidDataException("Motion/event association mismatch.");
                    }
                    for (int i = 0; i < count; i++)
                    {
                        values[i] = binary.ReadSingle();
                    }
                }
                previous = sequence;
                records++;
                yield return new(value.Clone(), values);
            }
            if (records != segment.GetProperty("records").GetInt64() || gzip.ReadByte() != -1)
            {
                throw new InvalidDataException("Segment record count or motion tail mismatch.");
            }
        }
        if (previous != manifest.RootElement.GetProperty("savedThroughSequence").GetInt64())
        {
            throw new InvalidDataException("Saved checkpoint sequence mismatch.");
        }
    }
}
