using AlleyCat.Core.Content;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for Markdown-backed perspective lore prompt injection.
/// </summary>
[Headless]
public sealed class EssentialLoreIntegrationTests
{
    /// <summary>
    /// Character lore requests the owner first and remaining FullIds in ordinal order.
    /// </summary>
    [Fact]
    public async Task CharacterLorePromptSection_QueriesOwnerFirstThenOrdinalSceneCharacters()
    {
        CapturingLoreQueryService queryService = new(
        [
            new LoreEntry("known", "Known", "Known lore.", Kind: LoreSubjectKind.Character),
        ]);
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService>(queryService)
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .BuildServiceProvider();
        PromptOwnerCharacter owner = new("owner");
        PromptOwnerCharacter zulu = new("zulu");
        PromptOwnerCharacter alpha = new("alpha");
        SceneContext scene = new([zulu, owner, alpha], ContentContext.Default);
        CharacterLorePromptSection section = new();

        string content = await section.GetContentAsync(new PromptSectionBuildContext(services, scene, owner));

        Assert.Equal("char:owner", queryService.Query!.ObserverID);
        Assert.Equal(
            ["char:owner", "char:alpha", "char:zulu"],
            queryService.Query.Subjects.Select(subject => subject.SubjectID));
        Assert.Contains("Known lore.", content, StringComparison.Ordinal);
    }

    /// <summary>
    /// Missing perspective lore remains absent and does not acquire fallback content in the section.
    /// </summary>
    [Fact]
    public async Task CharacterLorePromptSection_WhenLoreIsMissing_ReturnsEmptyContent()
    {
        CapturingLoreQueryService queryService = new([]);
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService>(queryService)
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .BuildServiceProvider();
        PromptOwnerCharacter owner = new("owner");
        SceneContext scene = new([owner], ContentContext.Default);

        string content = await new CharacterLorePromptSection().GetContentAsync(
            new PromptSectionBuildContext(services, scene, owner));

        Assert.Equal(string.Empty, content);
    }

