using AlleyCat.AI.Prompting;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.AI.Prompting;

/// <summary>
/// Godot runtime coverage for AI-003 prompt stack source assembly and compiler delegation.
/// </summary>
[Headless]
public sealed partial class PromptStackIntegrationTests
{
    /// <summary>
    /// Inline sections return their configured text content.
    /// </summary>
    [Fact]
    public void TextPromptSectionReturnsConfiguredText()
    {
        TextPromptSection section = new()
        {
            Text = "Inline prompt text.",
        };

        Assert.Equal("Inline prompt text.", section.GetContent());
    }

    /// <summary>
    /// Prompt stacks concatenate sections in order and return the compiler result.
    /// </summary>
    [Fact]
    public void CompileConcatenatesSectionsInOrderAndReturnsCompilerResult()
    {
        RecordingTemplate expectedTemplate = new();
        RecordingCompiler compiler = new(expectedTemplate);
        PromptStack stack = new()
        {
            Sections =
            [
                new TextPromptSection
                {
                    Name = "System Instructions",
                    Text = "Be kind.",
                },
                new TextPromptSection
                {
                    Name = "Player Context",
                    Text = "Player is nearby.",
                },
            ],
        };

        ITemplate template = stack.Compile(compiler);

        Assert.Same(expectedTemplate, template);
        Assert.Equal(
            "<system_instructions>\n" +
            "Be kind.\n" +
            "</system_instructions>\n" +
            "\n" +
            "<player_context>\n" +
            "Player is nearby.\n" +
            "</player_context>",
            compiler.Source);
    }

    /// <summary>
    /// Prompt stacks trim the complete generated source before compilation.
    /// </summary>
    [Fact]
    public void CompileTrimsCompleteSourceBeforeCompilation()
    {
        RecordingCompiler compiler = new(new RecordingTemplate());
        PromptStack stack = new()
        {
            Sections =
            [
                new TextPromptSection
                {
                    Name = " Trimmed Section ",
                    Text = "  content with intentional padding  \n",
                },
            ],
        };

        _ = stack.Compile(compiler);

        Assert.Equal(
            "<trimmed_section>\n" +
            "  content with intentional padding  \n" +
            "</trimmed_section>",
            compiler.Source);
    }

    /// <summary>
    /// Section names are normalised to snake-case tag names with acronym-aware boundaries.
    /// </summary>
    [Fact]
    public void CompileNormalisesSectionNamesToSnakeCaseTags()
    {
        (string SectionName, string ExpectedTagName)[] cases =
        [
            ("HTTP Response", "http_response"),
            ("NPCVoiceID", "npc_voice_id"),
            ("already_snake_case", "already_snake_case"),
            ("Player   Context", "player_context"),
            ("Line\tBreak", "line_break"),
            ("Model2D Target", "model_2_d_target"),
            ("agent.APIResponse", "agent_api_response"),
        ];

        foreach ((string sectionName, string expectedTagName) in cases)
        {
            RecordingCompiler compiler = new(new RecordingTemplate());
            PromptStack stack = new()
            {
                Sections =
            [
                new TextPromptSection
                {
                    Name = sectionName,
                    Text = "content",
                },
            ],
            };

            _ = stack.Compile(compiler);

            Assert.Equal(
                $"<{expectedTagName}>\ncontent\n</{expectedTagName}>",
                compiler.Source);
        }
    }

    /// <summary>
    /// Null section arrays are treated as an empty authored stack.
    /// </summary>
    [Fact]
    public void CompileTreatsNullSectionsArrayAsEmptyPromptSource()
    {
        RecordingCompiler compiler = new(new RecordingTemplate());
        PromptStack stack = new()
        {
            Sections = null!,
        };

        _ = stack.Compile(compiler);

        Assert.Equal(string.Empty, compiler.Source);
    }

    /// <summary>
    /// Null entries inside the section array fail as clear authoring errors.
    /// </summary>
    [Fact]
    public void CompileRejectsNullSectionEntriesClearly()
    {
        PromptStack stack = new()
        {
            Sections = [null!],
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            stack.Compile(new RecordingCompiler(new RecordingTemplate())));

        Assert.Contains("null section", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Empty or punctuation-only section names fail clearly before compilation.
    /// </summary>
    [Fact]
    public void CompileRejectsEmptySectionNamesClearly()
    {
        string[] cases = [string.Empty, "   ", "---"];

        foreach (string sectionName in cases)
        {
            PromptStack stack = new()
            {
                Sections =
            [
                new TextPromptSection
                {
                    Name = sectionName,
                    Text = "content",
                },
            ],
            };

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
                stack.Compile(new RecordingCompiler(new RecordingTemplate())));

            Assert.Contains("section", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Compile requires a non-null template compiler.
    /// </summary>
    [Fact]
    public void CompileRequiresCompiler()
    {
        PromptStack stack = new();

        _ = Assert.Throws<ArgumentNullException>(() => stack.Compile(null!));
    }

    private sealed class RecordingCompiler(ITemplate template) : ITemplateCompiler
    {
        private readonly ITemplate _template = template;

        public string? Source
        {
            get; private set;
        }

        public ITemplate Compile(string source)
        {
            Source = source;
            return _template;
        }
    }

    private sealed class RecordingTemplate : ITemplate
    {
        public string Render(IReadOnlyDictionary<string, object?> context) => string.Empty;
    }
}
