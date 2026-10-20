using AlleyCat.Templating;
using Xunit;

namespace AlleyCat.Tests.Templating;

/// <summary>Literal content round-trips through the production Fluid engine without evaluating authored syntax.</summary>
public sealed class FluidTemplateEscapingTests
{
    /// <summary>Adversarial template syntax survives the production compiler and renderer exactly.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{{ character.FullId }} {{ character.Id }}")]
    [InlineData("{% if broken {{ never closed")]
    [InlineData("{% endraw %}{%- assign x = 1 -%}{{%{{{{%%")]
    [InlineData("{{ \"{{\" }} and {{ \"{%\" }}")]
    [InlineData("\r\n <>&\"é — {{- 7 -}} \t\n{")]
    public async Task EscapeOpeners_RendersOriginalTextExactly(string content)
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile(FluidTemplateEscaping.EscapeOpeners(content));

        string rendered = await template.RenderAsync(new Dictionary<string, object?>());

        Assert.Equal(content, rendered);
    }

    /// <summary>Exhaustive short brace sequences have distinct escapes and round-trip without parser errors.</summary>
    [Fact]
    public async Task EscapeOpeners_RoundTripsEveryShortBraceSequence()
    {
        FluidTemplateCompilerEngine compiler = new();
        HashSet<string> escapedOutputs = new(StringComparer.Ordinal);
        string[] alphabet = ["{", "}", "%", "-", "\""];
        List<string> inputs = [string.Empty];
        List<string> frontier = [string.Empty];
        for (int length = 1; length <= 4; length++)
        {
            frontier = [.. frontier.SelectMany(prefix => alphabet.Select(character => prefix + character))];
            inputs.AddRange(frontier);
        }

        foreach (string input in inputs)
        {
            string escaped = FluidTemplateEscaping.EscapeOpeners(input);
            Assert.True(escapedOutputs.Add(escaped), $"Escape collision for '{input}'.");
            string rendered = await compiler.Compile(escaped).RenderAsync(new Dictionary<string, object?>());
            Assert.Equal(input, rendered);
        }
    }
}
