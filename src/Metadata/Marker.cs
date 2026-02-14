using AlleyCat.Common;
using AlleyCat.Transform;
using Godot;
using LanguageExt;
using static LanguageExt.Prelude;

namespace AlleyCat.Metadata;

public readonly record struct MarkerId
{
    public string Value { get; }

    private MarkerId(string value)
    {
        Value = value;
    }

    public static implicit operator string(MarkerId id) => id.Value;

    public static Either<ParseError, MarkerId> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Marker ID cannot be null or empty."))
            .Map(v => new MarkerId(v));

    public override string ToString() => Value;
}

public interface IMarker : ILocatable3d, ITagged
{
    MarkerId Id { get; }
}

public interface IMarked
{
    Seq<IMarker> Markers { get; }
}

public readonly record struct Marker(
    MarkerId Id,
    Set<Tag> Tags,
    IO<Transform3D> GlobalTransform
) : IMarker;