using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Scene;
using AlleyCat.TestFramework;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task GetContentReadsTextFromGodotResourcePath()
    {
        FilePromptSection section = new()
        {
            FilePath = "res://assets/testing/prompts/test_prompt_api.md",
        };

        string content = await section.GetContentAsync(CreateBuildContext());

        Assert.Equal("File-backed prompt content for AI-003.\n", content);
    }

    /// <summary>
    /// Missing file prompt paths fail with a path-specific error.
    /// </summary>
    [Fact]
    public async Task GetContentFailsClearlyWhenPathCannotBeRead()
    {
        FilePromptSection section = new()
        {
            FilePath = "res://assets/testing/prompts/missing_prompt_api.md",
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => section.GetContentAsync(CreateBuildContext()));

        Assert.Contains("res://assets/testing/prompts/missing_prompt_api.md", exception.Message, StringComparison.Ordinal);
    }

    private static PromptSectionBuildContext CreateBuildContext()
        => new(new ServiceCollection().BuildServiceProvider(), new SceneContext([]));
}
