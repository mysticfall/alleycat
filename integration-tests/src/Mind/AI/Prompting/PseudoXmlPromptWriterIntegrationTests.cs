using System.Reflection;
using AlleyCat.Core;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Godot;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot runtime coverage for pseudo-XML prompt writer formatting and service registration.
/// </summary>
[Headless]
public sealed partial class PseudoXmlPromptWriterIntegrationTests
{
    /// <summary>
    /// Sections are emitted in order with one blank line between pseudo-XML blocks.
    /// </summary>
    [Fact]
    public async Task FormatWritesSectionsWithBlankLineBetweenEntries()
    {
        string result = await PseudoXmlPromptWriter.FormatAsync(
        [
            new TextPromptSection { Name = "System Instructions", Text = "Be kind." },
            new TextPromptSection { Name = "Player Context", Text = "Player is nearby." },
        ], CreateBuildContext());

        Assert.Equal(
            "<System Instructions>\n" +
            "Be kind.\n" +
            "</System Instructions>\n" +
            "\n" +
            "<Player Context>\n" +
            "Player is nearby.\n" +
            "</Player Context>\n" +
            "\n",
            result);
    }

    /// <summary>
    /// Section content is preserved while authored names are preserved in prompt tags.
    /// </summary>
    [Fact]
    public async Task FormatPreservesContentPaddingAndAuthoredSectionNames()
    {
        string result = await PseudoXmlPromptWriter.FormatAsync(
        [
            new TextPromptSection { Name = "  NPCVoiceID  ", Text = "  keep padding  \n" },
        ], CreateBuildContext());

        Assert.Equal("<  NPCVoiceID  >\n  keep padding  \n</  NPCVoiceID  >\n\n", result);
    }

    /// <summary>
    /// Tag names preserve lax authored punctuation and replace only tag delimiter characters.
    /// </summary>
    [Fact]
    public async Task FormatWritesLaxSectionNamesAsPseudoXmlTags()
    {
        (string SectionName, string ExpectedTagName)[] cases =
        [
            ("Test Fixture", "Test Fixture"),
            ("HTTP Response", "HTTP Response"),
            ("NPCVoiceID", "NPCVoiceID"),
            ("already_snake_case", "already_snake_case"),
            ("Player   Context", "Player   Context"),
            ("Line\tBreak", "Line\tBreak"),
            ("agent.APIResponse", "agent.APIResponse"),
            ("---", "---"),
            ("Faction/Rank <Elite>", "Faction_Rank _Elite_"),
        ];

        foreach ((string sectionName, string expectedTagName) in cases)
        {
            string result = await PseudoXmlPromptWriter.FormatAsync(
                [new TextPromptSection { Name = sectionName, Text = "content" }],
                CreateBuildContext());

            Assert.Equal($"<{expectedTagName}>\ncontent\n</{expectedTagName}>\n\n", result);
        }
    }

    /// <summary>
    /// Prompt content is not escaped, including slash characters that are special in tag names.
    /// </summary>
    [Fact]
    public async Task FormatPreservesSlashCharactersInContent()
    {
        string result = await PseudoXmlPromptWriter.FormatAsync(
        [
            new TextPromptSection { Name = "Test Fixture", Text = "Line including / here" },
        ], CreateBuildContext());

        Assert.Equal("<Test Fixture>\nLine including / here\n</Test Fixture>\n\n", result);
    }

    /// <summary>
    /// Invalid authored section entries fail with clear exceptions.
    /// </summary>
    [Fact]
    public async Task FormatRejectsNullEntriesAndInvalidNamesClearly()
    {
        InvalidOperationException nullException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PseudoXmlPromptWriter.FormatAsync([null!], CreateBuildContext()));
        Assert.Contains("null section", nullException.Message, StringComparison.OrdinalIgnoreCase);

        foreach (string sectionName in new[] { string.Empty, "   " })
        {
            InvalidOperationException nameException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PseudoXmlPromptWriter.FormatAsync(
                    [new TextPromptSection { Name = sectionName, Text = "content" }],
                    CreateBuildContext()));
            Assert.Contains("section", nameException.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static PromptSectionBuildContext CreateBuildContext()
        => new(new ServiceCollection().BuildServiceProvider(), new SceneContext([]));

    /// <summary>
    /// The default prompt writer is an authorable Godot resource and service registrar.
    /// </summary>
    [Fact]
    public void PseudoXmlPromptWriterIsGodotAuthorableServiceRegistrar()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(PseudoXmlPromptWriter)));
        Assert.NotNull(typeof(PseudoXmlPromptWriter).GetCustomAttribute<GlobalClassAttribute>());
        Assert.NotNull(typeof(PseudoXmlPromptWriter).GetCustomAttribute<ToolAttribute>());
        Assert.True(typeof(IServiceRegistrar).IsAssignableFrom(typeof(PseudoXmlPromptWriter)));
        Assert.Contains(typeof(IPromptWriter), typeof(PseudoXmlPromptWriter).GetInterfaces());
    }

    /// <summary>
    /// RegisterServices exposes the writer through the IPromptWriter service contract.
    /// </summary>
    [Fact]
    public void RegisterServicesRegistersSelfAsPromptWriter()
    {
        PseudoXmlPromptWriter writer = new();
        ServiceCollection services = new();

        writer.RegisterServices(services);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(writer, provider.GetRequiredService<IPromptWriter>());
    }
}