    /// <summary>
    /// Invalid runtime identities fail before lore query construction.
    /// </summary>
    [Fact]
    public void CharacterLorePromptSection_WhenRuntimeIdentityIsInvalid_FailsClearly()
    {
        PromptOwnerCharacter owner = new("owner");
        PromptOwnerCharacter invalid = new("Owner");

        ArgumentException exception = Assert.Throws<ArgumentException>(() => new SceneContext([owner, invalid], ContentContext.Default));

        Assert.Contains("invalid identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Owner", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Character lore requires the exact owning runtime character to belong to the scene snapshot.
    /// </summary>
    [Fact]
    public async Task CharacterLorePromptSection_WhenOwnerIsAbsent_FailsClearly()
    {
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        PromptOwnerCharacter owner = new("owner");
        PromptOwnerCharacter sceneCharacter = new("scene_character");
        SceneContext scene = new([sceneCharacter], ContentContext.Default);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CharacterLorePromptSection().GetContentAsync(
                new PromptSectionBuildContext(services, scene, owner)));

        Assert.Contains("present in the scene context", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Vadim's essential world lore is selected from his perspective and sorted by stable ID when priorities tie.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SelectsEssentialWorldLoreForCanonicalObserver()
    {
        MarkdownLoreQueryService service = new();

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(
            ContentContext.Default,
            LoreQuery.Essential("char:vadim"));

        Assert.Equal(
            ["vadim.charter", "vadim.charter_office", "vadim.peoples", "vadim.reclassification"],
            entries.Select(entry => entry.ID));
        Assert.All(entries, entry => Assert.Equal(LoreSubjectKind.World, entry.Kind));
        Assert.Contains("operating logic", entries[0].Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Contextual results retain request grouping and match canonical subject FullIds irrespective of essential.
    /// </summary>
    [Fact]
    public async Task QueryAsync_SelectsContextualBatchInRequestOrder()
    {
        MarkdownLoreQueryService service = new();
        LoreQuery query = new(
            "char:vadim",
            [
                LoreSubjectRequest.Character("char:ally"),
                LoreSubjectRequest.Location("loc:interrogation_room"),
                LoreSubjectRequest.Character("char:vadim"),
            ]);

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(ContentContext.Default, query);

        Assert.Equal(["vadim.ally", "vadim.interrogation_room", "vadim.self"], entries.Select(entry => entry.ID));
        Assert.Equal(
            [LoreSubjectKind.Character, LoreSubjectKind.Location, LoreSubjectKind.Character],
            entries.Select(entry => entry.Kind));
        Assert.Equal(
            ["char:ally", "loc:interrogation_room", "char:vadim"],
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
            LoreQuery.Essential("char:observer_without_perspective"));

        Assert.Empty(entries);
    }

    /// <summary>
    /// Read-time triage keeps authoring-time pages out of runtime queries while nested subdirectory pages join the
    /// collection at any depth (AI-004 requirement 36).
    /// </summary>
    [Fact]
    public async Task QueryAsync_SkipsAuthoringTimePagesAndIncludesNestedSubdirectoryPages()
    {
        MarkdownLoreQueryService service = new();
        ContentContext content = new("lore-query-fixture", "res://tests/lore-query-fixture");

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(content, LoreQuery.Essential("char:test"));

        Assert.Equal(
            ["test.authoring_material", "test.nested_note", "test.stable"],
            entries.Select(entry => entry.ID));
        Assert.All(entries, entry => Assert.NotNull(entry.ID));
        Assert.All(entries, entry => Assert.Equal(LoreSubjectKind.World, entry.Kind));
        Assert.Contains("Kept prose from a nested subdirectory page.", entries[1].Body, StringComparison.Ordinal);
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("First source-path fallback entry.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("Second source-path fallback entry.", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("no frontmatter block at all", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("never closes it", StringComparison.Ordinal));
        Assert.DoesNotContain(
            entries,
            entry => entry.Body.Contains("authoring-time scratch", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parse-time body cleaning removes authoring-time material from entry bodies while fenced code blocks pass
    /// through verbatim (AI-004 requirements 37 to 40).
    /// </summary>
    [Fact]
    public async Task QueryAsync_CleansAuthoringTimeMaterialFromEntryBodies()
    {
        MarkdownLoreQueryService service = new();
        ContentContext content = new("lore-query-fixture", "res://tests/lore-query-fixture");

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(content, LoreQuery.Essential("char:test"));

        LoreEntry material = Assert.Single(entries, entry => entry.ID == "test.authoring_material");
        string body = material.Body;

        // No entry body carries inline link syntax, and only the verbatim fence carries comment syntax.
        Assert.All(entries, entry => Assert.DoesNotContain("](", entry.Body, StringComparison.Ordinal));
        Assert.All(
            entries.Where(entry => entry.ID != "test.authoring_material"),
            entry => Assert.DoesNotContain("<!--", entry.Body, StringComparison.Ordinal));

        // Reduced links keep their label text, and autolinks keep their URL text.
        Assert.Contains("the night market", body, StringComparison.Ordinal);
        Assert.Contains("the old vault", body, StringComparison.Ordinal);
        Assert.Contains("https://example.com/lore-notes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("loc:night_market", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<https://example.com/lore-notes>", body, StringComparison.Ordinal);

        // Non-link bracket forms are left untouched.
        Assert.Contains("[bracket label]", body, StringComparison.Ordinal);
        Assert.Contains("[[wiki link]]", body, StringComparison.Ordinal);

        // Ignored sections and bare comments never reach the entry body.
        Assert.DoesNotContain("Scratch planning", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden doubt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("lore:ignore", body, StringComparison.Ordinal);
        Assert.DoesNotContain("author note", body, StringComparison.Ordinal);

        // The fenced authoring example passes through verbatim, including link syntax and comments.
        Assert.Contains("```markdown", body, StringComparison.Ordinal);
        Assert.Contains(
            "See [the vault draft][vault] or <https://example.com/fence-example> for the draft.",
            body,
            StringComparison.Ordinal);
        Assert.Contains("<!-- fence comment: must survive verbatim -->", body, StringComparison.Ordinal);
        Assert.Contains("[vault]: loc:old_vault", body, StringComparison.Ordinal);

        // Prose outside the verbatim fence carries no comment syntax or dropped reference definitions.
        int fenceStart = body.IndexOf("```markdown", StringComparison.Ordinal);
        Assert.True(fenceStart > 0, "The cleaned body must retain the fenced authoring example.");
        string prose = body[..fenceStart];
        Assert.DoesNotContain("<!--", prose, StringComparison.Ordinal);
        Assert.DoesNotContain("[vault]:", prose, StringComparison.Ordinal);
    }

    /// <summary>
    /// Co-located authoring files never break runtime queries: warned skips surface through the service logger
    /// while silent triage stays silent (AI-004 requirements 36 and 41).
    /// </summary>
    [Fact]
    public async Task QueryAsync_WarnsOnlyForUnterminatedFrontmatterAndEmptyCleanedBodies()
    {
        CapturingLoreLogger logger = new();
        MarkdownLoreQueryService service = new(logger);
        ContentContext content = new("lore-query-fixture", "res://tests/lore-query-fixture");

        IReadOnlyList<LoreEntry> entries = await service.QueryAsync(content, LoreQuery.Essential("char:test"));

        Assert.Equal(3, entries.Count);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("unterminated-frontmatter.md", StringComparison.Ordinal)
                && message.Contains("never closes", StringComparison.Ordinal));
        Assert.Contains(
            logger.Messages,
            message => message.Contains("with-id-empty-after-clean.md", StringComparison.Ordinal)
                && message.Contains("empty after parse-time cleaning", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("no-frontmatter.md", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("without-id-a.md", StringComparison.Ordinal));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("without-id-b.md", StringComparison.Ordinal));
    }

    /// <summary>
    /// EssentialLorePromptSection consumes the associated observer query supplied by the prompt-build seam.
    /// </summary>
    [Fact]
    public async Task EssentialLorePromptSection_FormatsObserverPerspectiveWithMarkdownHeadings()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<ILoreQueryService, MarkdownLoreQueryService>()
            .AddSingleton<ILorePromptFormatter, MarkdownLorePromptFormatter>()
            .BuildServiceProvider();
        SceneContext scene = new([], ContentContext.Default);
        EssentialLorePromptSection section = new()
        {
            Name = "Essential Lore",
        };
        PromptOwnerCharacter character = new("vadim");
        PromptSectionBuildContext buildContext = new(services, scene, character);

        string content = await section.GetContentAsync(buildContext);

        Assert.StartsWith("# The Charter\n", content, StringComparison.Ordinal);
        Assert.Contains("operating logic", content, StringComparison.Ordinal);
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
    /// The default formatter renders each lore title verbatim as a Markdown heading and trims body boundaries.
    /// </summary>
    [Fact]
    public void MarkdownLorePromptFormatter_RendersVerbatimTitleHeadingAndTrimmedBody()
    {
        MarkdownLorePromptFormatter formatter = new();

        string content = formatter.Format(
        [
            new LoreEntry(
                "faction.rank.elite",
                "Faction/Rank <Elite>",
                "\nCanon includes / slashes.\n\n"),
        ]);

        Assert.Equal(
            "# Faction/Rank <Elite>\n\n" +
            "Canon includes / slashes.",
            content);
        Assert.DoesNotContain("<lore_entry>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<title>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<body>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<source>", content, StringComparison.Ordinal);
    }

    private sealed class CapturingLoreQueryService(IReadOnlyList<LoreEntry> entries) : ILoreQueryService
    {
        public LoreQuery? Query
        {
            get; private set;
        }

        public Task<IReadOnlyList<LoreEntry>> QueryAsync(
            ContentContext content,
            LoreQuery query,
            CancellationToken cancellationToken = default)
        {
            Query = query;
            return Task.FromResult(entries);
        }
    }

    private sealed class CapturingLoreLogger : ILogger<MarkdownLoreQueryService>
    {
        public List<string> Messages
        {
            get;
        } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
