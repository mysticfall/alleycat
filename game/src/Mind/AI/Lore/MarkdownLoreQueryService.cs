using System.Globalization;
using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Read-only Markdown-backed access to content-scoped perspective lore.
/// </summary>
public sealed class MarkdownLoreQueryService : ILoreQueryService
{
    /// <inheritdoc />
    public Task<IReadOnlyList<LoreEntry>> QueryAsync(
        ContentContext content,
        LoreQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        string perspectiveRoot = CombineResourcePath(
            content.RootPath,
            $"lore/perspectives/{query.ObserverID}/");
        Dictionary<LoreSubjectKind, IReadOnlyList<LoreMarkdownDocument>> documentsByKind = [];
        List<LoreEntry> entries = [];

        foreach (LoreSubjectRequest request in query.Subjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documentsByKind.TryGetValue(request.Kind, out IReadOnlyList<LoreMarkdownDocument>? documents))
            {
                documents = ReadCollection(perspectiveRoot, request.Kind, cancellationToken);
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

        return Task.FromResult<IReadOnlyList<LoreEntry>>(entries);
    }

    private static IReadOnlyList<LoreMarkdownDocument> ReadCollection(
        string perspectiveRoot,
        LoreSubjectKind kind,
        CancellationToken cancellationToken)
    {
        string collectionPath = perspectiveRoot + GetFolderName(kind) + "/";
        List<LoreMarkdownDocument> documents = [];
        foreach (string path in EnumerateMarkdownFiles(collectionPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            documents.Add(ReadDocument(path, kind));
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
            document.SubjectID);

    internal static int CompareDocuments(LoreMarkdownDocument left, LoreMarkdownDocument right)
    {
        int comparison = left.Priority.CompareTo(right.Priority);
        if (comparison != 0)
        {
            return comparison;
        }

        bool leftHasID = !string.IsNullOrEmpty(left.ID);
        bool rightHasID = !string.IsNullOrEmpty(right.ID);
        if (leftHasID != rightHasID)
        {
            return leftHasID ? -1 : 1;
        }

        if (leftHasID)
        {
            comparison = string.Compare(left.ID, right.ID, StringComparison.Ordinal);
            if (comparison != 0)
            {
                return comparison;
            }
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

    private static LoreMarkdownDocument ReadDocument(string path, LoreSubjectKind kind)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException($"Could not read lore file '{path}'. Godot FileAccess error: {error}.");
        }

        return ParseDocument(file.GetAsText(), path, kind);
    }

    internal static LoreMarkdownDocument ParseDocument(
        string markdown,
        string sourcePath,
        LoreSubjectKind kind = LoreSubjectKind.World)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string normalised = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Lore page '{sourcePath}' requires frontmatter with a non-empty 'title' field.");
        }

        int end = normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Lore page '{sourcePath}' has an unterminated frontmatter block.");
        }

        Dictionary<string, string> frontmatter = ParseFrontmatter(normalised[4..end]);
        string body = normalised[(end + "\n---\n".Length)..].Trim();
        string? id = GetOptionalField(frontmatter, "id");
        string title = GetRequiredField(frontmatter, "title", sourcePath);
        bool essential = ParseEssential(frontmatter, sourcePath);
        int priority = ParsePriority(frontmatter, sourcePath);
        string? subjectID = ParseSubjectID(frontmatter, sourcePath, kind);

        return new LoreMarkdownDocument(id, title, subjectID, essential, priority, body, sourcePath);
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
            return LoreQuery.NormaliseID(subjectID, "subject_id");
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

    private static string CombineResourcePath(string rootPath, string relativePath)
        => rootPath.EndsWith("/", StringComparison.Ordinal) ? rootPath + relativePath : rootPath + "/" + relativePath;

    internal sealed record LoreMarkdownDocument(
        string? ID,
        string Title,
        string? SubjectID,
        bool Essential,
        int Priority,
        string Body,
        string SourcePath);
}
