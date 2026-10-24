using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using AlleyCat.Core.Content;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Read-only Markdown-backed access to content-scoped lore with path-authoritative identity: each runtime
/// entry's kind, canonical subject binding, and canonical full ID derive from its collection directory and path
/// segments (AI-004 requirements 6 and 43), and the service reads both bases — the canonical wiki without an
/// observer and observer-scoped perspectives (AI-004 requirement 52). LLM-facing composition always supplies the
/// active observer, so it only ever resolves against that observer's perspective.
/// </summary>
/// <param name="logger">
/// Optional logger that receives read-time triage warnings; triage stays silent when no logger is supplied.
/// </param>
public sealed class MarkdownLoreQueryService(ILogger<MarkdownLoreQueryService>? logger = null) : ILoreQueryService
{
    private const string IgnoreOpenMarker = "<!-- lore:ignore -->";
    private const string IgnoreCloseMarker = "<!-- /lore:ignore -->";
    private const string CommentOpenMarker = "<!--";
    private const string CommentCloseMarker = "-->";
    private const string WikiRootSuffix = "lore/wiki/";

    /// <summary>
    /// The scope-wide collection order used by catalogue and canonical-ID queries, matching the catalogue's World,
    /// Characters, Locations group order.
    /// </summary>
    private static readonly LoreSubjectKind[] _scopeKindOrder =
        [LoreSubjectKind.World, LoreSubjectKind.Character, LoreSubjectKind.Location];

    private readonly Action<string>? _warn = logger is null
        ? null
        : message => logger.LogWarning("Lore: {Warning}", message);

    /// <summary>
    /// The base canonical-ID queries resolve against when their query defers base selection (AI-004 requirement
    /// 52). Defaults to the canonical wiki because no-observer canonical reads are the first-class service
    /// capability; LLM-facing composition always selects the observer's perspective explicitly.
    /// </summary>
    public LoreBase ConfiguredBase
    {
        get;
        init;
    } = LoreBase.Wiki;

