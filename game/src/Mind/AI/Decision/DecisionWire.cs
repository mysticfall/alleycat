using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace AlleyCat.Mind.AI.Decision;

internal static class DecisionWire
{
    // Single symmetric allowance for every supplied distribution sum and the weighted-score consistency check
    // (AI-014 TR-6): the value may deviate in either direction by this amount per supplied option/level.
    private const double SumAllowancePerOption = 0.005;
    // Float epsilon added to the allowance so exact-boundary sums survive accumulation error.
    private const double SumAllowanceEpsilon = 1e-9;

    /// <summary>OpenRouter and SystemOne share the map-based wire shapes; only OpenAI uses named arrays.</summary>
    private static bool MapsByName(DecisionAPIKind kind) => kind != DecisionAPIKind.OpenAI;

    internal static DecisionRequest Snapshot(DecisionRequest request)
    {
        if (request is null || request.Questions is null || request.Questions.Count == 0)
        {
            throw new DecisionException(DecisionFailure.Input, "Decision questions are required.");
        }

        object evidence = request.Evidence switch
        {
            string text => text,
            JsonElement json when json.ValueKind is JsonValueKind.Object or JsonValueKind.Array => json.Clone(),
            _ => throw new DecisionException(DecisionFailure.Unsupported, "Decision evidence must be text or a JSON object/array.")
        };
        HashSet<string> names = new(StringComparer.Ordinal);
        List<DecisionQuestion> questions = [];
        foreach (DecisionQuestion question in request.Questions)
        {
            if (question is null || string.IsNullOrWhiteSpace(question.Name) ||
                string.IsNullOrWhiteSpace(question.Instructions) || !names.Add(question.Name))
            {
                throw new DecisionException(DecisionFailure.Input, "Decision question names must be unique and instructions nonblank.");
            }

            switch (question)
            {
                case BooleanQuestion boolean:
                    questions.Add(boolean);
                    break;
                case ChoiceQuestion choice:
                    if (choice.Options is null || choice.Options.Count == 0 || choice.Options.Any(option =>
                            option is null || string.IsNullOrEmpty(option.Value) || string.IsNullOrWhiteSpace(option.Criteria)) ||
                        choice.Options.Select(option => option.Value).Distinct(StringComparer.Ordinal).Count() != choice.Options.Count)
                    {
                        throw new DecisionException(DecisionFailure.Input, "Decision choices need distinct nonempty values and textual criteria.");
                    }

                    questions.Add(choice with
                    {
                        Options = Array.AsReadOnly(choice.Options.ToArray())
                    });
                    break;
                case ScoreQuestion score:
                    if (score.Levels is null || score.Levels.Count is < 2 or > 10 || score.Levels.Any(string.IsNullOrWhiteSpace))
                    {
                        throw new DecisionException(DecisionFailure.Input, "Decision scores require 2–10 textual levels.");
                    }

                    questions.Add(score with
                    {
                        Levels = Array.AsReadOnly(score.Levels.ToArray())
                    });
                    break;
                default:
                    throw new DecisionException(DecisionFailure.Unsupported, "Unsupported decision question kind.");
            }
        }
        return new DecisionRequest(evidence, questions.AsReadOnly());
    }

    internal static byte[] Write(DecisionRequest request, DecisionAPIKind kind, string model)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WritePropertyName(MapsByName(kind) ? "state" : "input");
            if (request.Evidence is string text)
            {
                writer.WriteStringValue(text);
            }
            else if (MapsByName(kind))
            {
                ((JsonElement)request.Evidence).WriteTo(writer);
            }
            else
            {
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteStartArray("content");
                writer.WriteStartObject();
                writer.WriteString("type", "input_json");
                writer.WritePropertyName("data");
                ((JsonElement)request.Evidence).WriteTo(writer);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }
            writer.WritePropertyName("questions");
            if (MapsByName(kind))
            {
                writer.WriteStartObject();
            }
            else
            {
                writer.WriteStartArray();
            }

