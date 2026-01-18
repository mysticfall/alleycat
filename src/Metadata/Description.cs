using AlleyCat.Common;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

public readonly record struct Description
{
    public string Value { get; }

    private Description(string value)
    {
        Value = value;
    }

    public static Either<ParseError, Description> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Description cannot be null or empty."))
            .Map(v => new Description(v));

    public static Description operator +(Description left, Description right) =>
        new(Seq(left.Value, right.Value).ToFullString(" "));

    public static implicit operator string(Description description) => description.Value;

    public override string ToString() => Value;
}