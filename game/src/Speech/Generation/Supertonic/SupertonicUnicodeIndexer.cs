// Adapted from the Supertonic C# reference implementation (csharp/Helper.cs, UnicodeProcessor),
// Copyright (c) 2025 Supertone Inc. Licensed under the MIT License.
// https://github.com/supertone-inc/supertonic
using System.Text.Json;

namespace AlleyCat.Speech.Generation.Supertonic;

/// <summary>
/// Unicode-indexer token table mapping character code points to text-encoder token IDs.
/// </summary>
/// <remarks>
/// The <c>unicode_indexer.json</c> asset is a flat integer array whose element index is the source
/// code point and whose value is the token ID consumed by the Supertonic text encoder. Entries with
/// a negative value denote code points without a learned embedding row; the ONNX <c>Gather</c>
/// kernel resolves negative indices from the end of the embedding table, matching the reference
/// behaviour.
/// </remarks>
public sealed class SupertonicUnicodeIndexer
{
    private readonly Dictionary<int, long> _tokenIdsByCodePoint;

    /// <summary>
    /// Initialises the indexer from a pre-parsed token table.
    /// </summary>
    /// <param name="tokenIdsByCodePoint">Mapping from code point to token ID.</param>
    internal SupertonicUnicodeIndexer(Dictionary<int, long> tokenIdsByCodePoint)
    {
        ArgumentNullException.ThrowIfNull(tokenIdsByCodePoint);
        _tokenIdsByCodePoint = tokenIdsByCodePoint;
    }

    /// <summary>
    /// Gets the number of indexed code points.
    /// </summary>
    public int Count => _tokenIdsByCodePoint.Count;

    /// <summary>
    /// Attempts to resolve the encoder token ID for a UTF-16 code unit.
    /// </summary>
    /// <param name="codePoint">Source character code point.</param>
    /// <param name="tokenId">Resolved token ID when present.</param>
    /// <returns><see langword="true" /> when the code point is indexed; otherwise <see langword="false" />.</returns>
    public bool TryGetTokenId(int codePoint, out long tokenId) => _tokenIdsByCodePoint.TryGetValue(codePoint, out tokenId);

    /// <summary>
    /// Loads an indexer from a <c>unicode_indexer.json</c> file.
    /// </summary>
    /// <param name="path">Absolute path to the JSON file.</param>
    /// <returns>The parsed indexer.</returns>
    public static SupertonicUnicodeIndexer Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Parses an indexer from raw <c>unicode_indexer.json</c> content.
    /// </summary>
    /// <param name="json">Raw JSON document containing a flat integer array.</param>
    /// <returns>The parsed indexer.</returns>
    public static SupertonicUnicodeIndexer Parse(string json)
    {
        long[]? tokenIds = JsonSerializer.Deserialize<long[]>(json)
            ?? throw new InvalidOperationException("Failed to parse unicode_indexer.json as a token ID array.");

        Dictionary<int, long> tokenIdsByCodePoint = new(tokenIds.Length);
        for (int codePoint = 0; codePoint < tokenIds.Length; codePoint++)
        {
            tokenIdsByCodePoint[codePoint] = tokenIds[codePoint];
        }

        return new SupertonicUnicodeIndexer(tokenIdsByCodePoint);
    }
}
