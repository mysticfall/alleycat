using System.Text.Json;
using System.Text.Json.Serialization;
using AlleyCat.Common;
using LanguageExt;
using LanguageExt.UnsafeValueAccess;
using static LanguageExt.Prelude;

namespace AlleyCat.Speech;

[JsonConverter(typeof(DialogueTextJsonConverter))]
public readonly record struct DialogueText
{
    public string Value { get; }

    private DialogueText(string value)
    {
        Value = value;
    }

    public Seq<DialogueText> Split() => Value
        .Split(['.', '!', '?'], StringSplitOptions.RemoveEmptyEntries)
        .AsIterable()
        .Map(s => s.Trim())
        .Filter(s => !string.IsNullOrWhiteSpace(s))
        .ToSeq()
        .Map(s => new DialogueText(s));

    public static implicit operator string(DialogueText text) => text.Value;

    public override string ToString() => Value;

    public static Either<ParseError, DialogueText> Create(string? value) =>
        Optional(value)
            .Filter(v => !string.IsNullOrWhiteSpace(v))
            .ToEither(new ParseError("Dialogue text cannot be null or empty."))
            .Map(v => new DialogueText(v));

    private class DialogueTextJsonConverter : JsonConverter<DialogueText>
    {
        public override DialogueText Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options
        )
        {
            var value = reader.GetString();

            return Create(value).ValueUnsafe();
        }

        public override void Write(
            Utf8JsonWriter writer,
            DialogueText value,
            JsonSerializerOptions options
        )
        {
            writer.WriteStringValue(value.Value);
        }
    }
}