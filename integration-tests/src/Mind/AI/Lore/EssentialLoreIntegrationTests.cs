using AlleyCat.Core.Content;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for Markdown-backed essential lore prompt injection.
/// </summary>
[Headless]
public sealed class EssentialLoreIntegrationTests
{
    /// <summary>
    /// Essential lore queries read all pages marked essential under the active content lore wiki.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SelectsEssentialLoreFromDefaultContent()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(ContentContext.Default, LoreQuery.Essential());

        LoreEntry entry = Assert.Single(entries, entry => entry.ID == "alleycat.sanctuary");
        Assert.Equal("AlleyCat Sanctuary", entry.Title);
        Assert.Contains("fallback space", entry.Body, StringComparison.Ordinal);
        Assert.Equal("res://lore/wiki/setting/alleycat-sanctuary.md", entry.SourcePath);
    }

    /// <summary>
    /// EssentialLorePromptSection uses the lore query service and formatter through the prompt build context.
    /// </summary>
    [Fact]
    public async Task EssentialLorePromptSection_FormatsAllEssentialEntriesWithTitleTags()
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

        string content = await section.GetContentAsync(new PromptSectionBuildContext(services, scene));

        Assert.Contains("<AlleyCat Sanctuary>\n", content, StringComparison.Ordinal);
        Assert.Contains("fallback space", content, StringComparison.Ordinal);
        Assert.Contains("\n</AlleyCat Sanctuary>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<lore_entry>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<body>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<source>", content, StringComparison.Ordinal);
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
                "\nCanon includes / slashes.\n\n",
                "res://lore/wiki/faction.md"),
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
