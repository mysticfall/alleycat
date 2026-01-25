using AlleyCat.Io;
using LanguageExt;
using Markdig.Syntax;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Lore.Markdown;

public readonly record struct MarkdownLoreSource(
    ResourcePath Path,
    LoreId Id,
    LoreTitle Title,
    int Order,
    bool Essential,
    MarkdownDocument Document
) : IComparable<MarkdownLoreSource>
{
    public bool IsIndex => Path.Value.EndsWith("/index.md");

    public ResourcePath EntryPath
    {
        get
        {
            var path = Path;

            return IsIndex
                ? Path.Parent.IfNone(() =>
                    throw new InvalidOperationException($"Parent path not found for {path}")
                )
                : Path;
        }
    }

    public int CompareTo(MarkdownLoreSource other)
    {
        var e1 = EntryPath;
        var e2 = other.EntryPath;

        var p1 = e1.Parent.Map(x => x.Path).IfNone("").Split("/");
        var p2 = e2.Parent.Map(x => x.Path).IfNone("").Split("/");

        if (p1.SequenceEqual(p2) && Order != other.Order)
        {
            return Order.CompareTo(other.Order);
        }

        if (p1.StartsWith(p2) && p1.Length > p2.Length)
        {
            return 1;
        }

        if (p2.StartsWith(p1) && p1.Length < p2.Length)
        {
            return -1;
        }

        return e1.Value.CompareTo(e2.Value, StringComparison.InvariantCulture);
    }
}

public static class MarkdownLoreSourceExtensions
{
    public static Seq<LoreEntry> ToTableOfContents(this IEnumerable<MarkdownLoreSource> sources)
    {
        Map<string, MarkdownLoreSource> indexes = default;
        Map<string, Seq<MarkdownLoreSource>> children = default;

        var sourcesInOrder = sources.Order();

        foreach (var source in sourcesInOrder)
        {
            var entryPath = source.EntryPath;
            var parentPath = GetParentPath(source);

            if (source.IsIndex)
            {
                indexes = indexes.Add(entryPath.Path, source);
            }

            var siblings = children.GetValueOrDefault(parentPath);

            children = children.AddOrUpdate(parentPath, siblings.Add(source));
        }

        var (_, roots) = indexes
            .Fold((Depth: int.MaxValue, Children: Set<MarkdownLoreSource>()), (acc, s) =>
            {
                var p = s.EntryPath;
                var depth = p.Segments.Count;

                if (depth > acc.Depth)
                {
                    return acc;
                }

                if (depth < acc.Depth)
                {
                    return (depth, Set(s));
                }

                return (depth, acc.Children.AddOrUpdate(s));
            });

        return roots
            .Order()
            .AsIterable()
            .ToSeq()
            .Map(r => ToEntry(r));

        string GetParentPath(MarkdownLoreSource source) =>
            source.EntryPath.Parent.Match(p => p.Path, () => "");

        LoreEntry ToEntry(
            MarkdownLoreSource source,
            int depth = 1,
            Option<LoreId> parent = default
        ) =>
            new(
                source.Id,
                source.Title,
                depth,
                parent,
                children
                    .Find(source.EntryPath.Path)
                    .ToSeq()
                    .Flatten()
                    .Map(child => ToEntry(child, depth + 1, source.Id)),
                source.Essential
            );
    }
}