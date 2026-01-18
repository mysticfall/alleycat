using System.Collections;
using AlleyCat.Common;
using AlleyCat.Env;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Lore;

public readonly record struct LoreId : IComparable<LoreId>
{
    public string Value { get; }

    private LoreId(string value)
    {
        Value = value;
    }

    public static implicit operator string(LoreId id) => id.Value;

    public static Either<ParseError, LoreId> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Lore ID cannot be null or empty."))
            .Map(v => new LoreId(v));

    public override string ToString() => Value;

    public int CompareTo(LoreId other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);
}

public readonly record struct LoreTitle
{
    public string Value { get; }

    private LoreTitle(string value)
    {
        Value = value;
    }

    public static Either<ParseError, LoreTitle> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Lore title cannot be null or empty."))
            .Map(v => new LoreTitle(v));

    public static implicit operator string(LoreTitle title) => title.Value;

    public override string ToString() => Value;
}

public readonly record struct LoreText
{
    public string Value { get; }

    private LoreText(string value)
    {
        Value = value;
    }

    public static Either<ParseError, LoreText> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("LoreText cannot be null or empty."))
            .Map(v => new LoreText(v.Trim()));

    public static implicit operator string(LoreText text) => text.Value;

    public override string ToString() => Value;
}

public readonly record struct LoreEntry(
    LoreId Id,
    LoreTitle Title,
    int Depth = 1,
    Option<LoreId> Parent = default,
    Seq<LoreEntry> Children = default,
    bool Essential = false
) : IEnumerable<LoreEntry>
{
    public Option<LoreEntry> Filter(Seq<LorePath> paths) =>
        Optional(this)
            .Filter(x => paths.Exists(p => p.Matches(x.Id)))
            .Map(x =>
                x with
                {
                    Children = x.Children.Bind(c => c.Filter(paths).ToSeq())
                }
            );

    public IEnumerator<LoreEntry> GetEnumerator()
    {
        yield return this;

        foreach (var child in Children)
        {
            foreach (var descendant in child)
            {
                yield return descendant;
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
};

public readonly record struct LorePath(Seq<LoreId> Segments)
{
    public bool IsEmpty => Segments.IsEmpty;

    public bool Matches(LoreId id) => Segments.Contains<LoreId>(id);

    public override string ToString() => string.Join("/", Segments);
}

public interface ILoreBook
{
    Seq<LoreEntry> TableOfContents { get; }

    Eff<IEnv, LoreText> GetContents(params Seq<LoreId> ids);
}