// Adapted from the Supertonic C# reference implementation (csharp/Helper.cs, LoadCfgs),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Text.Json;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Synthesis constants parsed from the Supertonic <c>tts.json</c> asset.
/// </summary>
/// <param name="SampleRate">Audio sample rate produced by the vocoder, in hertz.</param>
/// <param name="BaseChunkSize">Vocoder chunk size in samples.</param>
/// <param name="ChunkCompressFactor">Latent temporal compression factor of the TTL stage.</param>
/// <param name="LatentDim">Base latent channel count of the TTL stage.</param>
public sealed record SupertonicModelConfig(
    int SampleRate,
    int BaseChunkSize,
    int ChunkCompressFactor,
    int LatentDim)
{
    /// <summary>
    /// Loads a configuration from a <c>tts.json</c> file.
    /// </summary>
    /// <param name="path">Absolute path to the JSON file.</param>
    /// <returns>The parsed configuration.</returns>
    public static SupertonicModelConfig Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parses a configuration from raw <c>tts.json</c> content.
    /// </summary>
    /// <param name="json">Raw JSON document.</param>
    /// <returns>The parsed configuration.</returns>
    public static SupertonicModelConfig Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new SupertonicModelConfig(
            RequirePositiveInt(root, "ae", "sample_rate"),
            RequirePositiveInt(root, "ae", "base_chunk_size"),
            RequirePositiveInt(root, "ttl", "chunk_compress_factor"),
            RequirePositiveInt(root, "ttl", "latent_dim"));
    }

    private static int RequirePositiveInt(JsonElement root, string section, string property)
    {
        return !root.TryGetProperty(section, out JsonElement sectionElement)
            || !sectionElement.TryGetProperty(property, out JsonElement valueElement)
            || valueElement.ValueKind != JsonValueKind.Number
            || !valueElement.TryGetInt32(out int value)
            ? throw new InvalidOperationException($"Supertonic tts.json is missing an integer '{section}.{property}' entry.")
            : value > 0
            ? value
            : throw new InvalidOperationException($"Supertonic tts.json entry '{section}.{property}' must be greater than zero. Got {value}.");
    }
}
