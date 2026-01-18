using System.Text.RegularExpressions;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Animation;

public readonly partial record struct AnimationParam
{
    [GeneratedRegex(@"^parameters(?:/[a-zA-Z0-9 _-]+)+$")]
    private static partial Regex ParamPattern();

    public string Value { get; }

    private AnimationParam(string value)
    {
        Value = value;
    }

    public static implicit operator string(AnimationParam id) => id.Value;

    public static implicit operator StringName(AnimationParam id) => id.Value;

    public static Either<ParseError, AnimationParam> Create(string? value) =>
        Optional(value)
            .Filter(x => !string.IsNullOrWhiteSpace(x))
            .ToEither(new ParseError("Animation parameter cannot be null or empty."))
            .Bind<AnimationParam>(x => ParamPattern().IsMatch(x)
                ? Right(new AnimationParam(x))
                : Left(new ParseError($"Invalid animation parameter: \"{value}\"."))
            );

    public override string ToString() => Value;
}