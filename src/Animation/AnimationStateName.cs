using System.Text.RegularExpressions;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Animation;

public readonly partial record struct AnimationStateName
{
    [GeneratedRegex(@"^[a-zA-Z]+[a-zA-Z0-9 _-]*$")]
    private static partial Regex NamePattern();

    public string Value { get; }

    private AnimationStateName(string value)
    {
        Value = value;
    }

    public static implicit operator string(AnimationStateName id) => id.Value;

    public static implicit operator StringName(AnimationStateName id) => id.Value;

    public static Either<ParseError, AnimationStateName> Create(string? value) =>
        Optional(value)
            .Filter(x => !string.IsNullOrWhiteSpace(x))
            .ToEither(new ParseError("Animation state name cannot be null or empty."))
            .Bind<AnimationStateName>(x => NamePattern().IsMatch(x)
                ? Right(new AnimationStateName(x))
                : Left(new ParseError($"Invalid animation state name: \"{value}\"."))
            );

    public override string ToString() => Value;
}