    /// <summary>
    /// Test instrumentation observing every Markdown file read at the moment the file opens successfully and
    /// before its text is read. Production leaves it unset; deterministic cancellation tests use it to trigger
    /// cancellation during a specific read (AI-004 requirement 49).
    /// </summary>
    internal Action<string>? FileReadObserver
    {
        get; set;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LoreEntry>> QueryAsync(
        ContentContext content,
        LoreQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        // Contextual selection is an LLM-facing prompt concern: it always reads the observer's perspective and
        // never substitutes canonical wiki content (AI-004 requirements 25 to 27 and 52).
        string perspectiveRoot = GetPerspectiveRoot(content, query.ObserverID);
        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = [];
        List<LoreEntry> entries = [];

        foreach (LoreSubjectRequest request in query.Subjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documentsByKind.TryGetValue(request.Kind, out IReadOnlyList<LoreMarkdownDocument>? documents))
            {
                documents = ReadCollection(perspectiveRoot, request.Kind, LoreBase.Perspective, cancellationToken, _warn);
                documentsByKind.Add(request.Kind, documents);
            }

            List<LoreMarkdownDocument> requestDocuments = [];
            foreach (LoreMarkdownDocument document in documents)
            {
                if (Matches(request, document))
                {
                    requestDocuments.Add(document);
                }
            }

            requestDocuments.Sort(CompareDocuments);
            foreach (LoreMarkdownDocument document in requestDocuments)
            {
                entries.Add(ToEntry(document));
            }
        }

        // Cancellation observed during the reads must cancel the whole query before it completes successfully
        // (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LoreEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LoreEntry>> QueryCatalogueAsync(
        ContentContext content,
        LoreCatalogueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = ReadScope(
            content,
            query.ObserverID,
            LoreBase.Perspective,
            cancellationToken,
            _warn);
        // Cancellation observed during the reads must cancel the whole query before it completes successfully,
        // including through result construction (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScopedAuthoringIdentityUniqueness(documentsByKind);

        List<LoreEntry> entries = [];
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            List<LoreMarkdownDocument> documents = [.. documentsByKind[kind]];
            documents.Sort(CompareDocuments);
            foreach (LoreMarkdownDocument document in documents)
            {
                entries.Add(ToEntry(document));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LoreEntry>>(entries);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<LoreEntryLookup>> QueryEntriesAsync(
        ContentContext content,
        LoreEntryIDQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        LoreBase loreBase = query.Base ?? ConfiguredBase;
        string? observerID = query.ObserverID;
        if (loreBase == LoreBase.Perspective && observerID is null)
        {
            throw new InvalidOperationException(
                "A perspective-base lore entry query requires an observer FullId; pass one explicitly or select " +
                "the canonical wiki base.");
        }

        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = ReadScope(
            content,
            observerID,
            loreBase,
            cancellationToken,
            _warn);
        // Cancellation observed during the reads must cancel the whole query before it completes successfully,
        // including through result construction (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScopedAuthoringIdentityUniqueness(documentsByKind);

        // Authored dot-handle ids index the in-scope entries for the unavailable-result hint (AI-004 requirement
        // 48); they never take part in canonical resolution themselves.
        Dictionary<string, LoreMarkdownDocument> documentsByAuthoringID = new(StringComparer.Ordinal);
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            foreach (LoreMarkdownDocument document in documentsByKind[kind])
            {
                if (document.ID is not null)
                {
                    _ = documentsByAuthoringID.TryAdd(document.ID, document);
                }
            }
        }

        List<LoreEntryLookup> lookups = new(query.EntryIDs.Count);
        foreach (string entryID in query.EntryIDs)
        {
            LoreMarkdownDocument? match = ResolveCanonicalID(entryID, documentsByKind);
            lookups.Add(match is null
                ? new LoreEntryLookup(
                    entryID,
                    Entry: null,
                    HintCanonicalID: documentsByAuthoringID.TryGetValue(entryID, out LoreMarkdownDocument? hinted)
                        ? hinted.CanonicalID
                        : null)
                : new LoreEntryLookup(entryID, ToEntry(match)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LoreEntryLookup>>(lookups);
    }

    /// <summary>
    /// Resolves one requested canonical ID against the read scope through the type→directory map and segment
    /// path with hyphen/underscore equivalence (AI-004 requirements 43 and 47). Unsupported types such as
    /// <c>scenario:</c>, authoring dot handles, and malformed strings resolve to <c>null</c> — unavailable, never
    /// an error. One canonical ID maps to at most one file per base by construction (per-directory collision
    /// validation), so the first equivalent document is the only one.
    /// </summary>
    private static LoreMarkdownDocument? ResolveCanonicalID(
        string requestedID,
        IReadOnlyDictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind)
    {
        LoreSubjectKind? kind = LoreCanonicalIDs.TryResolveKind(requestedID);
        if (kind is null)
        {
            return null;
        }

        foreach (LoreMarkdownDocument document in documentsByKind[kind.Value])
        {
            if (LoreCanonicalIDs.Equivalent(requestedID, document.CanonicalID))
            {
                return document;
            }
        }

        return null;
    }

    private static string GetPerspectiveRoot(ContentContext content, string observerID)
    {
        int observerSeparator = observerID.IndexOf(':', StringComparison.Ordinal);
        string observerType = observerID[..observerSeparator];
        string observerId = observerID[(observerSeparator + 1)..];
        return CombineResourcePath(content.RootPath, $"lore/perspectives/{observerType}/{observerId}/");
    }

    /// <summary>
    /// Gets the lore base root for one base: the canonical wiki — which the three runtime collections read
    /// directly, leaving the authoring-only <c>scenarios/</c> collection unread so <c>scenario:</c> IDs stay
    /// unavailable everywhere — or one observer's perspective root (AI-004 requirements 4 and 52).
    /// </summary>
    private static string GetBaseRoot(ContentContext content, LoreBase loreBase, string? observerID)
        => loreBase == LoreBase.Wiki
            ? CombineResourcePath(content.RootPath, WikiRootSuffix)
            : GetPerspectiveRoot(content, observerID!);

    /// <summary>
    /// Reads every runtime-eligible collection in one lore-base scope: world, characters, then locations,
    /// including nested subdirectories at any depth.
    /// </summary>
    private Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> ReadScope(
        ContentContext content,
        string? observerID,
        LoreBase loreBase,
        CancellationToken cancellationToken,
        Action<string>? warn)
    {
        string baseRoot = GetBaseRoot(content, loreBase, observerID);
        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = [];
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documentsByKind.Add(kind, ReadCollection(baseRoot, kind, loreBase, cancellationToken, warn));
            // A cancellation landing inside a collection's final read must not let the scope read complete
            // successfully (AI-004 requirement 49).
            cancellationToken.ThrowIfCancellationRequested();
        }

        return documentsByKind;
    }

    /// <summary>
    /// Enforces AI-004 requirement 43: authoring identities — authored dot-handle ids and their path-derived
    /// fallbacks — must be unique across all runtime-eligible world, character, and location entries within one
    /// lore-base scope, including nested pages. The thrown diagnostics name the conflicting source paths for
    /// internal triage only; model-facing failures must not surface them. Reuse across observers or content
    /// roots stays valid because validation never crosses a scope boundary.
    /// </summary>
    private static void ValidateScopedAuthoringIdentityUniqueness(
        IReadOnlyDictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind)
    {
        Dictionary<string, string> sourcePathsByIdentity = new(StringComparer.Ordinal);
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            foreach (LoreMarkdownDocument document in documentsByKind[kind])
            {
                if (sourcePathsByIdentity.TryGetValue(document.AuthoringID, out string? existingSourcePath))
                {
                    throw new InvalidOperationException(
                        $"Duplicate lore authoring identity '{document.AuthoringID}' at '{document.SourcePath}' " +
                        $"conflicts with the entry at '{existingSourcePath}'. Lore authoring identities must be " +
                        "unique across all runtime-eligible world, character, and location entries within one " +
                        "lore-base scope.");
                }

                sourcePathsByIdentity.Add(document.AuthoringID, document.SourcePath);
            }
        }
    }

    private IReadOnlyList<LoreMarkdownDocument> ReadCollection(
        string baseRoot,
        LoreSubjectKind kind,
        LoreBase loreBase,
        CancellationToken cancellationToken,
        Action<string>? warn)
    {
        string collectionPath = baseRoot + GetFolderName(kind) + "/";
        List<LoreMarkdownDocument> documents = [];
        foreach ((string path, string relativeStem) in EnumerateMarkdownFiles(collectionPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoreMarkdownDocument? document = ReadDocument(path, kind, relativeStem, loreBase, warn);
            // Cancellation can land inside the read itself — including the collection's final read, whose
            // cancellation would otherwise never be observed before completion (AI-004 requirement 49).
            cancellationToken.ThrowIfCancellationRequested();
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        ValidateSegmentCollisions(documents);
        return documents;
    }

    /// <summary>
    /// Enforces AI-004 requirement 43: two runtime-eligible files in one directory whose stems collide under the
    /// hyphen/underscore equivalence fail load validation, so one canonical ID maps to at most one file per base
    /// and canonical lookups stay unambiguous by construction. Authoring-only files never take part because they
    /// are not addressable.
    /// </summary>
    private static void ValidateSegmentCollisions(IReadOnlyList<LoreMarkdownDocument> documents)
    {
        Dictionary<(string Directory, string Stem), string> sourcePathsByKey = [];
        foreach (LoreMarkdownDocument document in documents)
        {
            int separator = document.CanonicalID.IndexOf(':', StringComparison.Ordinal);
            string relativeStem = document.CanonicalID[(separator + 1)..];
            int lastSeparator = relativeStem.LastIndexOf('/');
            (string directory, string stem) key = (
                lastSeparator < 0 ? string.Empty : relativeStem[..lastSeparator],
                LoreCanonicalIDs.NormaliseSegment(
                    lastSeparator < 0 ? relativeStem : relativeStem[(lastSeparator + 1)..]));

            if (sourcePathsByKey.TryGetValue(key, out string? existingSourcePath))
            {
                throw new InvalidOperationException(
                    $"The lore files '{document.SourcePath}' and '{existingSourcePath}' collide under the " +
                    "hyphen/underscore equivalence: two runtime-eligible files in one directory cannot share the " +
                    "same canonical path segment.");
            }

            sourcePathsByKey.Add(key, document.SourcePath);
        }
    }

    internal static bool Matches(LoreSubjectRequest request, LoreMarkdownDocument document)
        => request.Kind == LoreSubjectKind.World
            ? document.Essential
            : request.Kind == document.Kind && LoreCanonicalIDs.Equivalent(request.SubjectID!, document.SubjectID!);

    private static LoreEntry ToEntry(LoreMarkdownDocument document)
        => new(
            document.CanonicalID,
            document.Title,
            document.Body,
            document.Priority,
            document.Kind,
            document.SubjectID,
            document.Description);

    /// <summary>
    /// Orders documents by priority, then authoring identity — the authored dot-handle <c>id</c> or its
    /// path-derived fallback — then title, then source path using ordinal comparisons (AI-004 requirement 14).
    /// Directly constructed documents without a canonical ID sort their null fallback first defensively.
    /// </summary>
    internal static int CompareDocuments(LoreMarkdownDocument left, LoreMarkdownDocument right)
    {
        int comparison = left.Priority.CompareTo(right.Priority);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.Compare(left.AuthoringID, right.AuthoringID, StringComparison.Ordinal);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.Compare(left.Title, right.Title, StringComparison.Ordinal);
        return comparison != 0
            ? comparison
            : string.Compare(left.SourcePath, right.SourcePath, StringComparison.Ordinal);
    }

    private static string GetFolderName(LoreSubjectKind kind) => kind switch
    {
        LoreSubjectKind.World => "world",
        LoreSubjectKind.Location => "locations",
        LoreSubjectKind.Character => "characters",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported lore subject kind."),
    };

    /// <summary>
    /// Enumerates every Markdown file under one collection directory at any depth, yielding the file's resource
    /// path plus its collection-relative stem — the wiki-relative path without extension whose segments form the
    /// canonical ID (AI-004 requirement 43).
    /// </summary>
    private static IEnumerable<(string Path, string RelativeStem)> EnumerateMarkdownFiles(string directoryPath)
    {
        foreach ((string path, string relativeStem) in EnumerateMarkdownFiles(directoryPath, string.Empty))
        {
            yield return (path, relativeStem);
        }
    }

    private static IEnumerable<(string Path, string RelativeStem)> EnumerateMarkdownFiles(
        string directoryPath,
        string relativePrefix)
    {
        var directory = DirAccess.Open(directoryPath);
        if (directory is null)
        {
            yield break;
        }

        string[] files = directory.GetFiles();
        Array.Sort(files, StringComparer.Ordinal);
        foreach (string fileName in files)
        {
            if (fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                yield return (directoryPath + fileName, relativePrefix + fileName[..^".md".Length]);
            }
        }

        string[] directories = directory.GetDirectories();
        Array.Sort(directories, StringComparer.Ordinal);
        foreach (string childDirectory in directories)
        {
            foreach ((string path, string relativeStem) in EnumerateMarkdownFiles(
                         directoryPath + childDirectory + "/",
                         relativePrefix + childDirectory + "/"))
            {
                yield return (path, relativeStem);
            }
        }
    }

    private LoreMarkdownDocument? ReadDocument(
        string path,
        LoreSubjectKind kind,
        string relativeStem,
        LoreBase loreBase,
        Action<string>? warn)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException($"Could not read lore file '{path}'. Godot FileAccess error: {error}.");
        }

        FileReadObserver?.Invoke(path);
        return ParseDocument(file.GetAsText(), path, kind, relativeStem, loreBase, warn);
    }

    /// <summary>
    /// Parses a Markdown lore page with path-authoritative identity (AI-004 requirements 6, 9, and 36 to 41):
    /// the entry's kind, canonical subject binding, and canonical full ID derive from
    /// <paramref name="kind" /> and <paramref name="relativeStem" />, never from frontmatter. Read-time triage
    /// applies to both bases: a page runs when its frontmatter carries an <c>id</c> or a <c>title</c> — a
    /// title-bearing id-less page loads with its path-derived authoring identity — while pages with no
    /// frontmatter block or neither field are authoring-time only and skip silently. Unterminated frontmatter,
    /// unclosed <c>lore:ignore</c> fences, and bodies emptied by cleaning skip with a warning through
    /// <paramref name="warn" />. Field validation stays fail-hard on runtime-eligible pages: a <c>subject_id</c>
    /// field is rejected outright because identity is path-authoritative, an optional <c>type</c> contradicting
    /// the collection directory fails load validation, and wiki pages carrying an <c>id</c> additionally require
    /// a load-bearing <c>type</c> and a bare <c>id</c> that matches the page's path under hyphen/underscore
    /// equivalence. The optional <c>description</c> is a single-line frontmatter value where absent or blank
    /// means no description (AI-004 requirement 42).
    /// </summary>
    internal static LoreMarkdownDocument? ParseDocument(
        string markdown,
        string sourcePath,
        LoreSubjectKind kind = LoreSubjectKind.World,
        string relativeStem = "page",
        LoreBase loreBase = LoreBase.Perspective,
        Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string normalised = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (normalised != "---" && !normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            // No frontmatter block: the page is authoring-time only and is skipped without a signal.
            return null;
        }

        int end = normalised.Length < 5 ? -1 : normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            // A stray '---' must not break runtime, but dropping a possibly runtime-eligible entry deserves a signal.
            warn?.Invoke($"Skipped lore page '{sourcePath}': its frontmatter block never closes after opening '---'.");
            return null;
        }

        Dictionary<string, string> frontmatter = ParseFrontmatter(normalised[4..end]);
        if (frontmatter.ContainsKey("subject_id"))
        {
            // Identity is path-authoritative (AI-004 requirement 9): no frontmatter field restates the path's
            // identity decisions, so the legacy field is a load-validation failure on both bases.
            throw new InvalidOperationException(
                $"Lore page '{sourcePath}' carries a 'subject_id' frontmatter field. Entry identity is " +
                "path-authoritative: remove 'subject_id' and place the entry at its canonical path instead.");
        }

        string? id = GetOptionalField(frontmatter, "id");
        string? titleField = GetOptionalField(frontmatter, "title");
        if (id is null && titleField is null)
        {
            // Frontmatter carrying neither an 'id' nor a 'title': authoring-time only, skipped without a signal.
            return null;
        }

        string title = GetRequiredField(frontmatter, "title", sourcePath);
        bool essential = ParseEssential(frontmatter, sourcePath);
        int priority = ParsePriority(frontmatter, sourcePath);
        string? description = GetOptionalField(frontmatter, "description");
        string? type = GetOptionalField(frontmatter, "type");
        if (loreBase == LoreBase.Wiki && id is not null)
        {
            // Wiki pages restate their path identity with a bare 'id' plus load-bearing 'type' (AI-004
            // requirement 9), both validated against the page's actual position.
            type ??= GetRequiredField(frontmatter, "type", sourcePath);
            ValidateTypeMatchesDirectory(type, kind, sourcePath);
            ValidateWikiIDMatchesPath(id, relativeStem, sourcePath);
        }
        else if (type is not null)
        {
            ValidateTypeMatchesDirectory(type, kind, sourcePath);
        }

        string body = CleanBody(normalised[(end + "\n---\n".Length)..], sourcePath, warn);
        if (body.Length == 0)
        {
            warn?.Invoke($"Skipped lore page '{sourcePath}': its body is empty after parse-time cleaning.");
            return null;
        }

        string canonicalID = LoreCanonicalIDs.Compose(kind, relativeStem);
        return new LoreMarkdownDocument(
            id,
            title,
            kind == LoreSubjectKind.World ? null : canonicalID,
            essential,
            priority,
            body,
            sourcePath,
            description,
            canonicalID,
            kind);
    }

    /// <summary>
    /// Validates an optional frontmatter <c>type</c> against the collection directory the page actually sits in:
    /// a mismatch fails load validation because the path owns the kind decision (AI-004 requirements 9 and 36).
    /// </summary>
    private static void ValidateTypeMatchesDirectory(string type, LoreSubjectKind kind, string sourcePath)
    {
        if (!string.Equals(type, LoreCanonicalIDs.GetTypePrefix(kind), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lore page '{sourcePath}' declares frontmatter type '{type}', but its collection directory " +
                $"'{GetFolderName(kind)}' addresses kind '{LoreCanonicalIDs.GetTypePrefix(kind)}'. Remove or " +
                "correct the 'type' field so it matches the entry's path.");
        }
    }

    /// <summary>
    /// Validates a wiki page's bare <c>id</c> against its path segments, hyphen/underscore-equivalent
    /// (AI-004 requirement 9): composed full IDs are rejected because frontmatter values stay bare.
    /// </summary>
    private static void ValidateWikiIDMatchesPath(string id, string relativeStem, string sourcePath)
    {
        if (id.Contains(':', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Lore page '{sourcePath}' declares composed full ID '{id}' as its frontmatter 'id'. Wiki ids " +
                "are bare path restatements — for example 'id: vadim' plus 'type: char' — never 'type:id' values.");
        }

        if (!LoreCanonicalIDs.PathsEquivalent(id, relativeStem))
        {
            throw new InvalidOperationException(
                $"Lore page '{sourcePath}' declares frontmatter id '{id}', which does not match its path " +
                $"'{relativeStem}' under hyphen/underscore equivalence. The path owns the entry's identity.");
        }
    }

    private static string GetRequiredField(
        IReadOnlyDictionary<string, string> frontmatter,
        string field,
        string sourcePath)
    {
        return !frontmatter.TryGetValue(field, out string? value) || string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Lore page '{sourcePath}' requires a non-empty '{field}' frontmatter field.")
            : value.Trim();
    }

    private static string? GetOptionalField(IReadOnlyDictionary<string, string> frontmatter, string field)
        => frontmatter.TryGetValue(field, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    private static bool ParseEssential(IReadOnlyDictionary<string, string> frontmatter, string sourcePath)
    {
        bool essential = false;
        return !frontmatter.TryGetValue("essential", out string? value) || bool.TryParse(value, out essential)
            ? essential
            : throw new InvalidOperationException(
                $"Lore page '{sourcePath}' has invalid 'essential' frontmatter value '{value}'. Expected boolean 'true' or 'false'.");
    }

    private static int ParsePriority(IReadOnlyDictionary<string, string> frontmatter, string sourcePath)
    {
        return !frontmatter.TryGetValue("priority", out string? value)
            ? 0
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int priority)
            ? priority
            : throw new InvalidOperationException(
                $"Lore page '{sourcePath}' has invalid 'priority' frontmatter value '{value}'. Expected an integer.");
    }

    private static Dictionary<string, string> ParseFrontmatter(string frontmatter)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in frontmatter.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            int separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            result[line[..separator].Trim()] = line[(separator + 1)..].Trim().Trim('"');
        }

