using System.Text;
using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic text normalisation and tokenisation.
/// </summary>
/// <remarks>
/// Tests reconstruct the processed utterance from token IDs by supplying an identity indexer whose token IDs
/// equal their code points, keeping assertions independent of the shipped token table.
/// </remarks>
public sealed class SupertonicTextProcessorTests
{
    private const int IdentityTableLimit = 1024;

    /// <summary>
    /// Representative inputs and the exact processed strings the encoder must produce.
    /// </summary>
    public static TheoryData<string, string, string> NormalisationCases => new()
    {
        // Plain sentence: unchanged apart from language wrapping.
        { "Hello there, friend.", "en", "<en>Hello there, friend.</en>" },
        // Missing terminal punctuation gets a period appended.
        { "Hello there", "en", "<en>Hello there.</en>" },
        // Emojis are removed and the spacing before punctuation collapses.
        { "Hi 😀!", "en", "<en>Hi!</en>" },
        // Smart quotes and dashes normalise to ASCII equivalents.
        { "“Hello” — friend", "en", "<en>\"Hello\" - friend.</en>" },
        // Expression symbols expand to spoken words.
        { "Meet me @ noon, e.g., soon.", "en", "<en>Meet me at noon, for example, soon.</en>" },
        // Underscores and brackets become spaces and whitespace collapses.
        { "a_b [c]#d", "en", "<en>a b c d.</en>" },
        // Doubled straight quotes collapse to single quotes.
        { "Say \"\"hi\"\".", "en", "<en>Say \"hi\".</en>" },
        // Runs of whitespace collapse to single spaces before trimming.
        { "  spaced \n\t out  ", "en", "<en>spaced out.</en>" },
        // Special symbols are removed entirely.
        { "© rights \\ reserved", "en", "<en>rights reserved.</en>" },
        // Compatibility decomposition splits accented letters.
        { "Café", "en", "<en>Cafe\u0301.</en>" },
        // Non-English languages wrap in their own tags.
        { "Hallo", "de", "<de>Hallo.</de>" },
    };

    /// <summary>
    /// Normalised text must be wrapped in language tags with terminal punctuation guaranteed.
    /// </summary>
    [Theory]
    [MemberData(nameof(NormalisationCases))]
    public void Encode_RepresentativeText_NormalisesAndWrapsInLanguageTags(string text, string language, string expected)
    {
        SupertonicTextProcessor processor = CreateIdentityProcessor();

        SupertonicTextEncoding encoding = processor.Encode(text, language);

        Assert.Equal(expected, ReconstructProcessedText(encoding));
        Assert.Equal(expected.Length, encoding.TokenIds.Length);
    }

    /// <summary>
    /// The padding mask must be a single fully-enabled row aligned with the token count.
    /// </summary>
    [Fact]
    public void Encode_AnyText_ProducesFullyEnabledPaddingMask()
    {
        SupertonicTextProcessor processor = CreateIdentityProcessor();

        SupertonicTextEncoding encoding = processor.Encode("Hello there, friend.", "en");

        Assert.Equal(encoding.TokenIds.Length, encoding.PaddingMask.Length);
        Assert.All(encoding.PaddingMask, value => Assert.Equal(1f, value));
    }

    /// <summary>
    /// Characters without an indexer entry must keep the default zero token ID.
    /// </summary>
    [Fact]
    public void Encode_UnindexedCharacters_KeepDefaultTokenId()
    {
        SupertonicTextProcessor processor = new(SupertonicUnicodeIndexer.Parse("[5]"));

        SupertonicTextEncoding encoding = processor.Encode("A", "en");

        Assert.Equal("<en>A.</en>".Length, encoding.TokenIds.Length);
        Assert.All(encoding.TokenIds, tokenId => Assert.Equal(0, tokenId));
    }

    /// <summary>
    /// Unsupported language tags must fail fast with the supported set listed.
    /// </summary>
    [Theory]
    [InlineData("zz")]
    [InlineData("")]
    public void Encode_InvalidLanguage_Throws(string language)
    {
        SupertonicTextProcessor processor = CreateIdentityProcessor();

        ArgumentException ex = Assert.Throws<ArgumentException>(() => processor.Encode("Hello", language));

        Assert.Contains($"Invalid language: {language}", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Null text must be rejected.
    /// </summary>
    [Fact]
    public void Encode_NullText_Throws()
    {
        SupertonicTextProcessor processor = CreateIdentityProcessor();

        _ = Assert.Throws<ArgumentNullException>(() => processor.Encode(null!, "en"));
    }

    /// <summary>
    /// A null indexer must be rejected at construction.
    /// </summary>
    [Fact]
    public void Constructor_NullIndexer_Throws() => _ = Assert.Throws<ArgumentNullException>(() => new SupertonicTextProcessor(null!));

    private static SupertonicTextProcessor CreateIdentityProcessor()
    {
        StringBuilder json = new(IdentityTableLimit * 3);
        _ = json.Append('[');
        for (int codePoint = 0; codePoint < IdentityTableLimit; codePoint++)
        {
            if (codePoint > 0)
            {
                _ = json.Append(',');
            }

            _ = json.Append(codePoint);
        }

        _ = json.Append(']');
        return new SupertonicTextProcessor(SupertonicUnicodeIndexer.Parse(json.ToString()));
    }

    private static string ReconstructProcessedText(SupertonicTextEncoding encoding)
        => new([.. encoding.TokenIds.Select(tokenId => (char)tokenId)]);
}
