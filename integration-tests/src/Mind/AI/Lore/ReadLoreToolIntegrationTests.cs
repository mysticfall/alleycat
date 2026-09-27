using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Content;
using AlleyCat.Core.Threading;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Lore;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using MindBase = AlleyCat.Mind.Mind;

namespace AlleyCat.IntegrationTests.Mind.AI.Lore;

/// <summary>
/// Godot-runtime coverage for the composition-bound read-only lore tool: trusted observer/content binding with
/// entry-ID-only model inputs, single and batch lookup with deduplication and first-request order, explicit
/// unavailable results, whole-batch input validation before effects, sanitised source failures, cancellation
/// without partial results, and no Mind observations (AI-002 TR-23/24; AI-004 requirements 47 to 49).
/// </summary>
[Headless]
public sealed class ReadLoreToolIntegrationTests
{
    private static readonly ContentContext _fixture = new("lore-query-fixture", "res://tests/lore-query-fixture");

    /// <summary>
    /// A batch lookup returns exactly one result per distinct ID in first-request order: found entries carry their
    /// ID-associated formatted bodies — including automatically injected essential entries — and unknown IDs are
    /// explicitly unavailable (AI-004 requirements 47 and 48).
    /// </summary>
    [Fact]
    public async Task Read_ReturnsFirstRequestOrderedIDAssociatedBodiesAndUnavailableResults()
    {
        ReadLoreFixture fixture = new(new MarkdownLoreQueryService(), new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        string message = await fixture.InvokeReadAsync(
            ["test.interrogation_room", "test.missing", "test.interrogation_room", "test.stable"],
            CancellationToken.None);

        int interrogation = message.IndexOf("Entry 'test.interrogation_room':", StringComparison.Ordinal);
        int missing = message.IndexOf("Entry 'test.missing': unavailable.", StringComparison.Ordinal);
        int stable = message.IndexOf("Entry 'test.stable':", StringComparison.Ordinal);
        Assert.True(
            interrogation >= 0 && missing > interrogation && stable > missing,
            $"Results must keep first-request order with deduplication: {message}");
        Assert.Equal(interrogation, message.LastIndexOf("Entry 'test.interrogation_room':", StringComparison.Ordinal));

        Assert.Contains("# loc:interrogation_room\n", message, StringComparison.Ordinal);
        Assert.Contains("loc:interrogation_room sits below the charter office.", message, StringComparison.Ordinal);
        Assert.Contains("I have been inside once, and I do not intend to return.", message, StringComparison.Ordinal);

        // 'test.stable' is essential world lore that automatic injection already supplies; it stays retrievable.
        Assert.Contains("# Stable Entry\n\nStable ID entry.", message, StringComparison.Ordinal);

        // No canonical bodies and no scope-foreign knowledge join the result.
        Assert.DoesNotContain("Canonical authoring page", message, StringComparison.Ordinal);

        // read_lore is read-only: it emits no Mind observations (AI-002 TR-24).
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// The tool binds its trusted observer and content scope from the session context; the model supplies entry IDs
    /// only (AI-002 TR-23).
    /// </summary>
    [Fact]
    public async Task Read_BindsTrustedObserverAndContentFromTheSessionContext()
    {
        CapturingLoreQueryService queryService = new();
        ReadLoreFixture fixture = new(queryService, new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        _ = await fixture.InvokeReadAsync(["scope.probe"], CancellationToken.None);

        _ = Assert.Single(queryService.EntryIDQueryCalls);
        (ContentContext content, LoreEntryIDQuery query) = queryService.EntryIDQueryCalls[0];
        Assert.Equal(fixture.Owner.FullId, query.ObserverID);
        Assert.Equal(["scope.probe"], query.EntryIDs);
        Assert.Same(fixture.SceneContext.Content, content);
    }

    /// <summary>
    /// The complete ID list is validated before any lookup: blank IDs, empty lists, and null entries are rejected
    /// as invalid model input with no partial lore result and no service call (AI-004 requirement 47).
    /// </summary>
    [Fact]
    public async Task Read_WithInvalidIDList_RejectsWholeBatchBeforeAnyLookup()
    {
        CapturingLoreQueryService queryService = new();
        ReadLoreFixture fixture = new(queryService, new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.InvokeReadAsync(["test.ally", "   "], CancellationToken.None));
        _ = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.InvokeReadAsync([], CancellationToken.None));

        Assert.Empty(queryService.EntryIDQueryCalls);
    }

    /// <summary>
    /// Non-list and non-string argument values fail schema binding before the tool runs any effect (AI-004
    /// requirement 47).
    /// </summary>
    [Fact]
    public async Task Read_WithNonListOrNonStringArgumentValues_RejectsBeforeAnyLookup()
    {
        CapturingLoreQueryService queryService = new();
        ReadLoreFixture fixture = new(queryService, new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.InvokeReadRawAsync("test.ally", CancellationToken.None));
        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.InvokeReadRawAsync(42, CancellationToken.None));
        _ = await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.InvokeReadRawAsync(new object?[] { "test.ally", 7 }, CancellationToken.None));

        Assert.Empty(queryService.EntryIDQueryCalls);
    }

