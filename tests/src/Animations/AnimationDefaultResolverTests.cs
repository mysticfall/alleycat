using AlleyCat.Animations;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.Tests.Animations.PackBuilder;

namespace AlleyCat.Tests.Animations;

/// <summary>ANIM-003 TR10: agent preference, then template default, then the catalogue fallback.</summary>
public sealed class AnimationDefaultResolverTests
{
    private static readonly IReadOnlyList<AnimationEntry> _entries =
        Pack("default", (AnimationCategory.Idle, "sad"), (AnimationCategory.Idle, "calm"), (AnimationCategory.Idle, "proud")).Entries;

    private static string? Resolve(string? preference, string? templateDefault, CapturingLogger? logger = null)
        => AnimationDefaultResolver.Resolve(AnimationCategory.Idle, _entries, preference, templateDefault, logger ?? new CapturingLogger())?.ID;

    /// <summary>Each source wins over the ones after it.</summary>
    [Fact]
    public void Resolve_FollowsPreferenceThenDefaultThenFallback()
    {
        Assert.Equal("proud", Resolve("proud", "calm"));
        Assert.Equal("calm", Resolve(null, "calm"));
        Assert.Equal("sad", Resolve(null, null));
        Assert.Equal("sad", Resolve("", ""));
    }

    /// <summary>An unknown or incompatible id is ignored with a log message and resolution continues.</summary>
    [Fact]
    public void Resolve_IgnoresUnknownIDsWithLog()
    {
        var logger = new CapturingLogger();

        Assert.Equal("calm", Resolve("angry", "calm", logger));
        Assert.Equal("sad", Resolve(null, "missing", logger));

        Assert.Equal(2, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Warning, entry.Level));
        Assert.Contains("agent preference 'angry'", logger.Entries[0].Message, StringComparison.Ordinal);
        Assert.Contains("template default 'missing'", logger.Entries[1].Message, StringComparison.Ordinal);
    }

    /// <summary>The fallback is the first compatible entry in catalogue order, including an overriding entry's position.</summary>
    [Fact]
    public void Resolve_FallbackUsesOverridingEntryPosition()
    {
        IReadOnlyList<AnimationEntry> composed = AnimationCatalogueComposer.Compose(
        [
            new AnimationPackLayer(Pack("default", (AnimationCategory.Idle, "sad"), (AnimationCategory.Idle, "calm")), 0),
            new AnimationPackLayer(Pack("extension", (AnimationCategory.Idle, "sad")), 1)
        ], new CapturingLogger());

        AnimationEntry? fallback = AnimationDefaultResolver.Resolve(AnimationCategory.Idle, composed, null, null, new CapturingLogger());

        Assert.Equal(("calm", "default"), (fallback?.ID, fallback?.PackID));
    }

    /// <summary>A category without compatible entries resolves to nothing.</summary>
    [Fact]
    public void Resolve_EmptyCategoryReturnsNull()
        => Assert.Null(AnimationDefaultResolver.Resolve(AnimationCategory.Locomotion, [], "x", "y", new CapturingLogger()));
}
