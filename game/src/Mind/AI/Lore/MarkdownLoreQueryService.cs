using AlleyCat.Core.Content;
using Godot;

namespace AlleyCat.Mind.AI.Lore;

/// <summary>
/// Initial Markdown-backed lore query service for canonical wiki source files.
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

        string loreWikiRoot = CombineResourcePath(content.RootPath, "lore/wiki/");
        List<LoreEntry> entries = [];
        foreach (string path in EnumerateMarkdownFiles(loreWikiRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoreMarkdownDocument document = ReadDocument(path);
            if (!query.EssentialOnly || document.Essential)
            {
                entries.Add(ToEntry(document, path));
            }
        }

        entries.Sort(static (left, right) => string.Compare(left.ID, right.ID, StringComparison.Ordinal));
        return Task.FromResult<IReadOnlyList<LoreEntry>>(entries);
    }

    private static LoreEntry ToEntry(LoreMarkdownDocument document, string sourcePath)
    {
        if (document.Essential)
        {
            if (string.IsNullOrWhiteSpace(document.ID))
            {
                throw new InvalidOperationException($"Essential lore page '{sourcePath}' requires a non-empty 'id' frontmatter field.");
            }

            if (string.IsNullOrWhiteSpace(document.Title))
            {
                throw new InvalidOperationException($"Essential lore page '{sourcePath}' requires a non-empty 'title' frontmatter field.");
            }
        }

        return new LoreEntry(document.ID, document.Title, document.Body.Trim(), sourcePath);
    }

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

    private static LoreMarkdownDocument ReadDocument(string path)
    {
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read);
        if (file is null)
        {
            Error error = Godot.FileAccess.GetOpenError();
            throw new InvalidOperationException($"Could not read lore file '{path}'. Godot FileAccess error: {error}.");
        }

        return ParseDocument(file.GetAsText(), path);
    }

    internal static LoreMarkdownDocument ParseDocument(string markdown, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        string normalised = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!normalised.StartsWith("---\n", StringComparison.Ordinal))
        {
            return new LoreMarkdownDocument(string.Empty, string.Empty, Essential: false, normalised.Trim());
        }

        int end = normalised.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new InvalidOperationException($"Lore page '{sourcePath}' has an unterminated frontmatter block.");
        }

        Dictionary<string, string> frontmatter = ParseFrontmatter(normalised[4..end]);
        string body = normalised[(end + "\n---\n".Length)..];
        _ = frontmatter.TryGetValue("id", out string? id);
        _ = frontmatter.TryGetValue("title", out string? title);

        bool essential = ParseEssential(frontmatter, sourcePath);
        if (essential)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException($"Essential lore page '{sourcePath}' requires a non-empty 'id' frontmatter field.");
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                throw new InvalidOperationException($"Essential lore page '{sourcePath}' requires a non-empty 'title' frontmatter field.");
            }
        }

        return new LoreMarkdownDocument(id?.Trim() ?? string.Empty, title?.Trim() ?? string.Empty, essential, body.Trim());
    }

    private static bool ParseEssential(IReadOnlyDictionary<string, string> frontmatter, string sourcePath)
    {
        bool essential = false;
        return !frontmatter.TryGetValue("essential", out string? essentialValue) || bool.TryParse(essentialValue, out essential)
            ? essential
            : throw new InvalidOperationException(
                $"Lore page '{sourcePath}' has invalid 'essential' frontmatter value '{essentialValue}'. Expected boolean 'true' or 'false'.");
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

    internal sealed record LoreMarkdownDocument(string ID, string Title, bool Essential, string Body);
}
