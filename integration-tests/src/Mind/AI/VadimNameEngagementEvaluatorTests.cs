using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>Offline checks of the fixed experiment's fail-closed judgement boundary.</summary>
[Headless]
public sealed class VadimNameEngagementEvaluatorTests
{
    /// <summary>Malformed, unsupported, and incomplete categorical replies never produce a passing metric.</summary>
    [Fact]
    public void Parse_AcceptsOnlyKnownCategoriesAndNonEmptyReasons()
    {
        foreach (string category in new[] { "grounded-name-engagement", "generic-response", "unanswered" })
        {
            Assert.Equal(category, VadimNameEngagementEvaluator.Parse(
                $$"""{"category":"{{category}}","reason":"Recorded words."}""")?.Category);
        }

        foreach (string invalid in new[]
        {
            "not json",
            "[]",
            "{}",
            /*lang=json,strict*/
                                 "{\"category\":\"invalid\",\"reason\":\"Cannot tell.\"}",
            /*lang=json,strict*/
                                 "{\"category\":\"grounded-name-engagement\",\"reason\":\" \"}",
            /*lang=json,strict*/
                                 "{\"category\":\"unknown\",\"reason\":\"Words.\"}",
            /*lang=json,strict*/
                                 "{\"category\":\"unanswered\",\"reason\":42}",
            /*lang=json,strict*/
                                 "{\"category\":\"unanswered\",\"reason\":\"Words.\",\"extra\":true}",
        })
        {
            Assert.Null(VadimNameEngagementEvaluator.Parse(invalid));
        }
    }

    /// <summary>Judge settlement evidence omits model identity, target-facing instructions and private reasoning.</summary>
    [Fact]
    public void BuildSettlement_ExposesOnlyOutcomeAndExecutedTools()
    {
        const string text = """
            # Turn
            - **Model:** hidden-model
            - **Outcome:** Accepted
            ## Instructions
            Hidden target criteria
            ## Response
            Hidden private thought
            ## Tool Invocations
            ### speak
            Spoken through the configured voice.
            """;
        string evidence = VadimNameEngagementEvaluator.BuildSettlement(text);
        Assert.StartsWith("- **Outcome:** Accepted", evidence, StringComparison.Ordinal);
        Assert.Contains("Spoken through the configured voice.", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden", evidence, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Hidden", evidence, StringComparison.Ordinal);
    }
}
