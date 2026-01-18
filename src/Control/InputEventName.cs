using AlleyCat.Common;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Control;

public readonly record struct InputEventName
{
    public string Value { get; }

    private InputEventName(string value)
    {
        Value = value;
    }

    public static implicit operator string(InputEventName name) => name.Value;

    public override string ToString() => Value;

    public static Either<ParseError, InputEventName> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Input event name cannot be null or empty."))
            .Map(v => new InputEventName(v));
}