using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using AlleyCat.Core.Content;
using Godot;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Read-only Markdown-backed access to content-scoped perspective lore.
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

    /// <summary>
    /// The scope-wide collection order used by catalogue and entry-ID queries, matching the catalogue's World,
    /// Characters, Locations group order.
    /// </summary>
    private static readonly LoreSubjectKind[] _scopeKindOrder =
        [LoreSubjectKind.World, LoreSubjectKind.Character, LoreSubjectKind.Location];

    private readonly Action<string>? _warn = logger is null
        ? null
        : message => logger.LogWarning("Lore: {Warning}", message);

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

        string perspectiveRoot = GetPerspectiveRoot(content, query.ObserverID);
        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = [];
        List<LoreEntry> entries = [];

        foreach (LoreSubjectRequest request in query.Subjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documentsByKind.TryGetValue(request.Kind, out IReadOnlyList<LoreMarkdownDocument>? documents))
            {
                documents = ReadCollection(perspectiveRoot, request.Kind, cancellationToken, _warn);
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
                entries.Add(ToEntry(document, request.Kind));
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

        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind =
            ReadScope(content, query.ObserverID, cancellationToken, _warn);
        // Cancellation observed during the reads must cancel the whole query before it completes successfully,
        // including through result construction (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScopedEntryIDUniqueness(documentsByKind);

        List<LoreEntry> entries = [];
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            List<LoreMarkdownDocument> documents = [.. documentsByKind[kind]];
            documents.Sort(CompareDocuments);
            foreach (LoreMarkdownDocument document in documents)
            {
                entries.Add(ToEntry(document, kind));
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

        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind =
            ReadScope(content, query.ObserverID, cancellationToken, _warn);
        // Cancellation observed during the reads must cancel the whole query before it completes successfully,
        // including through result construction (AI-004 requirement 49).
        cancellationToken.ThrowIfCancellationRequested();
        ValidateScopedEntryIDUniqueness(documentsByKind);

        Dictionary<string, (LoreMarkdownDocument Document, LoreSubjectKind Kind)> documentsByID =
            new(StringComparer.Ordinal);
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            foreach (LoreMarkdownDocument document in documentsByKind[kind])
            {
                if (document.ID is not null)
                {
                    documentsByID.Add(document.ID, (document, kind));
                }
            }
        }

        List<LoreEntryLookup> lookups = new(query.EntryIDs.Count);
        foreach (string entryID in query.EntryIDs)
        {
            lookups.Add(documentsByID.TryGetValue(entryID, out (LoreMarkdownDocument Document, LoreSubjectKind Kind) match)
                ? new LoreEntryLookup(entryID, ToEntry(match.Document, match.Kind))
                : new LoreEntryLookup(entryID, null));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LoreEntryLookup>>(lookups);
    }

    private static string GetPerspectiveRoot(ContentContext content, string observerID)
    {
        int observerSeparator = observerID.IndexOf(':', StringComparison.Ordinal);
        string observerType = observerID[..observerSeparator];
        string observerId = observerID[(observerSeparator + 1)..];
        return CombineResourcePath(content.RootPath, $"lore/perspectives/{observerType}/{observerId}/");
    }

    /// <summary>
    /// Reads every runtime-eligible collection in one observer/content scope: world, characters, then
    /// locations, including nested subdirectories at any depth.
    /// </summary>
    private Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> ReadScope(
        ContentContext content,
        string observerID,
        CancellationToken cancellationToken,
        Action<string>? warn)
    {
        string perspectiveRoot = GetPerspectiveRoot(content, observerID);
        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = [];
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documentsByKind.Add(kind, ReadCollection(perspectiveRoot, kind, cancellationToken, warn));
            // A cancellation landing inside a collection's final read must not let the scope read complete
            // successfully (AI-004 requirement 49).
            cancellationToken.ThrowIfCancellationRequested();
        }

        return documentsByKind;
    }

    /// <summary>
    /// Enforces AI-004 requirement 43: entry IDs must be ordinally unique across all runtime-eligible world,
    /// character, and location entries within one observer/content scope, including nested pages. The thrown
    /// diagnostics name the conflicting source paths for internal triage only; model-facing failures must not
    /// surface them. Reuse across observers or content roots stays valid because validation never crosses a
    /// scope boundary.
    /// </summary>
    private static void ValidateScopedEntryIDUniqueness(
        IReadOnlyDictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind)
    {
        Dictionary<string, string> sourcePathsByID = new(StringComparer.Ordinal);
        foreach (LoreSubjectKind kind in _scopeKindOrder)
        {
            foreach (LoreMarkdownDocument document in documentsByKind[kind])
            {
                if (document.ID is null)
                {
                    continue;
                }

                if (sourcePathsByID.TryGetValue(document.ID, out string? existingSourcePath))
                {
                    throw new InvalidOperationException(
                        $"Duplicate lore entry ID '{document.ID}' in the '{GetFolderName(kind)}' collection at " +
                        $"'{document.SourcePath}' conflicts with the entry at '{existingSourcePath}'. Lore entry IDs " +
                        "must be unique across all runtime-eligible world, character, and location entries within " +
                        "one observer and content scope.");
                }

                sourcePathsByID.Add(document.ID, document.SourcePath);
            }
        }
    }

    private IReadOnlyList<LoreMarkdownDocument> ReadCollection(
        string perspectiveRoot,
        LoreSubjectKind kind,
        CancellationToken cancellationToken,
        Action<string>? warn)
    {
        string collectionPath = perspectiveRoot + GetFolderName(kind) + "/";
        List<LoreMarkdownDocument> documents = [];
        foreach (string path in EnumerateMarkdownFiles(collectionPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoreMarkdownDocument? document = ReadDocument(path, kind, warn);
            // Cancellation can land inside the read itself — including the collection's final read, whose
            // cancellation would otherwise never be observed before completion (AI-004 requirement 49).
            cancellationToken.ThrowIfCancellationRequested();
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        return documents;
    }

    internal static bool Matches(LoreSubjectRequest request, LoreMarkdownDocument document)
        => request.Kind == LoreSubjectKind.World
            ? document.Essential
            : string.Equals(document.SubjectID, request.SubjectID, StringComparison.Ordinal);

    private static LoreEntry ToEntry(LoreMarkdownDocument document, LoreSubjectKind kind)
        => new(
            document.ID,
            document.Title,
            document.Body,
            document.Priority,
            kind,
            document.SubjectID,
            document.Description);

    /// <summary>
    /// Orders documents by priority, then ID, then title, then source path using ordinal comparisons. Runtime
    /// documents always carry a non-null ID because pages without one are skipped at read time (AI-004
    /// requirement 36); null IDs are only possible on directly constructed documents and sort first defensively.
    /// </summary>
    internal static int CompareDocuments(LoreMarkdownDocument left, LoreMarkdownDocument right)
    {
        int comparison = left.Priority.CompareTo(right.Priority);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = string.Compare(left.ID, right.ID, StringComparison.Ordinal);
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

    private static IEnumerable<string> EnumerateMarkdownFiles(string directoryPath)
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
                yield return directoryPath + fileName;
            }
        }

        string[] directories = directory.GetDirectories();
        Array.Sort(directories, StringComparer.Ordinal);
        foreach (string childDirectory in directories)
        {
            foreach (string filePath in EnumerateMarkdownFiles(directoryPath + childDirectory + "/"))
            {
                yield return filePath;
            }
        }
    }

    private LoreMarkdownDocument? ReadDocument(string path, LoreSubjectKind kind, Action<string>? warn)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException($"Could not read lore file '{path}'. Godot FileAccess error: {error}.");
        }

        FileReadObserver?.Invoke(path);
        return ParseDocument(file.GetAsText(), path, kind, warn);
    }

    /// <summary>
    /// Parses a Markdown lore page, applying read-time triage and parse-time body cleaning (AI-004 requirements
    /// 36 to 41). The optional <c>description</c> is a single-line frontmatter value where absent or blank means
    /// no description (AI-004 requirement 42). Returns <see langword="null" /> for skipped pages: pages without
    /// a frontmatter block or whose frontmatter carries no <c>id</c> are skipped silently, while unterminated
    /// frontmatter, unclosed <c>lore:ignore</c> fences, and bodies emptied by cleaning are skipped with a warning
    /// through <paramref name="warn" />. Frontmatter field validation stays fail-hard for <c>id</c>-bearing
    /// pages.
    /// </summary>
    internal static LoreMarkdownDocument? ParseDocument(
        string markdown,
        string sourcePath,
        LoreSubjectKind kind = LoreSubjectKind.World,
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
            // A stray '---' must not break runtime, but dropping a possibly 'id'-bearing entry deserves a signal.
            warn?.Invoke($"Skipped lore page '{sourcePath}': its frontmatter block never closes after opening '---'.");
            return null;
        }

        Dictionary<string, string> frontmatter = ParseFrontmatter(normalised[4..end]);
        string? id = GetOptionalField(frontmatter, "id");
        if (id is null)
        {
            // Frontmatter without an 'id': the page is authoring-time only and is skipped without a signal.
            return null;
        }

        string title = GetRequiredField(frontmatter, "title", sourcePath);
        bool essential = ParseEssential(frontmatter, sourcePath);
        int priority = ParsePriority(frontmatter, sourcePath);
        string? description = GetOptionalField(frontmatter, "description");
        string? subjectID = ParseSubjectID(frontmatter, sourcePath, kind);
        string body = CleanBody(normalised[(end + "\n---\n".Length)..], sourcePath, warn);
        if (body.Length == 0)
        {
            warn?.Invoke($"Skipped lore page '{sourcePath}': its body is empty after parse-time cleaning.");
            return null;
        }

        return new LoreMarkdownDocument(id, title, subjectID, essential, priority, body, sourcePath, description);
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

    private static string? ParseSubjectID(
        IReadOnlyDictionary<string, string> frontmatter,
        string sourcePath,
        LoreSubjectKind kind)
    {
        if (kind == LoreSubjectKind.World)
        {
            return null;
        }

        string subjectID = GetRequiredField(frontmatter, "subject_id", sourcePath);
        try
        {
            LoreSubjectRequest request = kind == LoreSubjectKind.Character
                ? LoreSubjectRequest.Character(subjectID)
                : LoreSubjectRequest.Location(subjectID);
            return request.SubjectID;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                $"Lore page '{sourcePath}' has invalid 'subject_id' frontmatter value '{subjectID}'.",
                exception);
        }
    }

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
    /// autolink forms are reduced, and reference definition lines are dropped.
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
    /// and autolinks become their label or URL text. Bare <c>[label]</c>, image syntax, and <c>[[wiki]]</c>
    /// links have no following <c>(</c> or <c>[</c> or are preceded by <c>!</c> and pass through untouched.
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
                if (TryReduceBracketLink(line, index, out string? label, out int end))
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

    internal sealed record LoreMarkdownDocument(
        string? ID,
        string Title,
        string? SubjectID,
        bool Essential,
        int Priority,
        string Body,
        string SourcePath,
        string? Description = null);
}
