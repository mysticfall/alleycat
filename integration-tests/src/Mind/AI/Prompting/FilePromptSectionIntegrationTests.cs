using AlleyCat.Mind.AI.Prompting;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

/// <summary>
/// Godot runtime coverage for AI-003 file-backed prompt sections.
/// </summary>
[Headless]
public sealed partial class FilePromptSectionIntegrationTests
{
    /// <summary>
    /// File prompt sections read text from Godot resource paths.
    /// </summary>
    [Fact]
    public void GetContentReadsTextFromGodotResourcePath()
    {
        FilePromptSection section = new()
        {
            FilePath = "res://assets/testing/prompts/test_prompt_api.md",
        };

        string content = section.GetContent();

        Assert.Equal("File-backed prompt content for AI-003.\n", content);
    }

    /// <summary>
    /// Missing file prompt paths fail with a path-specific error.
    /// </summary>
    [Fact]
    public void GetContentFailsClearlyWhenPathCannotBeRead()
    {
        FilePromptSection section = new()
        {
            FilePath = "res://assets/testing/prompts/missing_prompt_api.md",
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(section.GetContent);

        Assert.Contains("res://assets/testing/prompts/missing_prompt_api.md", exception.Message, StringComparison.Ordinal);
    }
}
