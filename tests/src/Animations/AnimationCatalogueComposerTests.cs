using AlleyCat.Animations;
using Microsoft.Extensions.Logging;
using Xunit;
using static AlleyCat.Tests.Animations.PackBuilder;

namespace AlleyCat.Tests.Animations;

/// <summary>ANIM-003 TR7, TR8 and TR10: layering order, overrides, override logging and duplicate pack ids.</summary>
public sealed class AnimationCatalogueComposerTests
{
    private const AnimationCategory Idle = AnimationCategory.Idle;
    private const AnimationCategory Locomotion = AnimationCategory.Locomotion;

    /// <summary>Packs apply in ascending priority regardless of input order, then manifest order within each pack.</summary>
    [Fact]
    public void Compose_OrdersByPriorityThenManifest()
    {
        var logger = new CapturingLogger();
        IReadOnlyList<AnimationEntry> entries = AnimationCatalogueComposer.Compose(
        [
            new AnimationPackLayer(Pack("combat", (Idle, "guard"), (Locomotion, "stalk")), 10),
            new AnimationPackLayer(Pack("default", (Idle, "sad"), (Locomotion, "feminine")), 0)
        ], logger);

        Assert.Equal(["default:sad", "default:feminine", "combat:guard", "combat:stalk"],
            entries.Select(e => $"{e.PackID}:{e.ID}"));
        Assert.Empty(logger.Entries);
    }

    /// <summary>
    /// A later-applied pack overrides by (category, id); the override takes the overriding pack's position and logs
    /// the category, entry, overriding and overridden pack.
    /// </summary>
    [Fact]
    public void Compose_OverrideReplacesEntryAndLogs()
    {
        var logger = new CapturingLogger();
        IReadOnlyList<AnimationEntry> entries = AnimationCatalogueComposer.Compose(
        [
            new AnimationPackLayer(Pack("default", (Idle, "sad"), (Idle, "calm"), (Locomotion, "sad")), 0),
            new AnimationPackLayer(Pack("extension", (Idle, "sad"), (Locomotion, "stroll")), 5)
        ], logger);

        Assert.Equal(["default:calm", "default:sad", "extension:sad", "extension:stroll"],
            entries.Select(e => $"{e.PackID}:{e.ID}"));
        Assert.Equal(Locomotion, entries[1].Category);
        Assert.Equal(Idle, entries[2].Category);
        (LogLevel level, string message) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, level);
        Assert.Equal("Animation catalogue: idle entry 'sad' from pack 'extension' overrides pack 'default'", message);
    }

    /// <summary>Equal priorities apply in ordinal pack-id order with the same override log message.</summary>
    [Fact]
    public void Compose_EqualPriorityUsesOrdinalPackID()
    {
        var logger = new CapturingLogger();
        IReadOnlyList<AnimationEntry> entries = AnimationCatalogueComposer.Compose(
        [
            new AnimationPackLayer(Pack("beta", (Idle, "sad")), 3),
            new AnimationPackLayer(Pack("Zeta", (Idle, "calm")), 3),
            new AnimationPackLayer(Pack("alpha", (Idle, "sad")), 3)
        ], logger);

        // Ordinal ordering: "Zeta" < "alpha" < "beta".
        Assert.Equal(["Zeta:calm", "beta:sad"], entries.Select(e => $"{e.PackID}:{e.ID}"));
        Assert.Equal("Animation catalogue: idle entry 'sad' from pack 'beta' overrides pack 'alpha'", Assert.Single(logger.Entries).Message);
    }

    /// <summary>Two enabled packs with one pack id fail construction clearly.</summary>
    [Fact]
    public void Compose_DuplicatePackIDThrows()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => AnimationCatalogueComposer.Compose(
        [
            new AnimationPackLayer(Pack("default", (Idle, "sad")), 0),
            new AnimationPackLayer(Pack("default", (Idle, "calm")), 4)
        ], new CapturingLogger()));

        Assert.Contains("'default'", error.Message, StringComparison.Ordinal);
    }
}
