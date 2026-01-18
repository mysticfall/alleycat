using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Schema;
using AlleyCat.Common;
using LanguageExt;
using LanguageExt.Common;
using static LanguageExt.Prelude;

namespace AlleyCat.Ai.Agent;

public readonly record struct AgentResponse(
    [Description("The delay in seconds before your next update. " +
                 "Choose a duration based on when you expect new events " +
                 "(e.g., a character's response or an environmental change).")]
    float NextUpdate,
    [Description("Brief internal reasoning or reminders to maintain " +
                 "character consistency. Keep this concise and focused on " +
                 "planning your next actions.")]
    string? Thoughts
)
{
    public static JsonElement GenerateSchema()
    {
        var options = JsonSerializerOptions.Default;
        var schemaNode = options.GetJsonSchemaAsNode(typeof(AgentResponse), new JsonSchemaExporterOptions
        {
            TransformSchemaNode = (context, node) =>
            {
                var description = context.PropertyInfo?.AttributeProvider?
                    .GetCustomAttributes(typeof(DescriptionAttribute), false)
                    .OfType<DescriptionAttribute>()
                    .FirstOrDefault()?
                    .Description;

                if (description != null)
                {
                    node["description"] = description;
                }

                return node;
            }
        });

        return JsonSerializer.Deserialize<JsonElement>(schemaNode.ToJsonString());
    }

    public static Eff<AgentResponse> Parse(string text) =>
        liftEff(() => JsonSerializer.Deserialize<AgentResponse>(text.ExtractJson()))
            .Bind(x => Optional(x).ToEff())
            .MapFail(_ => Error.New($"Failed to parse assistant JSON response: {text}"));
}