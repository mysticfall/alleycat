using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Common;

public readonly record struct FrameRate
{
    public float Value { get; }

    private FrameRate(float value)
    {
        Value = value;
    }

    public static implicit operator float(FrameRate value) => value.Value;

    public static Either<ParseError, FrameRate> Create(float? value) =>
        Optional(value)
            .ToEither(new ParseError("Framerate cannot be null."))
            .Bind(v => v >= 0 ? Right<ParseError, float>(v) : Left(new ParseError("Framerate must be non-negative.")))
            .Map(v => new FrameRate(v));

    public override string ToString() => $"{Value} frames/s";
}