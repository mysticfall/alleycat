using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic <c>tts.json</c> configuration parsing.
/// </summary>
public sealed class SupertonicModelConfigTests
{
    /// <summary>
    /// The shipped Supertonic model configuration must expose the pipeline constants downstream code relies on,
    /// including the 44100 Hz vocoder rate assumed by the WAV output contract.
    /// </summary>
    [Fact]
    public void Load_ShippedModelConfig_ReadsPipelineConstants()
    {
        string configPath = RepositoryPath.Get("game", "models", "supertonic-3", "onnx", "tts.json");

        var config = SupertonicModelConfig.Load(configPath);

        Assert.Equal(44100, config.SampleRate);
        Assert.Equal(512, config.BaseChunkSize);
        Assert.Equal(6, config.ChunkCompressFactor);
        Assert.Equal(24, config.LatentDim);
    }

    /// <summary>
    /// Section entries must map onto their typed record properties.
    /// </summary>
    [Fact]
    public void Parse_ValidConfig_MapsSectionEntries()
    {
        const string json = /*lang=json,strict*/ """
            {
              "ae": { "sample_rate": 24000, "base_chunk_size": 256 },
              "ttl": { "chunk_compress_factor": 2, "latent_dim": 8 }
            }
            """;

        var config = SupertonicModelConfig.Parse(json);

        Assert.Equal(24000, config.SampleRate);
        Assert.Equal(256, config.BaseChunkSize);
        Assert.Equal(2, config.ChunkCompressFactor);
        Assert.Equal(8, config.LatentDim);
    }

    /// <summary>
    /// Required entries that are absent must be rejected with a diagnostic naming the section and property.
    /// </summary>
    [Theory]
    [InlineData("ae", "sample_rate")]
    [InlineData("ae", "base_chunk_size")]
    [InlineData("ttl", "chunk_compress_factor")]
    [InlineData("ttl", "latent_dim")]
    public void Parse_MissingEntry_Throws(string section, string property)
    {
        const string json = /*lang=json,strict*/ """
            {
              "ae": { "sample_rate": 44100, "base_chunk_size": 512 },
              "ttl": { "chunk_compress_factor": 6, "latent_dim": 24 }
            }
            """;

        string partialJson = json.Replace($"\"{property}\": ", $"\"{property}_missing\": ", StringComparison.Ordinal);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicModelConfig.Parse(partialJson));

        Assert.Contains($"'{section}.{property}'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Missing whole sections must be rejected with a diagnostic naming the section and property.
    /// </summary>
    [Fact]
    public void Parse_MissingSection_Throws()
    {
        const string json = /*lang=json,strict*/ """
            {
              "ttl": { "chunk_compress_factor": 6, "latent_dim": 24 }
            }
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SupertonicModelConfig.Parse(json));

        Assert.Contains("'ae.sample_rate'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-positive values must be rejected because they break latent-size arithmetic.
    /// </summary>
    [Theory]
    [InlineData("sample_rate", 0)]
    [InlineData("base_chunk_size", -512)]
    [InlineData("latent_dim", 0)]
    public void Parse_NonPositiveEntry_Throws(string property, int value)
    {
        string json = $$"""
            {
              "ae": { "sample_rate": 44100, "base_chunk_size": 512 },
              "ttl": { "chunk_compress_factor": 6, "latent_dim": 24 }
            }
            """;

        string nonPositiveJson = json.Replace($"\"{property}\": ", $"\"{property}\": {value}, \"x\": ", StringComparison.Ordinal);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicModelConfig.Parse(nonPositiveJson));

        Assert.Contains("must be greater than zero", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Non-integer values must be rejected so silent JSON coercion cannot produce wrong constants.
    /// </summary>
    [Fact]
    public void Parse_NonIntegerEntry_Throws()
    {
        const string json = /*lang=json,strict*/ """
            {
              "ae": { "sample_rate": "44100", "base_chunk_size": 512 },
              "ttl": { "chunk_compress_factor": 6, "latent_dim": 24 }
            }
            """;

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SupertonicModelConfig.Parse(json));

        Assert.Contains("'ae.sample_rate'", ex.Message, StringComparison.Ordinal);
    }
}