    /// <summary>
    /// Source validation failures stay errors, never missing knowledge: a scope with duplicate entry IDs fails the
    /// lookup with a fixed, path-free result, while the complete internal detail — including source paths — is
    /// logged internally (AI-004 requirements 43 and 49).
    /// </summary>
    [Fact]
    public async Task Read_WithDuplicateScopedEntryIDs_ReturnsSanitisedFailureAndLogsDetailInternally()
    {
        CapturingLogger logger = new();
        ReadLoreTool tool = new(new MarkdownLoreQueryService(), new MarkdownLorePromptFormatter(), logger);
        ReadLoreFixture fixture = new(tool);
        await fixture.ReadyAsync();
        fixture.UseConflictedObserver();

        string message = await fixture.InvokeReadAsync(["conflicted.entry"], CancellationToken.None);

        Assert.Equal(
            "The lore lookup failed and nothing was retrieved. This is an internal error, not an unavailable "
            + "result; no entry was marked unavailable.",
            message);
        Assert.DoesNotContain("res://", message, StringComparison.Ordinal);
        Assert.DoesNotContain(".md", message, StringComparison.Ordinal);
        Assert.DoesNotContain("conflicted.entry", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Duplicate lore entry ID", message, StringComparison.Ordinal);

        // The internal log carries the full diagnostic, including the conflicting source paths.
        (LogLevel level, Exception? exception) = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, level);
        InvalidOperationException? failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("conflicted.entry", failure.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate.md", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancellation before completion cancels the whole operation without a partial or unavailable-marked result
    /// (AI-004 requirement 49).
    /// </summary>
    [Fact]
    public async Task Read_WithCancelledToken_ThrowsWithoutAnyResult()
    {
        ReadLoreFixture fixture = new(new MarkdownLoreQueryService(), new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.InvokeReadAsync(["test.ally"], cancellation.Token));

        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation observed during the final lore read — while the tool's entry-ID query is still in flight —
    /// propagates out of the read_lore function as cancellation without a partial batch, an unavailable-marked
    /// result, or a sanitised internal-error result (AI-004 requirement 49).
    /// </summary>
    [Fact]
    public async Task Read_WithCancellationDuringFinalRead_PropagatesCancellationWithoutAnyResult()
    {
        const string finalReadPath =
            "res://tests/lore-query-fixture/lore/perspectives/char/test/locations/interrogation_room.md";
        List<string> readPaths = [];
        using CancellationTokenSource cancellation = new();
        MarkdownLoreQueryService queryService = new()
        {
            FileReadObserver = path =>
            {
                readPaths.Add(path);
                if (path == finalReadPath)
                {
                    cancellation.Cancel();
                }
            },
        };
        ReadLoreFixture fixture = new(queryService, new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        Exception? thrown = await Record.ExceptionAsync(
            () => fixture.InvokeReadAsync(["test.ally"], cancellation.Token));

        // The cancellation genuinely landed inside the final location read of the tool's own query.
        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotEmpty(readPaths);
        Assert.Equal(finalReadPath, readPaths[^1]);
        Assert.True(
            thrown is OperationCanceledException,
            $"Cancellation during the final read must cancel the tool call, but it completed with "
            + $"{(thrown is null ? "a successful result" : thrown.GetType().Name)} after {readPaths.Count} reads.");
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Cancellation landing inside the tool's own formatting stage — after the lookup batch completed and before
    /// the formatted result is returned — cancels the whole tool call instead of returning success (AI-004
    /// requirement 49). The dispatcher accepts a successful invocation whose token is cancelled, so the tool
    /// enforces its own completion contract through the real bound function.
    /// </summary>
    [Fact]
    public async Task Read_WithCancellationInsideFormatter_CancelsInsteadOfReturningSuccess()
    {
        using CancellationTokenSource cancellation = new();
        CancellingFormatter formatter = new(cancellation);
        ReadLoreTool tool = new(new MarkdownLoreQueryService(), formatter);
        ReadLoreFixture fixture = new(tool);
        await fixture.ReadyAsync();

        Exception? thrown = await Record.ExceptionAsync(
            () => fixture.InvokeReadAsync(["test.ally"], cancellation.Token));

        // The cancellation genuinely landed inside the formatter, after the lookup batch completed.
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, formatter.FormatCalls);
        Assert.True(
            thrown is OperationCanceledException,
            $"Cancellation inside the formatter must cancel the tool call, but it completed with "
            + $"{(thrown is null ? "a successful result" : thrown.GetType().Name)} after the lookup succeeded.");
        Assert.Empty(fixture.Mind.GetTimelineForTest());
    }

    /// <summary>
    /// Formatter double that cancels the operation token from inside formatting, mirroring the reviewer's
    /// source-linked probe: it returns a valid formatted body so the token is the only failure trigger.
    /// </summary>
    private sealed class CancellingFormatter(CancellationTokenSource cancellation) : ILorePromptFormatter
    {
        public int FormatCalls
        {
            get; private set;
        }

        public string Format(IReadOnlyList<LoreEntry> entries)
        {
            FormatCalls++;
            cancellation.Cancel();
            return "Formatted lore body.";
        }
    }

    /// <summary>
    /// The delivered tool metadata the model receives agrees with the list-only input contract (AI-004
    /// requirement 47): the authored and delivered descriptions and the <c>entry_ids</c> schema describe a
    /// non-empty string list with a one-element list for a single entry, with no scalar-permissive wording.
    /// Scalar rejection is covered by <see cref="Read_WithNonListOrNonStringArgumentValues_RejectsBeforeAnyLookup" />.
    /// </summary>
    [Fact]
    public async Task Read_DeliveredDescriptionAgreesWithListOnlySchema()
    {
        ReadLoreFixture fixture = new(new MarkdownLoreQueryService(), new MarkdownLorePromptFormatter());
        await fixture.ReadyAsync();

        AIFunction function = fixture.ReadFunction;
        Assert.Equal("read_lore", function.Name);
        Assert.Contains("non-empty list of IDs", function.Description, StringComparison.Ordinal);
        Assert.Contains("one-element list", function.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("single ID or a list", function.Description, StringComparison.Ordinal);

        // The authored description is the source the delivered function metadata is built from.
        Assert.Equal(fixture.Tool.ToolDescription.Trim(), function.Description);

        JsonElement entryIDs = function.JsonSchema.GetProperty("properties").GetProperty("entry_ids");
        Assert.Equal("array", entryIDs.GetProperty("type").GetString());
        Assert.Equal("string", entryIDs.GetProperty("items").GetProperty("type").GetString());
        string? parameterDescription = entryIDs.GetProperty("description").GetString();
        Assert.NotNull(parameterDescription);
        Assert.Contains("non-empty list", parameterDescription!, StringComparison.Ordinal);
        Assert.Contains("one-element list", parameterDescription!, StringComparison.Ordinal);
        Assert.DoesNotContain("one ID or several", parameterDescription!, StringComparison.Ordinal);
        Assert.Contains(
            "entry_ids",
            function.JsonSchema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
    }

    /// <summary>
    /// Assembles the in-tree Mind, scene membership, session tool function, and controllable dependencies shared by
    /// the read_lore tool tests.
    /// </summary>
    private sealed class ReadLoreFixture(ReadLoreTool tool)
    {
        private AIFunction? _readFunction;

        public ReadLoreFixture(ILoreQueryService queryService, ILorePromptFormatter formatter)
            : this(new ReadLoreTool(queryService, formatter))
        {
        }

        public ReadLoreTool Tool => tool;

        /// <summary>The delivered AI function whose metadata the model actually receives.</summary>
        public AIFunction ReadFunction => _readFunction ?? throw new InvalidOperationException("The fixture is not ready.");

        public ICharacter Owner { get; private set; } = new PromptOwnerCharacter("test");

        public SceneContext SceneContext { get; private set; } = null!;

        public TestMind Mind { get; } = new();

        /// <summary>
        /// Rebinds the fixture to the conflicted observer whose perspective carries duplicate scoped entry IDs
        /// (AI-004 requirement 43).
        /// </summary>
        public void UseConflictedObserver()
        {
            Owner = new PromptOwnerCharacter("conflicted");
            SceneContext = new SceneContext([Owner], _fixture);
            Mind.SetOwner(Owner);
            CreateFunction();
        }

        public async Task ReadyAsync()
        {
            SceneContext = new SceneContext([Owner], _fixture);
            Mind.SetOwner(Owner);
            SceneTree sceneTree = TestUtils.GetSceneTree();
            (sceneTree.CurrentScene ?? sceneTree.Root).AddChild(Mind);
            await TestUtils.WaitForFramesAsync(sceneTree, 2);
            CreateFunction();
        }

        public Task<string> InvokeReadAsync(IReadOnlyList<string> entryIDs, CancellationToken cancellationToken)
            => InvokeReadRawAsync(entryIDs, cancellationToken);

        public async Task<string> InvokeReadRawAsync(object? entryIDsValue, CancellationToken cancellationToken)
        {
            AIFunction function = _readFunction ?? throw new InvalidOperationException("The fixture is not ready.");
            object? result = await function.InvokeAsync(
                new AIFunctionArguments { ["entry_ids"] = entryIDsValue },
                cancellationToken);
            return Assert.IsType<string>(result);
        }

        private void CreateFunction()
        {
            ScenarioContext context = new(Owner, SceneContext);
            IMainThreadDispatcher dispatcher = Game.Instance.GetRequiredService<IMainThreadDispatcher>();
            AgentToolSession sessionServices = new(context, Mind, Clock: null);
            _readFunction = tool.CreateFunction(context, Mind, dispatcher, sessionServices);
        }
    }

    private sealed partial class TestMind : MindBase
    {
        private ICharacter _owner = null!;

        public void SetOwner(ICharacter owner) => _owner = owner;

        public IReadOnlyList<AlleyCat.Mind.Observation.Observation> GetTimelineForTest()
            => GetObservationTimelineSnapshot();

        protected override ICharacter ResolveOwningCharacter() => _owner;
    }

    private sealed class CapturingLoreQueryService : ILoreQueryService
    {
        public List<(ContentContext Content, LoreEntryIDQuery Query)> EntryIDQueryCalls
        {
            get;
        } = [];

        public Task<IReadOnlyList<LoreEntry>> QueryAsync(
            ContentContext content,
            LoreQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LoreEntry>>([]);

        public Task<IReadOnlyList<LoreEntry>> QueryCatalogueAsync(
            ContentContext content,
            LoreCatalogueQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LoreEntry>>([]);

        public Task<IReadOnlyList<LoreEntryLookup>> QueryEntriesAsync(
            ContentContext content,
            LoreEntryIDQuery query,
            CancellationToken cancellationToken = default)
        {
            EntryIDQueryCalls.Add((content, query));
            return Task.FromResult<IReadOnlyList<LoreEntryLookup>>([]);
        }
    }

    private sealed class CapturingLogger : ILogger<ReadLoreTool>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries
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
            => Entries.Add((logLevel, exception));
    }
}