        return result;
    }

    /// <summary>
    /// Cleans a lore body at parse time before any entry is constructed: omission fences, bare HTML comments,
    /// and link syntax are removed outside fenced code blocks, which pass through verbatim (AI-004 requirements
    /// 37 to 40). Comment stripping runs before link reduction so comment remnants are never mistaken for
    /// autolinks. Returns the trimmed cleaned body, which may be empty.
    /// </summary>
    private static string CleanBody(string body, string sourcePath, Action<string>? warn)
    {
        string withoutOmissionFences = RemoveOmissionFences(body, sourcePath, warn);
        string withoutComments = StripHtmlComments(withoutOmissionFences);

        return ReduceLinks(withoutComments).Trim();
    }

    /// <summary>
    /// Removes <c>lore:ignore</c> omission blocks: a block runs from a line that is exactly
    /// <c>&lt;!-- lore:ignore --&gt;</c> to the next line that is exactly <c>&lt;!-- /lore:ignore --&gt;</c>,
    /// both markers inclusive. An unclosed block removes to the end of the body with a warning.
    /// </summary>
    private static string RemoveOmissionFences(string body, string sourcePath, Action<string>? warn)
    {
        string[] lines = body.Split('\n');
        List<string> result = new(lines.Length);
        bool inFence = false;
        bool inOmission = false;

        foreach (string line in lines)
        {
            if (inFence)
            {
                result.Add(line);
                inFence = !IsCodeFence(line);
                continue;
            }

            if (inOmission)
            {
                if (line.Trim() == IgnoreCloseMarker)
                {
                    inOmission = false;
                }

                continue;
            }

            if (IsCodeFence(line))
            {
                result.Add(line);
                inFence = true;
                continue;
            }

            if (line.Trim() == IgnoreOpenMarker)
            {
                inOmission = true;
                continue;
            }

            result.Add(line);
        }

        if (inOmission)
        {
            warn?.Invoke(
                $"Lore page '{sourcePath}': content after an unclosed 'lore:ignore' block was removed " +
                $"because no closing '{IgnoreCloseMarker}' marker was found.");
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Strips all bare HTML comments outside fenced code blocks, including comments that span lines. An
    /// unterminated comment consumes the rest of the body.
    /// </summary>
    private static string StripHtmlComments(string body)
    {
        string[] lines = body.Split('\n');
        List<string> result = new(lines.Length);
        bool inFence = false;
        bool inComment = false;

        foreach (string line in lines)
        {
            if (inFence)
            {
                result.Add(line);
                inFence = !IsCodeFence(line);
                continue;
            }

            if (inComment)
            {
                int close = line.IndexOf(CommentCloseMarker, StringComparison.Ordinal);
                if (close < 0)
                {
                    continue;
                }

                inComment = false;
                result.Add(StripCommentsFromSegment(line[(close + CommentCloseMarker.Length)..], ref inComment));
                continue;
            }

            if (IsCodeFence(line))
            {
                result.Add(line);
                inFence = true;
                continue;
            }

            result.Add(StripCommentsFromSegment(line, ref inComment));
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Removes every complete <c>&lt;!-- ... --&gt;</c> span from a single line segment. A segment that opens a
    /// comment without closing it keeps only its text before the opener and flags <paramref name="inComment" />.
    /// </summary>
    private static string StripCommentsFromSegment(string segment, ref bool inComment)
    {
        StringBuilder result = new(segment.Length);
        int index = 0;

        while (index < segment.Length)
        {
            int open = segment.IndexOf(CommentOpenMarker, index, StringComparison.Ordinal);
            if (open < 0)
            {
                _ = result.Append(segment[index..]);
                break;
            }

            _ = result.Append(segment[index..open]);
            int close = segment.IndexOf(CommentCloseMarker, open + CommentOpenMarker.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                inComment = true;
                break;
            }

            index = close + CommentCloseMarker.Length;
        }

        return result.ToString();
    }

    /// <summary>
    /// Reduces link syntax to its label text outside fenced code blocks: inline, reference, collapsed, and
    /// autolink forms are reduced, annotated pipe wiki references reduce to their label with the annotation
    /// retained, and reference definition lines are dropped.
    /// </summary>
    private static string ReduceLinks(string body)
    {
        string[] lines = body.Split('\n');
        List<string> result = new(lines.Length);
        bool inFence = false;

        foreach (string line in lines)
        {
            if (inFence)
            {
                result.Add(line);
                inFence = !IsCodeFence(line);
                continue;
            }

            if (IsCodeFence(line))
            {
                result.Add(line);
                inFence = true;
                continue;
            }

            if (IsLinkDefinitionLine(line))
            {
                continue;
            }

            result.Add(ReduceLinksInLine(line));
        }

        return string.Join("\n", result);
    }

    /// <summary>
    /// Determines whether a line is a reference link definition (<c>[ref]: target</c>): such lines carry no
    /// prompt-facing content and are dropped wholesale.
    /// </summary>
    private static bool IsLinkDefinitionLine(string line)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith('['))
        {
            return false;
        }

        int close = FindMatchingBracket(trimmed, 0);

        return close >= 0 && close + 1 < trimmed.Length && trimmed[close + 1] == ':';
    }

    /// <summary>
    /// Reduces the link forms in a single line: <c>[label](target)</c>, <c>[label][ref]</c>, <c>[label][]</c>,
    /// and autolinks become their label or URL text, and an annotated pipe wiki reference
    /// <c>[[target|label]](annotation)</c> becomes its label with the annotation retained verbatim. Bare
    /// <c>[label]</c>, image syntax, and wiki links without an annotation have no following <c>(</c> or
    /// <c>[</c> or are preceded by <c>!</c> and pass through untouched.
    /// </summary>
    private static string ReduceLinksInLine(string line)
    {
        StringBuilder result = new(line.Length);
        int index = 0;

        while (index < line.Length)
        {
            char current = line[index];
            if (current == '[' && (index == 0 || line[index - 1] != '!'))
            {
                if (TryReduceAnnotatedWikiReference(line, index, out string? label, out int end))
                {
                    _ = result.Append(label);
                    index = end;
                    continue;
                }

                if (TryReduceBracketLink(line, index, out label, out end))
                {
                    _ = result.Append(label);
                    index = end;
                    continue;
                }
            }
            else if (current == '<')
            {
                if (TryReduceAutolink(line, index, out string? content, out int end))
                {
                    _ = result.Append(content);
                    index = end;
                    continue;
                }
            }

            _ = result.Append(current);
            index++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Attempts to reduce an annotated pipe wiki reference starting at <paramref name="openIndex" />: an
    /// Obsidian-style pipe wiki link <c>[[target|label]]</c> immediately followed by a complete parenthesised
    /// annotation yields its alias text (everything after the first pipe) with the annotation appended
    /// verbatim, and the index past the reduced span. Pipe wiki links without an annotation, pipe-less wiki
    /// links, and embeds (preceded by <c>!</c>) are not this construct and are left to the remaining rules.
    /// </summary>
    private static bool TryReduceAnnotatedWikiReference(
        string line,
        int openIndex,
        [NotNullWhen(true)] out string? label,
        out int end)
    {
        label = null;
        end = 0;

        // A wiki link opens with two consecutive brackets; a single bracket is ordinary link syntax.
        if (openIndex + 1 >= line.Length || line[openIndex + 1] != '[')
        {
            return false;
        }

        int close = FindMatchingBracket(line, openIndex);
        if (close < 1 || line[close - 1] != ']')
        {
            return false;
        }

        // The annotation must be a complete parenthesised span on the same line.
        if (close + 1 >= line.Length || line[close + 1] != '(')
        {
            return false;
        }

        int annotationClose = FindMatchingParen(line, close + 1);
        if (annotationClose < 0)
        {
            return false;
        }

        string content = line[(openIndex + 2)..(close - 1)];
        int separator = content.IndexOf('|');
        if (separator < 0)
        {
            return false;
        }

        label = content[(separator + 1)..] + line[(close + 1)..(annotationClose + 1)];
        end = annotationClose + 1;

        return true;
    }

    /// <summary>
    /// Attempts to reduce a bracketed link starting at <paramref name="openIndex" />: an inline
    /// <c>[label](target)</c>, reference <c>[label][ref]</c>, or collapsed <c>[label][]</c> form yields its
    /// label text and the index past the reduced span.
    /// </summary>
    private static bool TryReduceBracketLink(
        string line,
        int openIndex,
        [NotNullWhen(true)] out string? label,
        out int end)
    {
        label = null;
        end = 0;

        int close = FindMatchingBracket(line, openIndex);
        if (close < 0 || close + 1 >= line.Length)
        {
            return false;
        }

        int spanEnd = line[close + 1] switch
        {
            '(' => FindMatchingParen(line, close + 1),
            '[' => FindMatchingBracket(line, close + 1),
            _ => -1,
        };

        if (spanEnd < 0)
        {
            return false;
        }

        label = line[(openIndex + 1)..close];
        end = spanEnd + 1;

        return true;
    }

    /// <summary>
    /// Attempts to reduce an autolink starting at <paramref name="openIndex" />: a single
    /// <c>&lt;url&gt;</c> token without internal whitespace yields its URL text.
    /// </summary>
    private static bool TryReduceAutolink(
        string line,
        int openIndex,
        [NotNullWhen(true)] out string? content,
        out int end)
    {
        content = null;
        end = 0;

        int close = line.IndexOf('>', openIndex + 1);
        if (close <= openIndex + 1)
        {
            return false;
        }

        string candidate = line[(openIndex + 1)..close];
        if (candidate.Any(char.IsWhiteSpace))
        {
            return false;
        }

        content = candidate;
        end = close + 1;

        return true;
    }

    /// <summary>
    /// Finds the index of the <c>]</c> matching the <c>[</c> at <paramref name="openIndex" />, counting nested
    /// bracket pairs within the line.
    /// </summary>
    private static int FindMatchingBracket(string line, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < line.Length; i++)
        {
            char current = line[i];
            if (current == '[')
            {
                depth++;
            }
            else if (current == ']')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the index of the <c>)</c> matching the <c>(</c> at <paramref name="openIndex" />, counting nested
    /// paren pairs within the line.
    /// </summary>
    private static int FindMatchingParen(string line, int openIndex)
    {
        int depth = 0;
        for (int i = openIndex; i < line.Length; i++)
        {
            char current = line[i];
            if (current == '(')
            {
                depth++;
            }
            else if (current == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Determines whether a line opens or closes a backtick fenced code block, mirroring the fence semantics of
    /// <see cref="MarkdownLorePromptFormatter" />: any line whose first non-whitespace characters are three
    /// backticks toggles the fence state.
    /// </summary>
    private static bool IsCodeFence(string line) => line.TrimStart().StartsWith("```", StringComparison.Ordinal);

    private static string CombineResourcePath(string rootPath, string relativePath)
        => rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath + relativePath : rootPath + "/" + relativePath;

    /// <summary>
    /// One parsed runtime-eligible lore page. <see cref="CanonicalID" /> is the path-derived canonical full ID
    /// and the only identity that reaches <see cref="LoreEntry" />; <see cref="ID" /> is the optional authored
    /// dot-handle id, an authoring identity that never appears model-visible. <see cref="AuthoringID" /> — the
    /// authored dot handle or its canonical-ID fallback — drives deterministic ordering and per-scope uniqueness
    /// (AI-004 requirement 14). The source path stays backend-private for diagnostics and the final sorting
    /// tie-breaker (AI-004 requirement 20).
    /// </summary>
    internal sealed record LoreMarkdownDocument(
        string? ID,
        string Title,
        string? SubjectID,
        bool Essential,
        int Priority,
        string Body,
        string SourcePath,
        string? Description = null,
        string CanonicalID = "",
        LoreSubjectKind Kind = LoreSubjectKind.World)
    {
        /// <summary>Gets the entry's authoring identity: its authored dot-handle id or canonical-ID fallback.</summary>
        public string AuthoringID => ID ?? CanonicalID;
    }
}
