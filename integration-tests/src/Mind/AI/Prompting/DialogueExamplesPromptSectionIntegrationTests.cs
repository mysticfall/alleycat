using System.Text.RegularExpressions;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot-runtime coverage for AI-012 character dialogue examples: verbatim file passthrough per character and
/// content root, empty content with unchanged wrapper emission when no examples are authored, a clear failure
/// for an existing-but-unreadable file, the section's wiring between Characters and Lore Catalogue in the
/// shared NPC stack with movable authored order, the shared guidance's mandatory concepts, the isolation
/// contract keeping example content out of every lore surface (AI-012 TR-1–TR-9), and template opacity at the
/// rendered-instruction level — only the sanctioned owner token resolves in examples, while existing template
/// sections keep substituting (AI-012 UR-2, TR-3).
/// </summary>
[Headless]
public sealed partial class DialogueExamplesPromptSectionIntegrationTests
{
    private const string SharedStackPath = "res://assets/characters/prompts/generic_npc_prompt_stack.tres";
    private const string SharedMindPromptPath = "res://prompts/mind.md";
    private const string FixtureDialoguePath = "res://tests/lore-query-fixture/lore/perspectives/char/test/dialogue.md";
    private const string FixtureDialogueMarker = "quicksilver tally, rivercoin, or favour";
    private const string OpacityDialoguePath = "res://tests/lore-query-fixture/lore/perspectives/char/opacity/dialogue.md";
    private const string MalformedDialoguePath = "res://tests/lore-query-fixture/lore/perspectives/char/malformed/dialogue.md";

    private static readonly ContentContext _fixture = new("lore-query-fixture", "res://tests/lore-query-fixture");

    /// <summary>
    /// With guidance disabled, examples preserve all text except the sanctioned owner token (AI-012 TR-3, UR-2).
    /// </summary>
    [Fact]
    public async Task GetContent_ReturnsExamplesWithOnlyOwnerTokenSubstituted()
    {
        DialogueExamplesPromptSection section = new()
        {
            GuidancePath = string.Empty
        };
        string content = await section.GetContentAsync(CreateBuildContext("test"));

        Assert.Equal(ExpectedExamples(FixtureDialoguePath, "char:test"), content);
        Assert.Contains(FixtureDialogueMarker, content, StringComparison.Ordinal);
    }

    /// <summary>
    /// A character without examples starts normally: a missing dialogue file yields empty section content and no
    /// error, both for an observer with a perspective folder and one without (AI-012 TR-5, UR-4).
    /// </summary>
    [Theory]
    [InlineData("other")]
    [InlineData("observer_without_perspective")]
    public async Task GetContent_WithoutDialogueFile_ReturnsEmptyContentWithoutError(string observerID)
    {
        DialogueExamplesPromptSection section = new();
        string content = await section.GetContentAsync(CreateBuildContext(observerID));

        Assert.Equal(string.Empty, content);
    }

    /// <summary>
    /// The shared writer still emits the section wrapper for the empty content of a missing file, unchanged from
    /// its behaviour for every other section (AI-012 TR-5).
    /// </summary>
    [Fact]
    public async Task MissingDialogueFile_KeepsUnchangedWrapperEmission()
    {
        DialogueExamplesPromptSection section = new()
        {
            Name = "Dialogue Examples"
        };
        PromptSectionBuildContext buildContext = CreateBuildContext("other");

        string source = await PseudoXmlPromptWriter.FormatAsync([section], buildContext);

        Assert.Equal(
            "<Dialogue Examples>\n\n</Dialogue Examples>\n\n",
            source,
            ignoreLineEndingDifferences: false);
    }

