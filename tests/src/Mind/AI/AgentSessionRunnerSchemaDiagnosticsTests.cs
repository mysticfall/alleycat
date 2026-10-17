using System.Text.Json;
using AlleyCat.Mind.AI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Tests the argument-schema mismatch diagnostics walk (AI-011 TR-20/TR-21): matching values produce no
/// description, and every mismatch identifies the failing property path and the expected type or constraint from
/// the schema alone — never the argument value.
/// </summary>
public sealed class AgentSessionRunnerSchemaDiagnosticsTests
{
    /// <summary>
    /// Gets parsed-value and schema pairs with the description a mismatch must carry, or null when the value must
    /// match.
    /// </summary>
    public static TheoryData<string, string, string?> SchemaWalkCases
    {
        get;
    } = new()
    {
        {
            /*lang=json,strict*/
                                 "{\"speech\": \"hi\"}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"speech\": {\"type\": \"string\"}}}",
            null
        },
        {
            /*lang=json,strict*/
                                 "{\"speech\": 123}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"speech\": {\"type\": \"string\"}}}",
            "arguments.speech: expected type 'string' but the parsed value is of kind 'Number'"
        },
        {
            /*lang=json,strict*/
                                 "{\"count\": 1.5}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"count\": {\"type\": \"integer\"}}}",
            "arguments.count: expected type 'integer' but the parsed number is not an integer"
        },
        {
            /*lang=json,strict*/
                                 "{\"mode\": 42}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"mode\": {\"type\": [\"string\", \"null\"]}}}",
            "arguments.mode: expected one of the types 'string', 'null' but the parsed value is of kind 'Number'"
        },
        {
            /*lang=json,strict*/
                                 "{\"mode\": null}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"mode\": {\"type\": [\"string\", \"null\"]}}}",
            null
        },
        {
            /*lang=json,strict*/
                                 "{\"mode\": \"silent\"}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"mode\": {\"enum\": [\"quiet\", \"loud\"]}}}",
            "arguments.mode: expected one of the enumerated constants 'quiet', 'loud'"
        },
        {
            /*lang=json,strict*/
                                 "{\"mode\": \"loud\"}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"mode\": {\"enum\": [\"quiet\", \"loud\"]}}}",
            null
        },
        {
            "{}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"required\": [\"speech\"], \"properties\": {\"speech\": {\"type\": \"string\"}}}",
            "arguments: the required property 'speech' is missing"
        },
        {
            /*lang=json,strict*/
                                 "{\"options\": {\"volume\": \"high\"}}",
            "{\"type\": \"object\", \"properties\": {\"options\": {\"type\": \"object\", \"properties\": "
                + "{\"volume\": {\"type\": \"number\"}}}}}",
            "arguments.options.volume: expected type 'number' but the parsed value is of kind 'String'"
        },
        {
            /*lang=json,strict*/
                                 "{\"tags\": [\"ok\", 42]}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {\"tags\": {\"type\": \"array\", \"items\": {\"type\": \"string\"}}}}",
            "arguments.tags[1]: expected type 'string' but the parsed value is of kind 'Number'"
        },
        {
            /*lang=json,strict*/
                                 "{\"surprise\": 1}",
            /*lang=json,strict*/
                                 "{\"type\": \"object\", \"properties\": {}, \"additionalProperties\": false}",
            "arguments.surprise: the property is not declared by the schema and additional properties are disallowed"
        },
        { /*lang=json,strict*/ "{\"anything\": 1}", "true", null },
        { /*lang=json,strict*/ "{\"anything\": 1}", "false", "arguments: the schema permits no value" },
        {
            /*lang=json,strict*/
                                 "{\"anything\": 1}",
            "\"not an object schema\"",
            "arguments: the schema is not a JSON object schema"
        },
    };

    /// <summary>Verifies each schema-walk case produces exactly the expected path-and-constraint description.</summary>
    [Theory]
    [MemberData(nameof(SchemaWalkCases))]
    public void TryDescribeSchemaMismatch_IdentifiesThePathAndExpectedConstraint(
        string valueJson,
        string schemaJson,
        string? expectedDescription)
    {
        string? actual = AgentSessionRunner.TryDescribeSchemaMismatch(
            Parse(valueJson),
            Parse(schemaJson),
            AgentSessionRunner.ArgumentsPathRoot);

        Assert.Equal(expectedDescription, actual);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