            foreach (DecisionQuestion question in request.Questions)
            {
                if (MapsByName(kind))
                {
                    writer.WritePropertyName(question.Name);
                }

                writer.WriteStartObject();
                if (kind == DecisionAPIKind.OpenAI)
                {
                    writer.WriteString("name", question.Name);
                }

                writer.WriteString("type", TypeName(question, kind));
                writer.WriteString("instructions", question.Instructions);
                if (question is ChoiceQuestion choice)
                {
                    if (MapsByName(kind))
                    {
                        writer.WriteStartObject("criteria");
                        foreach (DecisionOption option in choice.Options)
                        {
                            writer.WriteString(option.Value, option.Criteria);
                        }

                        writer.WriteEndObject();
                    }
                    else
                    {
                        writer.WriteStartArray("choices");
                        foreach (DecisionOption option in choice.Options)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("value", option.Value);
                            writer.WriteString("description", option.Criteria);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                    }
                }
                if (question is ScoreQuestion score)
                {
                    writer.WriteStartArray(MapsByName(kind) ? "criteria" : "levels");
                    foreach (string level in score.Levels)
                    {
                        if (MapsByName(kind))
                        {
                            writer.WriteStringValue(level);
                        }
                        else
                        {
                            writer.WriteStartObject();
                            writer.WriteString("label", level);
                            writer.WriteEndObject();
                        }
                    }
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            if (MapsByName(kind))
            {
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static string TypeName(DecisionQuestion question, DecisionAPIKind kind) => question switch
    {
        BooleanQuestion => kind == DecisionAPIKind.OpenAI ? "predicate" : "noul",
        ChoiceQuestion => "choice",
        ScoreQuestion => "score",
        _ => throw Invalid()
    };

    internal static DecisionResult Parse(byte[] body, DecisionRequest request, DecisionAPIKind kind)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false });
            JsonElement root = document.RootElement;
            string model = RequiredString(root, "model");
            if (string.IsNullOrWhiteSpace(model))
            {
                throw Invalid();
            }

            Dictionary<string, JsonElement> received = new(StringComparer.Ordinal);
            JsonElement answers = root.GetProperty("answers");
            if (MapsByName(kind))
            {
                foreach (JsonProperty item in answers.EnumerateObject())
                {
                    received.Add(item.Name, item.Value);
                }
            }
            else
            {
                foreach (JsonElement item in answers.EnumerateArray())
                {
                    if (!received.TryAdd(RequiredString(item, "name"), item))
                    {
                        throw Invalid();
                    }
                }
            }
            if (received.Count != request.Questions.Count)
            {
                throw Invalid();
            }

            List<DecisionAnswer> ordered = [];
            Dictionary<string, DecisionAnswer> keyed = new(StringComparer.Ordinal);
            foreach (DecisionQuestion question in request.Questions)
            {
                if (!received.TryGetValue(question.Name, out JsonElement item))
                {
                    throw Invalid();
                }

                DecisionAnswer answer = ReadAnswer(question, item, kind);
                ordered.Add(answer);
                keyed.Add(question.Name, answer);
            }
            DecisionUsage? usage = null;
            if (root.TryGetProperty("usage", out JsonElement usageJSON))
            {
                if (usageJSON.ValueKind != JsonValueKind.Object)
                {
                    throw Invalid();
                }

                usage = new DecisionUsage(Tokens(usageJSON, "input_tokens"), Tokens(usageJSON, "output_tokens"),
                    OptionalNumber(usageJSON, "cost", double.MaxValue), usageJSON.Clone());
            }
            return new DecisionResult(model, OptionalString(root, "id"), OptionalString(root, "provider"), usage,
                OptionalNumber(root, "cost", double.MaxValue) ?? usage?.Cost, ordered.AsReadOnly(),
                new ReadOnlyDictionary<string, DecisionAnswer>(keyed));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static DecisionAnswer ReadAnswer(DecisionQuestion question, JsonElement item, DecisionAPIKind kind)
    {
        string type = RequiredString(item, "type");
        if (kind == DecisionAPIKind.OpenAI && type == "refusal")
        {
            return new DecisionRefusal(question, item.Clone());
        }

        if (type != TypeName(question, kind))
        {
            throw Invalid();
        }

        double? confidence = OptionalNumber(item, "confidence", 1);
        switch (question)
        {
            case BooleanQuestion boolean:
                return new BooleanAnswer(boolean, Number(item.GetProperty(MapsByName(kind) ? "noul" : "probability"), 1), confidence);
            case ChoiceQuestion choice:
                {
                    string value = RequiredString(item, "choice");
                    string[] values = [.. choice.Options.Select(option => option.Value)];
                    if (!values.Contains(value, StringComparer.Ordinal))
                    {
                        throw Invalid();
                    }

                    double[]? probabilities = Distribution(item, values, null, kind);
                    IReadOnlyDictionary<string, double>? mapped = probabilities is null ? null :
                        new ReadOnlyDictionary<string, double>(values.Select((key, index) => (key, index))
                            .ToDictionary(pair => pair.key, pair => probabilities[pair.index], StringComparer.Ordinal));
                    return new ChoiceAnswer(choice, value, confidence, mapped);
                }
            case ScoreQuestion score:
                {
                    string[] indices = [.. Enumerable.Range(0, score.Levels.Count).Select(index => index.ToString(CultureInfo.InvariantCulture))];
                    double value = Number(item.GetProperty("score"), score.Levels.Count - 1);
                    if (item.TryGetProperty("legend", out JsonElement legend))
                    {
                        if (legend.EnumerateObject().Count() != indices.Length)
                        {
                            throw Invalid();
                        }

                        for (int i = 0; i < indices.Length; i++)
                        {
                            if (RequiredString(legend, indices[i]) != score.Levels[i])
                            {
                                throw Invalid();
                            }
                        }
                    }
                    double[]? probabilities = Distribution(item, indices, score.Levels, kind);
                    return probabilities is not null && Math.Abs(probabilities.Select((p, i) => p * i).Sum() - value) > SumAllowance(score.Levels.Count)
                        ? throw Invalid()
                        : (DecisionAnswer)new ScoreAnswer(score, value, confidence, probabilities is null ? null : Array.AsReadOnly(probabilities));
                }
            default:
                throw Invalid();
        }
    }

    private static double[]? Distribution(JsonElement item, string[] keys, IReadOnlyList<string>? levels, DecisionAPIKind kind)
    {
        if (!item.TryGetProperty("probabilities", out JsonElement distribution))
        {
            return null;
        }

        Dictionary<string, double> values = new(StringComparer.Ordinal);
        if (MapsByName(kind))
        {
            foreach (JsonProperty entry in distribution.EnumerateObject())
            {
                values.Add(entry.Name, Number(entry.Value, 1));
            }
        }
        else
        {
            foreach (JsonElement entry in distribution.EnumerateArray())
            {
                JsonElement rawValue = entry.GetProperty("value");
                string key = levels is null ? rawValue.GetString() ?? throw Invalid() :
                    rawValue.GetInt32().ToString(CultureInfo.InvariantCulture);
                if (!values.TryAdd(key, Number(entry.GetProperty("probability"), 1)))
                {
                    throw Invalid();
                }

                if (levels is not null && entry.TryGetProperty("label", out JsonElement label))
                {
                    int index = Array.IndexOf(keys, key);
                    if (index < 0 || label.GetString() != levels[index])
                    {
                        throw Invalid();
                    }
                }
            }
        }
        double sum = values.Values.Sum();
        return values.Count != keys.Length || keys.Any(key => !values.ContainsKey(key)) || Math.Abs(sum - 1) > SumAllowance(keys.Length)
            ? throw Invalid()
            : [.. keys.Select(key => values[key])];
    }

    // Total symmetric deviation allowed for a distribution of the supplied option/level count.
    private static double SumAllowance(int suppliedCount) => (SumAllowancePerOption * suppliedCount) + SumAllowanceEpsilon;

    private static string RequiredString(JsonElement item, string key) => item.GetProperty(key).GetString() ?? throw Invalid();
    private static string? OptionalString(JsonElement item, string key) => item.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
    private static double? OptionalNumber(JsonElement item, string key, double maximum) =>
        item.TryGetProperty(key, out JsonElement value) ? Number(value, maximum) : null;
    private static double Number(JsonElement value, double maximum)
    {
        double number = value.GetDouble();
        return double.IsFinite(number) && number >= 0 && number <= maximum ? number : throw Invalid();
    }
    private static long? Tokens(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out JsonElement value))
        {
            return null;
        }

        long count = value.GetInt64();
        return count >= 0 ? count : throw Invalid();
    }
    private static DecisionException Invalid() => new(DecisionFailure.InvalidResponse, "Invalid decision response.");
}