    /// <summary>
    /// A dialogue file that exists but cannot be opened fails clearly with the resolved path, exactly like the
    /// file prompt section's read failure (AI-012 TR-3 boundary; brief's unreadable-path contract).
    /// </summary>
    [Fact]
    public async Task GetContent_WhenFileExistsButCannotBeOpened_FailsClearly()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Fail("The existing-but-unreadable dialogue file vector requires Unix file permissions.");
        }

        string root = "user://ai012_unreadable/";
        string globalRoot = ProjectSettings.GlobalizePath(root);
        string globalFile = Path.Combine(globalRoot, "lore/perspectives/char/test/dialogue.md");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(globalFile)!);
        await File.WriteAllTextAsync(globalFile, "existing but unreadable");
        DenyAllUnixPermissions(globalFile);
        try
        {
            try
            {
                FileStream opened = File.OpenRead(globalFile);
                opened.Dispose();
                Assert.Fail(
                    "This environment bypasses file permissions (for example when running as root), so the "
                    + "existing-but-unreadable dialogue file state cannot be produced here.");
            }
            catch (UnauthorizedAccessException)
            {
                // The permission vector is armed: the file exists but cannot be read by this process.
            }

            DialogueExamplesPromptSection section = new();
            string path = root + "lore/perspectives/char/test/dialogue.md";

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => section.GetContentAsync(
                    new PromptSectionBuildContext(
                        new ServiceCollection().BuildServiceProvider(),
                        new SceneContext([new PromptOwnerCharacter("test")], new ContentContext("unreadable", root)),
                        new PromptOwnerCharacter("test"))));

            Assert.Contains(path, exception.Message, StringComparison.Ordinal);
            Assert.Contains("dialogue examples", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            RestoreOwnerUnixPermissions(globalFile);
            if (File.Exists(globalFile))
            {
                File.Delete(globalFile);
            }

            if (Directory.Exists(globalRoot))
            {
                Directory.Delete(globalRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// Arms the existing-but-unreadable vector on Linux; the fact's platform gate keeps non-Linux runs out before
    /// this ever matters. The positive guard is the platform-analyser-recognised form for this Unix-only API.
    /// </summary>
    private static void DenyAllUnixPermissions(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path, UnixFileMode.None);
        }
    }

    /// <summary>Restores owner write access so the fact's cleanup can delete the armed file.</summary>
    private static void RestoreOwnerUnixPermissions(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>
    /// The shared NPC stack compiles the section between Characters and Lore Catalogue and carries the fixture
    /// text verbatim inside the rendered Dialogue Examples block; moving the authored section order moves the
    /// rendered block with it (AI-012 TR-7).
    /// </summary>
    [Fact]
    public async Task SharedStack_RendersDialogueExamplesBetweenCharactersAndLoreCatalogue()
    {
        PromptStack stack = LoadSharedStack();
        PromptOwnerCharacter owner = new("test");
        FixturePlayerCharacter player = new();
        try
        {
            SceneContext scene = new([owner, player], _fixture);
            string rendered = await CompileAndRenderAsync(stack, owner, scene);

            string dialogue = ExtractSection(rendered, "Dialogue Examples");
            Assert.Equal(ExpectedSection(FixtureDialoguePath, "char:test"), dialogue);
            AssertBlockOrder(rendered, "<Characters>", "<Dialogue Examples>", "<Lore Catalogue>");

            // The authored stack order stays movable: relocating the section to the end relocates the rendered
            // block with it, after the scenario section.
            PromptSection dialogueSection = stack.Sections.First(section => section.Name == "Dialogue Examples");
            PromptStack relocated = new()
            {
                Sections = [.. stack.Sections.Where(section => section != dialogueSection), dialogueSection],
            };
            string relocatedRendered = await CompileAndRenderAsync(relocated, owner, scene);

            AssertBlockOrder(relocatedRendered, "<Lore Catalogue>", "<Scenario>", "<Dialogue Examples>");
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>
    /// Dialogue examples are data, not templates: a file containing Fluid output tokens renders its text
    /// literally except for the sanctioned <c>{{ character.FullId }}</c> substitution through
    /// the real session-start compile, while the shared instruction's own <c>mind.md</c> variables keep
    /// substituting in the same rendered instruction, so opacity is scoped to the dialogue section (AI-012
    /// UR-2, TR-3).
    /// </summary>
    [Fact]
    public async Task RenderedInstruction_KeepsDialogueTemplateTokensLiteralWhileMindVariablesSubstitute()
    {
        PromptStack stack = LoadSharedStack();
        PromptOwnerCharacter owner = new("opacity");
        FixturePlayerCharacter player = new();
        try
        {
            SceneContext scene = new([owner, player], _fixture);
            string rendered = await CompileAndRenderAsync(stack, owner, scene);

            Assert.Equal(ExpectedSection(OpacityDialoguePath, "char:opacity"), ExtractSection(rendered, "Dialogue Examples"));
            Assert.DoesNotContain("{{ character.FullId }}", ExtractSection(rendered, "Dialogue Examples"), StringComparison.Ordinal);
            Assert.Contains("{{ character.Id }}", ExtractSection(rendered, "Dialogue Examples"), StringComparison.Ordinal);

            // The shared instruction is still a template: its own variable substitutes in the same render.
            Assert.Contains($"You are {((IIdentifiable)owner).FullId},", rendered, StringComparison.Ordinal);
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>
    /// Malformed Fluid tokens in a dialogue file — unclosed tags, dangling output openers, a stray
    /// <c>{% endraw %}</c> — neither crash the single session-start compile nor change the rendered text: the
    /// file's content appears unchanged in the final system instruction (AI-012 UR-2, TR-3; AI-003 TR-1).
    /// </summary>
    [Fact]
    public async Task RenderedInstruction_CompilesMalformedDialogueTokensAndRendersThemLiterally()
    {
        PromptStack stack = LoadSharedStack();
        PromptOwnerCharacter owner = new("malformed");
        FixturePlayerCharacter player = new();
        try
        {
            SceneContext scene = new([owner, player], _fixture);
            string rendered = await CompileAndRenderAsync(stack, owner, scene);

            Assert.Equal(ExpectedSection(MalformedDialoguePath, "char:malformed"), ExtractSection(rendered, "Dialogue Examples"));
            Assert.Contains("{% if the tray tipped and never closed", rendered, StringComparison.Ordinal);
            Assert.Contains("{{ a hand-seal with no wax", rendered, StringComparison.Ordinal);
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>
    /// A character without dialogue examples still gets the unconditional empty wrapper in the final rendered
    /// system instruction — opacity changes nothing about the writer's empty-block contract (AI-012 TR-5).
    /// </summary>
    [Fact]
    public async Task RenderedInstruction_WithoutDialogueFile_EmitsUnconditionalEmptyWrapper()
    {
        PromptStack stack = LoadSharedStack();
        PromptOwnerCharacter owner = new("observer_without_perspective");
        FixturePlayerCharacter player = new();
        try
        {
            SceneContext scene = new([owner, player], _fixture);
            string rendered = await CompileAndRenderAsync(stack, owner, scene);

            Assert.Contains("<Dialogue Examples>\n\n</Dialogue Examples>", rendered, StringComparison.Ordinal);
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>
    /// The section's dedicated guidance file, rather than the shared instruction, carries
    /// every mandatory concept — imitating vocabulary, rhythm, and mannerisms; the exchanges never happened; not
    /// lore, knowledge, or memory; never recited nor treated as fact — as character-neutral guidance without
    /// pinning exact prose (AI-012 TR-8, UR-3).
    /// </summary>
    [Fact]
    public void SectionGuidance_CarriesEveryMandatoryDialogueExamplesConcept()
    {
        string mind = ReadResourceText(SharedMindPromptPath);

        Assert.DoesNotContain("# Dialogue Examples", mind, StringComparison.Ordinal);
        string passage = ReadResourceText("res://prompts/dialogue.md");
        Assert.Contains("vocabulary", passage, StringComparison.Ordinal);
        Assert.Contains("rhythm", passage, StringComparison.Ordinal);
        Assert.Contains("mannerism", passage, StringComparison.Ordinal);
        Assert.Matches(NeverHappenedPhrase(), passage);
        string[] notCategories = ["lore", "knowledge", "memory"];
        foreach (string notACategory in notCategories)
        {
            Assert.Contains(notACategory, passage, StringComparison.Ordinal);
        }

        Assert.Contains("recite", passage, StringComparison.Ordinal);
        Assert.Contains("fact", passage, StringComparison.Ordinal);

        // The guidance stays character-neutral and template-clean.
        Assert.DoesNotContain("Vadim", passage, StringComparison.Ordinal);
        Assert.DoesNotContain("Ally", passage, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", passage, StringComparison.Ordinal);
    }

    /// <summary>Guidance is resource-owned, optional, and absent when the section is omitted.</summary>
    [Fact]
    public async Task Guidance_IsOptionalAndTravelsOnlyWithTheSection()
    {
        DialogueExamplesPromptSection section = new()
        {
            Name = "Dialogue Examples"
        };
        Assert.Equal("res://prompts/dialogue.md", section.GuidancePath);
        string examplesOnly = ExpectedExamples(FixtureDialoguePath, "char:test");
        section.GuidancePath = " ";
        Assert.Equal(examplesOnly, await section.GetContentAsync(CreateBuildContext("test")));

        section.GuidancePath = "res://prompts/ai012_missing_guidance.md";
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => section.GetContentAsync(CreateBuildContext("test")));
        Assert.Contains(section.GuidancePath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DialogueExamplesPromptSection), exception.Message, StringComparison.Ordinal);

        PromptStack shared = LoadSharedStack();
        PromptStack withoutDialogue = new()
        {
            Sections = [.. shared.Sections.Where(item => item is not DialogueExamplesPromptSection)],
        };
        PromptOwnerCharacter owner = new("test");
        FixturePlayerCharacter player = new();
        try
        {
            string rendered = await CompileAndRenderAsync(withoutDialogue, owner, new SceneContext([owner, player], _fixture));
            Assert.DoesNotContain(ReadResourceText("res://prompts/dialogue.md").TrimEnd(), rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("<Dialogue Examples>", rendered, StringComparison.Ordinal);
        }
        finally
        {
            player.Free();
        }
    }

    /// <summary>Guidance preserves syntax and whitespace, including the example-only owner token.</summary>
    [Fact]
    public async Task Guidance_TemplateSyntaxIsLiteralIncludingTheOwnerToken()
    {
        const string guidancePath = "user://ai012_literal_guidance.md";
        const string guidance = "{{ character.FullId }} {{ character.Id }} {% broken {% endraw %}\r\n \t";
        string globalPath = ProjectSettings.GlobalizePath(guidancePath);
        await File.WriteAllTextAsync(globalPath, guidance);
        PromptOwnerCharacter owner = new("test");
        FixturePlayerCharacter player = new();
        try
        {
            PromptStack stack = new()
            {
                Sections = [new DialogueExamplesPromptSection { Name = "Dialogue Examples", GuidancePath = guidancePath }],
            };
            string rendered = await CompileAndRenderAsync(stack, owner, new SceneContext([owner, player], _fixture));
            Assert.Equal(guidance + "\n\n" + ExpectedExamples(FixtureDialoguePath, "char:test"), ExtractSection(rendered, "Dialogue Examples"));
        }
        finally
        {
            player.Free();
            File.Delete(globalPath);
        }
    }

    /// <summary>
    /// Authored dialogue examples never surface through lore: no essential or scene-character query result, no
    /// catalogue row, and no read_lore entry contains the examples' content, and the file registers as no lore
    /// entry at all (AI-012 TR-4, UR-5).
    /// </summary>
    [Fact]
    public async Task DialogueExamples_NeverSurfaceThroughLoreQueriesCatalogueOrReadLore()
    {
        MarkdownLoreQueryService service = new();
        LoreQuery characterQuery = new(
            "char:test",
            [LoreSubjectRequest.Character("char:test"), LoreSubjectRequest.Character("char:ally")]);
        IReadOnlyList<LoreEntry> essential = await service.QueryAsync(_fixture, LoreQuery.Essential("char:test"));
        IReadOnlyList<LoreEntry> characters = await service.QueryAsync(_fixture, characterQuery);
        IReadOnlyList<LoreEntry> catalogue = await service.QueryCatalogueAsync(
            _fixture,
            new LoreCatalogueQuery("char:test"));
        IReadOnlyList<LoreEntryLookup> lookups = await service.QueryEntriesAsync(
            _fixture,
            new LoreEntryIDQuery("char:test", ["test.stable", "test.ally", "test.dialogue"]));

        Assert.DoesNotContain(FixtureDialogueMarker, EntriesText(essential), StringComparison.Ordinal);
        Assert.DoesNotContain(FixtureDialogueMarker, EntriesText(characters), StringComparison.Ordinal);
        Assert.DoesNotContain(FixtureDialogueMarker, EntriesText(catalogue), StringComparison.Ordinal);
        Assert.DoesNotContain("dialogue.md", EntriesText(catalogue), StringComparison.Ordinal);

        LoreEntryLookup dialogueLookup = Assert.Single(
            lookups,
            lookup => lookup.RequestedID == "test.dialogue");
        Assert.Null(dialogueLookup.Entry);
        foreach (LoreEntryLookup lookup in lookups)
        {
            if (lookup.Entry is not null)
            {
                Assert.DoesNotContain(FixtureDialogueMarker, lookup.Entry.Body, StringComparison.Ordinal);
            }
        }
    }

    private static PromptSectionBuildContext CreateBuildContext(string observerID)
    {
        PromptOwnerCharacter owner = new(observerID);
        return new PromptSectionBuildContext(
            new ServiceCollection().BuildServiceProvider(),
            new SceneContext([owner], _fixture),
            owner);
    }

    private static PromptStack LoadSharedStack()
    {
        PromptStack stack = ResourceLoader.Load<PromptStack>(SharedStackPath);
        Assert.NotNull(stack);
        return stack;
    }

    private static async Task<string> CompileAndRenderAsync(PromptStack stack, ICharacter owner, SceneContext scene)
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .AddSingleton<ILoreCataloguePromptFormatter, MarkdownLoreCataloguePromptFormatter>()
            .AddSingleton<IPromptWriter>(new PseudoXmlPromptWriter())
            .AddSingleton<ITemplateCompiler>(new FluidTemplateCompiler())
            .BuildServiceProvider();
        var buildContext = new PromptSectionBuildContext(services, scene, owner);

        ITemplate template = await stack.CompileAsync(buildContext);

        return await template.RenderAsync(AgenticMind.CreateRenderContext(owner, scene));
    }

    private static string ExtractSection(string source, string tagName)
    {
        string opening = $"<{tagName}>\n";
        int start = source.IndexOf(opening, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a '<{tagName}>' block in the rendered prompt.");
        int end = source.IndexOf($"</{tagName}>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected a closed '<{tagName}>' block in the rendered prompt.");
        return source[(start + opening.Length)..end];
    }

    private static void AssertBlockOrder(string source, string first, string second, string third)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        int thirdIndex = source.IndexOf(third, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Expected '{first}' in the rendered prompt.");
        Assert.True(secondIndex > firstIndex, $"Expected '{second}' after '{first}' in the rendered prompt.");
        Assert.True(thirdIndex > secondIndex, $"Expected '{third}' after '{second}' in the rendered prompt.");
    }

    private static string ExpectedExamples(string path, string fullID)
        => ReadResourceText(path).Replace("{{ character.FullId }}", fullID, StringComparison.Ordinal);

    private static string ExpectedSection(string path, string fullID)
        => ReadResourceText("res://prompts/dialogue.md") + "\n\n" + ExpectedExamples(path, fullID);

    private static string EntriesText(IEnumerable<LoreEntry> entries)
        => string.Join(
            "\n",
            entries.Select(static entry => $"{entry.ID}|{entry.Title}|{entry.Description}|{entry.Body}"));

    private static string ReadResourceText(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        Assert.NotNull(file);
        return file!.GetAsText();
    }

    [GeneratedRegex(@"never\s+(took\s+place|happened)")]
    private static partial Regex NeverHappenedPhrase();
}
