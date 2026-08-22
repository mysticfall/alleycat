using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic synthesis pipeline validation and utterance chunking logic.
/// </summary>
public sealed class SupertonicInferencePipelineTests
{
    private const int MaximumChunkLength = SupertonicInferencePipeline.MaximumChunkLength;

    /// <summary>
    /// Quality-step counts below one must be rejected because the flow-matching loop needs at least one pass.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ValidateQualitySteps_BelowOne_Throws(int qualitySteps)
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SupertonicInferencePipeline.ValidateQualitySteps(qualitySteps));

        Assert.Contains("quality steps must be at least 1", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Positive quality-step counts, including the shipped default and its tuning range, must be accepted.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(12)]
    public void ValidateQualitySteps_PositiveValues_Accepts(int qualitySteps) => SupertonicInferencePipeline.ValidateQualitySteps(qualitySteps);

    /// <summary>
    /// Short utterances must stay a single chunk without modification apart from trimming.
    /// </summary>
    [Fact]
    public void SplitIntoChunks_ShortUtterance_YieldsSingleTrimmedChunk()
    {
        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks("  Hello there, friend.  ", MaximumChunkLength);

        string chunk = Assert.Single(chunks);
        Assert.Equal("Hello there, friend.", chunk);
    }

    /// <summary>
    /// Sentences must be merged greedily while they fit within the chunk limit.
    /// </summary>
    [Fact]
    public void SplitIntoChunks_AdjacentSentencesWithinLimit_MergeIntoOneChunk()
    {
        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks(
            "Hello there. Welcome to the alley.",
            MaximumChunkLength);

        string chunk = Assert.Single(chunks);
        Assert.Equal("Hello there. Welcome to the alley.", chunk);
    }

    /// <summary>
    /// Oversised utterances must split at sentence boundaries once the merged size would exceed the limit.
    /// </summary>
    [Fact]
    public void SplitIntoChunks_SentencesExceedingLimit_SplitAtSentenceBoundaries()
    {
        string firstSentence = CreateSentence('a', letterCount: 99);
        string secondSentence = CreateSentence('b', letterCount: 99);
        string thirdSentence = CreateSentence('c', letterCount: 149);

        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks(
            $"{firstSentence} {secondSentence} {thirdSentence}",
            MaximumChunkLength);

        Assert.Equal(2, chunks.Count);
        Assert.Equal($"{firstSentence} {secondSentence}", chunks[0]);
        Assert.Equal(201, chunks[0].Length);
        Assert.Equal(thirdSentence, chunks[1]);
    }

    /// <summary>
    /// Paragraph boundaries must always start new chunks even when the paragraphs would fit merged.
    /// </summary>
    [Fact]
    public void SplitIntoChunks_MultipleParagraphs_NeverMergeAcrossParagraphs()
    {
        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks(
            "First paragraph.\n\nSecond paragraph.",
            MaximumChunkLength);

        Assert.Equal(["First paragraph.", "Second paragraph."], chunks);
    }

    /// <summary>
    /// Common abbreviations must not be treated as sentence boundaries.
    /// </summary>
    [Fact]
    public void SplitIntoChunks_AbbreviatedTitles_DoNotSplitSentences()
    {
        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks(
            "Mr. Smith arrived. He left.",
            MaximumChunkLength);

        string chunk = Assert.Single(chunks);
        Assert.Equal("Mr. Smith arrived. He left.", chunk);
    }

    /// <summary>
    /// Blank utterances must still yield one chunk so synthesis receives processable input.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SplitIntoChunks_BlankText_YieldsSingleTrimmedChunk(string text)
    {
        List<string> chunks = SupertonicInferencePipeline.SplitIntoChunks(text, MaximumChunkLength);

        string chunk = Assert.Single(chunks);
        Assert.Equal(text.Trim(), chunk);
    }

    private static string CreateSentence(char letter, int letterCount)
        => new string(letter, letterCount) + ".";
}
