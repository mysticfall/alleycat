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
/// Godot-runtime coverage for AI-004's grouped catalogue prompt section over the Markdown lore backend: every
/// remaining eligible entry listed exactly once with exact ID, title, and optional description, in World,
/// Characters, Locations group order with automatic-injection entries excluded by entry ID through the shared
/// selection logic, and unchanged automatic bodies alongside it (AI-004 requirements 44 to 46; AI-003 TR-17).
/// </summary>
[Headless]
public sealed class LoreCataloguePromptSectionIntegrationTests
{
    private static readonly ContentContext _fixture = new("lore-query-fixture", "res://tests/lore-query-fixture");

    /// <summary>
    /// The catalogue lists every remaining eligible entry exactly once, groups them in World, Characters, Locations
    /// order, omits the empty Characters group, and carries optional descriptions without bodies — every row shows
    /// the entry's backticked canonical full ID. The automatically injected essential-world entries
    /// (<c>world:with-id</c>, <c>world:with-id-authoring-material</c>, <c>world:notes/nested</c>,
    /// <c>world:without-id-a</c>, <c>world:without-id-b</c>) and scene-character entries (<c>char:ally</c>,
    /// <c>char:vadim</c>) are excluded by canonical ID while their fixture-authored essential flags stay untouched.
    /// </summary>
    [Fact]
    public async Task CatalogueSection_ListsRemainingEntriesGrouped_ExcludingAutomaticSelectionByID()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .AddSingleton<ILoreCataloguePromptFormatter, MarkdownLoreCataloguePromptFormatter>()
            .BuildServiceProvider();
        PromptOwnerCharacter owner = new("test");
        PromptOwnerCharacter ally = new("ally");
        PromptOwnerCharacter vadim = new("vadim");
        SceneContext scene = new([ally, owner, vadim], _fixture);
        LoreCataloguePromptSection section = new();
        PromptSectionBuildContext buildContext = new(services, scene, owner);

        string content = await section.GetContentAsync(buildContext);

        Assert.Equal(
            "# World\n\n"
            + "- `world:nonessential` — Non-Essential World Entry\n"
            + "- `world:shared` — Shared World Entry — Riverside routes I keep to myself.\n"
            + "\n"
            + "# Locations\n\n"
            + "- `loc:interrogation_room` — loc:interrogation_room — The room where they ask their questions.",
            content);
        Assert.DoesNotContain("# Characters", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`world:with-id`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`world:with-id-authoring-material`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`world:notes/nested`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`world:without-id-a`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`world:without-id-b`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`char:ally`", content, StringComparison.Ordinal);
        Assert.DoesNotContain("`char:vadim`", content, StringComparison.Ordinal);

        // Catalogue rows never carry bodies and never show authoring dot-handle ids.
        Assert.DoesNotContain("I walk them when I do not want to be found.", content, StringComparison.Ordinal);
        Assert.DoesNotContain("I have been inside once", content, StringComparison.Ordinal);
        Assert.DoesNotContain("World knowledge I have not internalised", content, StringComparison.Ordinal);
        Assert.DoesNotContain("test.", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// The automatic essential-world and scene-character bodies stay unchanged on the same fixture: the catalogue
    /// adds discovery without altering the automatically injected lore (AI-004 user requirement 4).
    /// </summary>
    [Fact]
    public async Task CatalogueSection_LeavesAutomaticEssentialAndSceneCharacterBodiesUnchanged()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .AddSingleton<ILoreCataloguePromptFormatter, MarkdownLoreCataloguePromptFormatter>()
            .BuildServiceProvider();
        PromptOwnerCharacter owner = new("test");
        PromptOwnerCharacter ally = new("ally");
        PromptOwnerCharacter vadim = new("vadim");
        SceneContext scene = new([owner, ally, vadim], _fixture);
        EssentialLorePromptSection essentialSection = new();
        CharacterLorePromptSection characterSection = new();
        PromptSectionBuildContext buildContext = new(services, scene, owner);

        string essential = await essentialSection.GetContentAsync(buildContext);
        string characters = await characterSection.GetContentAsync(buildContext);

        Assert.Contains("# Stable Entry\n\n`world:with-id`\n\nStable ID entry.", essential, StringComparison.Ordinal);
        Assert.Contains(
            "# Nested Note Entry\n\n`world:notes/nested`\n\nKept prose from a nested subdirectory page.",
            essential,
            StringComparison.Ordinal);
        Assert.Equal(
            [
                "# Authoring Material Entry",
                "# Nested Note Entry",
                "# Stable Entry",
                "# Fallback Entry",
                "# Fallback Entry",
            ],
            ExtractEntryTitleLines(essential));
        Assert.StartsWith("# char:ally\n", characters, StringComparison.Ordinal);
        Assert.Contains("Who I am, in my own words.", characters, StringComparison.Ordinal);
    }

    /// <summary>An absent perspective renders coherent empty catalogue content, never a canonical fallback.</summary>
    [Fact]
    public async Task CatalogueSection_WhenNoEntriesRemain_ReturnsEmptyContent()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .AddSingleton<ILoreCataloguePromptFormatter, MarkdownLoreCataloguePromptFormatter>()
            .BuildServiceProvider();
        PromptOwnerCharacter owner = new("observer_without_perspective");
        SceneContext scene = new([owner], _fixture);
        LoreCataloguePromptSection section = new();
        PromptSectionBuildContext buildContext = new(services, scene, owner);

        string content = await section.GetContentAsync(buildContext);

        Assert.Equal(string.Empty, content);
    }

    private static IReadOnlyList<string> ExtractEntryTitleLines(string formatted)
        => [.. formatted.Split('\n')
            .Where(static line => line.StartsWith("# ", StringComparison.Ordinal))
            .Select(static line => line.TrimEnd())];
}
