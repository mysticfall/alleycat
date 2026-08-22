using AlleyCat.Speech.Generation.Supertonic;
using Xunit;

namespace AlleyCat.Tests.Speech;

/// <summary>
/// Unit coverage for Supertonic voice-style name resolution and fallback semantics.
/// </summary>
public sealed class SupertonicVoiceStyleResolverTests : IDisposable
{
    private readonly string _voiceStylesDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Effective voice names must follow the override &gt; voice &gt; fallback precedence with trimming.
    /// </summary>
    [Theory]
    [InlineData(null, "F3", "F3")]
    [InlineData(null, " M2 ", "M2")]
    [InlineData(" ", "F3", "F3")]
    [InlineData("", "F1", "F1")]
    [InlineData("F5 ", "F3", "F5")]
    [InlineData(" F4", "F3", "F4")]
    [InlineData("F5", "", "F5")]
    [InlineData(null, "", "M1")]
    [InlineData(null, "   ", "M1")]
    [InlineData("   ", "   ", "M1")]
    public void ResolveEffectiveName_FollowsOverrideVoiceFallbackPrecedence(string? voiceOverride, string voice, string expected)
    {
        string effectiveName = SupertonicVoiceStyleResolver.ResolveEffectiveName(voiceOverride, voice);

        Assert.Equal(expected, effectiveName);
    }

    /// <summary>
    /// A preset whose JSON file exists must resolve onto its own file without fallback.
    /// </summary>
    [Fact]
    public void Resolve_KnownVoiceStyle_ResolvesOwnFileWithoutFallback()
    {
        CreateVoiceStyleFiles("M1", "F3");

        SupertonicVoiceStyleResolution resolution = SupertonicVoiceStyleResolver.Resolve(
            _voiceStylesDirectory,
            voiceOverride: null,
            voice: "F3");

        Assert.Equal("F3", resolution.RequestedName);
        Assert.Equal("F3", resolution.ResolvedName);
        Assert.False(resolution.UsedFallback);
        Assert.EndsWith("F3.json", resolution.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(resolution.FilePath));
    }

    /// <summary>
    /// An override must win over the configured voice, even when the voice itself is valid.
    /// </summary>
    [Fact]
    public void Resolve_VoiceOverrideTakesPrecedenceOverConfiguredVoice()
    {
        CreateVoiceStyleFiles("M1", "F3", "F5");

        SupertonicVoiceStyleResolution resolution = SupertonicVoiceStyleResolver.Resolve(
            _voiceStylesDirectory,
            voiceOverride: "F5",
            voice: "F3");

        Assert.Equal("F5", resolution.ResolvedName);
        Assert.False(resolution.UsedFallback);
    }

    /// <summary>
    /// Unknown style names must signal fallback and resolve onto the "M1" default so generation can continue.
    /// </summary>
    [Fact]
    public void Resolve_UnknownVoiceStyle_SignalsFallbackToDefaultVoice()
    {
        CreateVoiceStyleFiles("M1");

        SupertonicVoiceStyleResolution resolution = SupertonicVoiceStyleResolver.Resolve(
            _voiceStylesDirectory,
            voiceOverride: null,
            voice: "Z9");

        Assert.Equal("Z9", resolution.RequestedName);
        Assert.Equal(SupertonicVoiceStyleResolver.FallbackVoiceName, resolution.ResolvedName);
        Assert.True(resolution.UsedFallback);
        Assert.EndsWith("M1.json", resolution.FilePath, StringComparison.Ordinal);
        Assert.True(File.Exists(resolution.FilePath));
    }

    /// <summary>
    /// A whitespace-only override must be ignored so an unknown configured voice still falls back to "M1".
    /// </summary>
    [Fact]
    public void Resolve_WhitespaceOverrideWithUnknownVoice_FallsBackToDefaultVoice()
    {
        CreateVoiceStyleFiles("M1");

        SupertonicVoiceStyleResolution resolution = SupertonicVoiceStyleResolver.Resolve(
            _voiceStylesDirectory,
            voiceOverride: "   ",
            voice: "Z9");

        Assert.Equal("Z9", resolution.RequestedName);
        Assert.Equal("M1", resolution.ResolvedName);
        Assert.True(resolution.UsedFallback);
    }

    /// <summary>
    /// A missing directory must be rejected as a caller error rather than silently falling back.
    /// </summary>
    [Fact]
    public void Resolve_NullDirectory_Throws()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
            () => SupertonicVoiceStyleResolver.Resolve(null!, voiceOverride: null, voice: "M1"));

        Assert.Equal("voiceStylesDirectory", ex.ParamName);
    }

    private void CreateVoiceStyleFiles(params string[] voiceNames)
    {
        _ = Directory.CreateDirectory(_voiceStylesDirectory);
        foreach (string voiceName in voiceNames)
        {
            File.WriteAllText(Path.Combine(_voiceStylesDirectory, $"{voiceName}.json"), "{}");
        }
    }

    /// <summary>
    /// Removes temporary voice-style files created for directory-based tests.
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_voiceStylesDirectory))
        {
            Directory.Delete(_voiceStylesDirectory, recursive: true);
        }
    }
}
