using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for Markdown-backed perspective lore prompt injection.
/// </summary>
[Headless]
public sealed class EssentialLoreIntegrationTests
{
    /// <summary>
    /// Vadim's essential world lore is selected from his perspective and sorted by stable ID when priorities tie.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SelectsEssentialWorldLoreForNormalisedObserver()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(
            ContentContext.Default,
            LoreQuery.Essential("VADIM"));

        Assert.Equal(
            ["vadim.charter", "vadim.charter_office", "vadim.peoples", "vadim.reclassification"],
            entries.Select(entry => entry.ID));
        Assert.All(entries, entry => Assert.Equal(LoreSubjectKind.World, entry.Kind));
        Assert.Contains("operating logic", entries[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contextual results retain request grouping and match normalised subject IDs irrespective of essential.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SelectsContextualBatchInRequestOrder()
    {
        MarkdownLoreQueryService service = new();
        LoreQuery query = new(
            "Vadim",
            [
                LoreSubjectRequest.Character("CHARACTER.ALLY"),
                LoreSubjectRequest.Location("LOCATION.INTERROGATION_ROOM"),
                LoreSubjectRequest.Character("character.vadim"),
            ]);

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(ContentContext.Default, query);

        Assert.Equal(["vadim.ally", "vadim.interrogation_room", "vadim.self"], entries.Select(entry => entry.ID));
        Assert.Equal(
            [LoreSubjectKind.Character, LoreSubjectKind.Location, LoreSubjectKind.Character],
            entries.Select(entry => entry.Kind));
        Assert.Equal(
            ["character.ally", "location.interrogation_room", "character.vadim"],
            entries.Select(entry => entry.SubjectID));
    }

    /// <summary>
    /// An absent perspective is absent knowledge and never falls back to the canonical wiki.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WhenPerspectiveIsAbsent_DoesNotUseCanonicalLore()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(
            ContentContext.Default,
            LoreQuery.Essential("observer-without-perspective"));

        Assert.Empty(entries);
    }

    /// <summary>
    /// Missing optional IDs use title and source path after entries with stable IDs at the same priority.
    /// </summary>
    [Fact]
    public async Task QueryAsync_WhenEntryIDIsMissing_UsesDeterministicFallbackOrdering()
    {
        MarkdownLoreQueryService service = new();
        ContentContext content = new("lore-query-fixture", "res://tests/lore-query-fixture");

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(content, LoreQuery.Essential("test"));

        Assert.Collection(
            entries,
            entry => Assert.Equal("test.stable", entry.ID),
            entry =>
            {
                Assert.Null(entry.ID);
                Assert.Equal("First source-path fallback entry.", entry.Body);
            },
            entry =>
            {
                Assert.Null(entry.ID);
                Assert.Equal("Second source-path fallback entry.", entry.Body);
            });
    }

    /// <summary>
    /// EssentialLorePromptSection consumes the associated observer query supplied by the prompt-build seam.
    /// </summary>
    [Fact]
    public async Task EssentialLorePromptSection_FormatsObserverPerspectiveWithTitleTags()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, PseudoXmlLorePromptFormatter>()
            .BuildServiceProvider();
        SceneContext scene = new([], ContentContext.Default);
        EssentialLorePromptSection section = new()
        {
            Name = "Essential Lore",
        };
        PromptOwnerCharacter character = new("Vadim");
        PromptSectionBuildContext buildContext = new(services, scene, character);

        string content = await section.GetContentAsync(buildContext);

        Assert.Contains("<The Charter>\n", content, StringComparison.Ordinal);
        Assert.Contains("operating logic", content, StringComparison.Ordinal);
        Assert.Contains("\n</The Charter>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<lore_entry>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("AlleyCat Sanctuary", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Lore-enabled prompt use reports an unnamed observer at the section usage boundary.
    /// </summary>
    [Fact]
    public async Task EssentialLorePromptSection_WhenObserverIDIsEmpty_FailsClearlyAtUsageBoundary()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        SceneContext scene = new([], ContentContext.Default);
        EssentialLorePromptSection section = new();
        PromptOwnerCharacter character = new(string.Empty);
        PromptSectionBuildContext buildContext = new(services, scene, character);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(buildContext));

        Assert.Contains("EssentialLorePromptSection", exception.Message, StringComparison.Ordinal);
        Assert.Contains("observer ID", exception.Message, StringComparison.Ordinal);
        _ = Assert.IsType<ArgumentException>(exception.InnerException);
    }

    /// <summary>
    /// The default formatter uses each lore title as the pseudo-XML tag and trims only the body boundary whitespace.
    /// </summary>
    [Fact]
    public void PseudoXmlLorePromptFormatter_UsesSanitisedTitleAsTagAndTrimmedBodyContent()
    {
        PseudoXmlLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "faction.rank.elite",
                "Faction/Rank <Elite>",
                "\nCanon includes / slashes.\n\n"),
        ]);

        Assert.Equal(
            "<Faction_Rank _Elite_>\n" +
            "Canon includes / slashes.\n" +
            "</Faction_Rank _Elite_>",
            content);
        Assert.DoesNotContain("<lore_entry>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<body>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<source>", content, StringComparison.Ordinal);
    }
}
