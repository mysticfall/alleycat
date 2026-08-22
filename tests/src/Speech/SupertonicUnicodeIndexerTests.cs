using System.Text.Json;
using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for the Supertonic unicode-indexer token table.
/// </summary>
public sealed class SupertonicUnicodeIndexerTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The flat token array must map code points onto token IDs by element index, including negative entries.
    /// </summary>
    [Fact]
    public void Parse_TokenArray_MapsCodePointsToTokenIds()
    {
        var indexer = SupertonicUnicodeIndexer.Parse("[10, -1, 42]");

        Assert.Equal(3, indexer.Count);
        Assert.True(indexer.TryGetTokenId(0, out long firstTokenId));
        Assert.Equal(10, firstTokenId);
        Assert.True(indexer.TryGetTokenId(1, out long negativeTokenId));
        Assert.Equal(-1, negativeTokenId);
        Assert.True(indexer.TryGetTokenId(2, out long lastTokenId));
        Assert.Equal(42, lastTokenId);
    }

    /// <summary>
    /// Code points beyond the indexed range must report as unmapped with a zero token ID.
    /// </summary>
    [Fact]
    public void TryGetTokenId_UnindexedCodePoint_ReturnsFalse()
    {
        var indexer = SupertonicUnicodeIndexer.Parse("[10, -1, 42]");

        Assert.False(indexer.TryGetTokenId(3, out long unmappedTokenId));
        Assert.Equal(0, unmappedTokenId);
        Assert.False(indexer.TryGetTokenId(999, out _));
    }

    /// <summary>
    /// Indexer files must be loadable from disk, matching the runtime asset-loading path.
    /// </summary>
    [Fact]
    public void Load_IndexerFile_ParsesTokenTableFromDisk()
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
        string indexerPath = Path.Combine(_temporaryDirectory, "unicode_indexer.json");
        File.WriteAllText(indexerPath, "[7, 8, 9]");

        var indexer = SupertonicUnicodeIndexer.Load(indexerPath);

        Assert.Equal(3, indexer.Count);
        Assert.True(indexer.TryGetTokenId(2, out long tokenId));
        Assert.Equal(9, tokenId);
    }

    /// <summary>
    /// JSON null documents must be rejected with a parsing diagnostic.
    /// </summary>
    [Fact]
    public void Parse_NullJson_Throws()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicUnicodeIndexer.Parse("null"));

        Assert.Contains("Failed to parse unicode_indexer.json", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-array JSON documents must be rejected by the deserialiser.
    /// </summary>
    [Fact]
    public void Parse_NonArrayJson_Throws() => _ = Assert.Throws<JsonException>(() => SupertonicUnicodeIndexer.Parse(/*lang=json,strict*/ "{\"tokens\": [1]}"));

    /// <summary>
    /// Removes temporary indexer files created for file-based tests.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
