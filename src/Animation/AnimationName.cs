using System.Text.RegularExpressions;
using AlleyCat.Common;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Animation;

public readonly partial record struct AnimationName
{
    [GeneratedRegex(@"^[a-zA-Z]+[a-zA-Z0-9 _-]*$")]
    private static partial Regex NamePattern();

    public string Value { get; }

    private AnimationName(string value)
    {
        Value = value;
    }

    public static implicit operator string(AnimationName id) => id.Value;

    public static implicit operator StringName(AnimationName id) => id.Value;

    public static Either<ParseError, AnimationName> Create(string? value) =>
        Optional(value)
            .Filter(x => !string.IsNullOrWhiteSpace(x))
            .ToEither(new ParseError("Animation name cannot be null or empty."))
            .Bind<AnimationName>(x => NamePattern().IsMatch(x)
                ? Right(new AnimationName(x))
                : Left(new ParseError($"Invalid animation name: \"{value}\"."))
            );

    public override string ToString() => Value;
}