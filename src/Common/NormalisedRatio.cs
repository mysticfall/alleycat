using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Common;

public readonly record struct NormalisedRatio
{
    public float Value { get; }

    private NormalisedRatio(float value)
    {
        Value = value;
    }

    public static implicit operator float(NormalisedRatio value) => value.Value;

    public static Either<ParseError, NormalisedRatio> Create(float? value) =>
        Optional(value)
            .ToEither(new ParseError("Ratio cannot be null."))
            .Bind(v => v is >= 0 and <= 1
                ? Right<ParseError, float>(v)
                : Left(new ParseError("Ratio between 0 and 1 (inclusive).")))
            .Map(v => new NormalisedRatio(v));

    public static NormalisedRatio Coerce(float value) => new(float.Clamp(value, 0, 1));

    public override string ToString() => $"{Value:F3}";
}