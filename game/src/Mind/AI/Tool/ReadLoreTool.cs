using System.ComponentModel;
using AlleyCat.Mind.AI.Lore;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Tool;

/// <summary>
/// Read-only tool that retrieves lore entries by their exact entry IDs from the observer/content scope bound at
/// AgenticMind composition (AI-002 TR-23). Only entry IDs are model inputs: the observer perspective and content
/// root are trusted composition state the model cannot supply. Retrieval emits no Mind observations and its settled
/// exchange follows default retention, so results reach the next provider request before a subsequent action
/// decision (AI-002 TR-24).
/// </summary>
[Tool]
[GlobalClass]
public partial class ReadLoreTool : AgentTool
{
    /// <summary>Model-facing name of the production read-only lore tool (AI-002 TR-13).</summary>
    internal const string ProductionToolName = "read_lore";

    private const string MissingDependenciesMessage =
        "The read_lore tool requires its composition-bound lore dependencies.";

    private readonly ILoreQueryService? _queryService;
    private readonly ILorePromptFormatter? _formatter;
    private readonly ILogger<ReadLoreTool>? _logger;

    /// <summary>
    /// Creates a read-only lore tool with the default model-facing metadata for editor authoring. Composition binds
    /// the lore dependencies through the internal constructor; an authored instance without them fails clearly at
    /// use.
    /// </summary>
    public ReadLoreTool()
    {
        ToolName = ProductionToolName;
        ToolDescription = "Read the full bodies of lore entries by their exact entry IDs. Entry IDs come from the "
            + "Lore Catalogue in your instructions and are lore handles, not subject full IDs. Pass a non-empty "
            + "list of IDs — for a single entry, pass a one-element list; every requested ID returns either its "
            + "full body or an explicit unavailable result, and unavailable means no available lore of yours "
            + "carries that exact ID — never a reason to invent. Returned bodies are your own beliefs, not "
            + "omniscient facts. Results are retained, so they reach your next request: retrieve first, then "
            + "reason and act in a later response — a tool call later in the same response cannot use a result "
            + "it has not received. Retrieval is encouraged when relevant, never required before every action.";
        // Default retention keeps the settled exchange model-visible (AI-002 TR-20/24): the next provider request
        // replays it so its results inform the subsequent action decision.
    }

    /// <summary>
    /// Creates a read-only lore tool with its typed lore dependencies bound at the AgenticMind composition
    /// boundary (AI-002 TR-23). The optional logger receives complete internal failure detail that must never
    /// reach the model.
    /// </summary>
    internal ReadLoreTool(
        ILoreQueryService queryService,
        ILorePromptFormatter formatter,
        ILogger<ReadLoreTool>? logger = null) : this()
    {
        ArgumentNullException.ThrowIfNull(queryService);
        ArgumentNullException.ThrowIfNull(formatter);
        _queryService = queryService;
        _formatter = formatter;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override Delegate CreateDelegate() => Read;

    private async ValueTask<AgentToolResult> Read(
        ScenarioContext context,
        [Description(
            "Exact lore entry IDs from your Lore Catalogue. Pass a non-empty list; use a one-element list for a "
            + "single entry; repeated IDs are read once.")]
        IReadOnlyList<string> entry_ids,
        CancellationToken cancellationToken = default)
    {
        ILoreQueryService queryService = _queryService
            ?? throw new InvalidOperationException(MissingDependenciesMessage);
        ILorePromptFormatter formatter = _formatter
            ?? throw new InvalidOperationException(MissingDependenciesMessage);

        // Whole-batch input validation happens in the query constructor before any read: blank or empty ID lists
        // are rejected with a model-safe message and no partial lore (AI-004 requirement 47). Argument-shape
        // failures for non-list or non-string values are rejected by binding before this delegate runs.
        LoreEntryIDQuery query = new(context.Character.FullId, entry_ids);

        IReadOnlyList<LoreEntryLookup> lookups;
        try
        {
            lookups = await queryService.QueryEntriesAsync(
                context.SceneContext.Content,
                query,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation cancels the whole operation without a partial batch (AI-004 requirement 49); the runner
            // owns the canonical cancelled result.
            throw;
        }
        catch (Exception exception)
        {
            // Source validation failures stay errors, never missing knowledge (AI-004 requirement 49): log the
            // complete internal detail — including any source paths — and return a fixed, path-free failure so no
            // source path or arbitrary exception text reaches the model.
            _logger?.LogError(
                exception,
                "The read_lore tool failed while looking up {EntryCount} lore entry ID(s) for observer "
                + "'{ObserverID}'; the model received a sanitised failure result.",
                query.EntryIDs.Count,
                context.Character.FullId);

            return new AgentToolResult(
                "The lore lookup failed and nothing was retrieved. This is an internal error, not an unavailable "
                + "result; no entry was marked unavailable.");
        }

        // Cancellation after the read window but before completion still cancels the whole operation: the
        // dispatcher accepts a successful invocation whose token is cancelled, so the tool enforces its own
        // completion contract (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        string message = ComposeResultMessage(lookups, formatter);
        // Cancellation can also land inside formatting itself; observe it before any successful return.
        cancellationToken.ThrowIfCancellationRequested();
        return new AgentToolResult(message);
    }

    /// <summary>
    /// Associates every requested ID — in first-request order — with either its formatted lore body or an explicit
    /// unavailable status (AI-004 requirement 48). Body formatting reuses the shared lore formatter unchanged; the
    /// envelope adds only the ID association.
    /// </summary>
    private static string ComposeResultMessage(IReadOnlyList<LoreEntryLookup> lookups, ILorePromptFormatter formatter)
    {
        List<string> blocks = new(lookups.Count);
        foreach (LoreEntryLookup lookup in lookups)
        {
            blocks.Add(lookup.Found
                ? $"Entry '{lookup.RequestedID}':\n{formatter.Format([lookup.Entry!])}"
                : $"Entry '{lookup.RequestedID}': unavailable.");
        }

        return string.Join("\n\n", blocks);
    }
}
