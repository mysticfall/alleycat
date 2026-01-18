using AlleyCat.Common;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai;

public readonly record struct PromptText
{
    public string Value { get; }

    private PromptText(string value)
    {
        Value = value;
    }

    public static implicit operator string(PromptText text) => text.Value;

    public override string ToString() => Value;

    public static Either<ParseError, PromptText> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Prompt cannot be null or empty."))
            .Map(v => new PromptText(v));
}