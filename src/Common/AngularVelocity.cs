using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Common;

public readonly record struct AngularVelocity
{
    public float Radians { get; }

    public float Degrees => float.RadiansToDegrees(Radians);

    private AngularVelocity(float radians)
    {
        Radians = radians;
    }

    public static Either<ParseError, AngularVelocity> FromRadians(float? value) =>
        Optional(value)
            .ToEither(new ParseError("Angular velocity cannot be null."))
            .Bind(v => v >= 0f
                ? Right<ParseError, float>(v)
                : Left(new ParseError("Angular velocity cannot be negative.")))
            .Map(v => new AngularVelocity(v));

    public static Either<ParseError, AngularVelocity> FromDegrees(float? value) =>
        FromRadians(value != null ? float.DegreesToRadians((float)value) : null);
}