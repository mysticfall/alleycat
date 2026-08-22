using System.Text.Json;
using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic voice-style JSON parsing.
/// </summary>
public sealed class SupertonicVoiceStyleTests : IDisposable
{
    private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A real-shape style document — nested tensor data, dims, and mixed scalar metadata — must parse into
    /// flattened tensors with their declared shapes.
    /// </summary>
    [Fact]
    public void Parse_RealShapeStyleJson_LoadsTensorsShapesAndMetadata()
    {
        const string json = /*lang=json,strict*/ """
            {
              "style_ttl": { "dims": [1, 2, 3], "type": "float32", "data": [[[1, 2, 3], [4, 5, 6]]] },
              "style_dp": { "dims": [1, 2, 2], "type": "float32", "data": [[[7, 8], [9, 10]]] },
              "metadata": {
                "source_file": "M1.wav",
                "source_sample_rate": 44100,
                "active": true,
                "nested": { "ignored": 1 },
                "items": [1, 2],
                "nothing": null
              }
            }
            """;

        var style = SupertonicVoiceStyle.Parse(json);

        Assert.Equal([1f, 2f, 3f, 4f, 5f, 6f], style.TtlData);
        Assert.Equal([1L, 2L, 3L], style.TtlShape);
        Assert.Equal([7f, 8f, 9f, 10f], style.DpData);
        Assert.Equal([1L, 2L, 2L], style.DpShape);

        Assert.Equal(3, style.Metadata.Count);
        Assert.Equal("M1.wav", style.Metadata["source_file"]);
        Assert.Equal("44100", style.Metadata["source_sample_rate"]);
        Assert.Equal("true", style.Metadata["active"]);
    }

    /// <summary>
    /// Style files must be loadable from disk, matching the runtime asset-loading path.
    /// </summary>
    [Fact]
    public void Load_StyleFile_ParsesTensorsFromDisk()
    {
        string stylePath = WriteTemporaryStyleFile(/*lang=json,strict*/ """
            {
              "style_ttl": { "dims": [1, 1, 1], "type": "float32", "data": [[[0.25]]] },
              "style_dp": { "dims": [1, 1, 1], "type": "float32", "data": [[[-0.5]]] }
            }
            """);

        var style = SupertonicVoiceStyle.Load(stylePath);

        Assert.Equal([0.25f], style.TtlData);
        Assert.Equal([-0.5f], style.DpData);
        Assert.Empty(style.Metadata);
    }

    /// <summary>
    /// Styles without a metadata object must parse with empty metadata rather than failing.
    /// </summary>
    [Fact]
    public void Parse_StyleWithoutMetadata_YieldsEmptyMetadata()
    {
        const string json = /*lang=json,strict*/ """
            {
              "style_ttl": { "dims": [1, 1, 1], "data": [[[1]]] },
              "style_dp": { "dims": [1, 1, 1], "data": [[[1]]] }
            }
            """;

        var style = SupertonicVoiceStyle.Parse(json);

        Assert.Empty(style.Metadata);
    }

    /// <summary>
    /// Missing tensor entries must be rejected with a clear diagnostic naming the entry.
    /// </summary>
    [Theory]
    [InlineData("style_ttl")]
    [InlineData("style_dp")]
    public void Parse_MissingTensorEntry_Throws(string missingEntry)
    {
        string json = $$"""
            {
              "style_ttl": { "dims": [1, 1, 1], "data": [[[1]]] },
              "style_dp": { "dims": [1, 1, 1], "data": [[[2]]] }
            }
            """;

        string jsonWithoutEntry = json.Replace($"\"{missingEntry}\"", $"\"{missingEntry}_removed\"", StringComparison.Ordinal);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SupertonicVoiceStyle.Parse(jsonWithoutEntry));

        Assert.Contains($"missing the '{missingEntry}'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tensor entries without dims must be rejected because the flattened length cannot be validated.
    /// </summary>
    [Fact]
    public void Parse_TensorWithoutDims_Throws()
    {
        const string json = /*lang=json,strict*/ """
            {
              "style_ttl": { "data": [[[1, 2]]] },
              "style_dp": { "dims": [1, 1, 2], "data": [[[3, 4]]] }
            }
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SupertonicVoiceStyle.Parse(json));

        Assert.Contains("missing 'dims'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tensor data whose element count disagrees with the declared dims must be rejected.
    /// </summary>
    [Fact]
    public void Parse_TensorDataLengthDisagreeingWithDims_Throws()
    {
        const string json = /*lang=json,strict*/ """
            {
              "style_ttl": { "dims": [1, 2, 3], "data": [[[1, 2, 3], [4, 5]]] },
              "style_dp": { "dims": [1, 1, 1], "data": [[[1]]] }
            }
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SupertonicVoiceStyle.Parse(json));

        Assert.Contains("declares 6 values from dims but contains 5", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Malformed JSON documents must be rejected by the parser.
    /// </summary>
    [Fact]
    public void Parse_MalformedJson_Throws()
        => _ = Assert.ThrowsAny<JsonException>(() => SupertonicVoiceStyle.Parse("not a style document"));

    private string WriteTemporaryStyleFile(string json)
    {
        _ = Directory.CreateDirectory(_temporaryDirectory);
        string stylePath = Path.Combine(_temporaryDirectory, "style.json");
        File.WriteAllText(stylePath, json);
        return stylePath;
    }

    /// <summary>
    /// Removes temporary style files created for file-based tests.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }
}
