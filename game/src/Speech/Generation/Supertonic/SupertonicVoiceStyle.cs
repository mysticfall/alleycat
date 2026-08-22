// Adapted from the Supertonic C# reference implementation (csharp/Helper.cs, LoadVoiceStyle),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Text.Json;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// A preset voice style parsed from a Supertonic <c>voice_styles</c> JSON file.
/// </summary>
/// <param name="TtlData">Flattened <c>style_ttl</c> tensor data.</param>
/// <param name="TtlShape">Tensor shape of <see cref="TtlData" />.</param>
/// <param name="DpData">Flattened <c>style_dp</c> tensor data.</param>
/// <param name="DpShape">Tensor shape of <see cref="DpData" />.</param>
/// <param name="Metadata">Scalar metadata entries describing the extracted style.</param>
public sealed record SupertonicVoiceStyle(
    float[] TtlData,
    long[] TtlShape,
    float[] DpData,
    long[] DpShape,
    IReadOnlyDictionary<string, string> Metadata)
{
    /// <summary>
    /// Loads a voice style from a JSON file.
    /// </summary>
    /// <param name="path">Absolute path to the voice-style JSON file.</param>
    /// <returns>The parsed voice style.</returns>
    public static SupertonicVoiceStyle Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parses a voice style from raw JSON content.
    /// </summary>
    /// <param name="json">Raw JSON document containing <c>style_ttl</c>, <c>style_dp</c>, and optional metadata.</param>
    /// <returns>The parsed voice style.</returns>
    public static SupertonicVoiceStyle Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        return new SupertonicVoiceStyle(
            ParseTensor(root, "style_ttl", out long[] ttlShape),
            ttlShape,
            ParseTensor(root, "style_dp", out long[] dpShape),
            dpShape,
            ParseMetadata(root));
    }

    private static float[] ParseTensor(JsonElement root, string propertyName, out long[] shape)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement tensor))
        {
            throw new InvalidOperationException($"Supertonic voice style is missing the '{propertyName}' entry.");
        }

        if (!tensor.TryGetProperty("dims", out JsonElement dimsElement))
        {
            throw new InvalidOperationException($"Supertonic voice style '{propertyName}' is missing 'dims'.");
        }

        shape = [.. dimsElement.EnumerateArray().Select(element => element.GetInt64())];
        int expectedLength = 1;
        foreach (long dimension in shape)
        {
            expectedLength *= (int)dimension;
        }

        float[] data = [.. FlattenData(tensor.GetProperty("data"))];
        return data.Length == expectedLength
            ? data
            : throw new InvalidOperationException(
                $"Supertonic voice style '{propertyName}' declares {expectedLength} values from dims but contains {data.Length}.");
    }

    private static IEnumerable<float> FlattenData(JsonElement data)
    {
        foreach (JsonElement batch in data.EnumerateArray())
        {
            foreach (JsonElement row in batch.EnumerateArray())
            {
                foreach (JsonElement value in row.EnumerateArray())
                {
                    yield return value.GetSingle();
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseMetadata(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out JsonElement metadata) || metadata.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>();
        }

        Dictionary<string, string> entries = [];
        foreach (JsonProperty property in metadata.EnumerateObject())
        {
            // Metadata is informational only; scalar entries are preserved verbatim and structured
            // values are skipped rather than blocking style loading.
            string? value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null => null,
                _ => null,
            };

            if (value is not null)
            {
                entries[property.Name] = value;
            }
        }

        return entries;
    }
}
