using AlleyCat.Common;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

public readonly record struct Tag : IComparable
{
    public string Value { get; }

    private Tag(string value)
    {
        Value = value.ToLowerInvariant();
    }

    public static implicit operator string(Tag id) => id.Value;

    public static Either<ParseError, Tag> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Tag cannot be null or empty."))
            .Map(v => new Tag(v));

    public int CompareTo(object? obj)
    {
        return obj switch
        {
            null => 1,
            Tag other => string.Compare(Value, other.Value, StringComparison.Ordinal),
            string s => string.Compare(Value, s.ToLowerInvariant(), StringComparison.Ordinal),
            _ => throw new ArgumentException("Object is not a Tag or string.", nameof(obj))
        };
    }

    public override string ToString() => Value;
}

public interface ITagged
{
    Set<Tag> Tags { get; }

    bool Matches(Tag tag) => Tags.Contains(tag);

    bool MatchesAny(IEnumerable<Tag> tags) => tags.Any(Tags.Contains);

    bool MatchesAll(IEnumerable<Tag> tags) => tags.All(Tags.Contains);
}

public static class TagExtensions
{
    public static Either<ParseError, Set<Tag>> AsTags(this string[] tags) =>
        toSet(tags)
            .Map(x => x.Trim())
            .Traverse(Tag.Create)
            .As();
}