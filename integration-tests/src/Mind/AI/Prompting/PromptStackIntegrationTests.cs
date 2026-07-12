using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Templating;
using AlleyCat.TestFramework;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlleyCat.IntegrationTests.Mind.AI.Prompting;

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
    /// Prompt stacks delegate source writing through services and return the compiler result.
    /// </summary>
    [Fact]
    public void CompileResolvesWriterAndCompilerFromServiceProvider()
    {
        RecordingTemplate expectedTemplate = new();
        RecordingCompiler compiler = new(expectedTemplate);
        RecordingPromptWriter writer = new("  compiled source  ");
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IPromptWriter>(writer)
            .AddSingleton<ITemplateCompiler>(compiler)
            .BuildServiceProvider();
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

        ITemplate template = stack.Compile(services);

        Assert.Same(expectedTemplate, template);
        Assert.Same(stack.Sections, writer.Sections);
        Assert.Equal("compiled source", compiler.Source);
    }

    /// <summary>
    /// Prompt stacks trim the complete generated source before compilation.
    /// </summary>
    [Fact]
    public void CompileTrimsCompleteSourceBeforeCompilation()
    {
        RecordingCompiler compiler = new(new RecordingTemplate());
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IPromptWriter>(new RecordingPromptWriter("\n  complete source  \n"))
            .AddSingleton<ITemplateCompiler>(compiler)
            .BuildServiceProvider();
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

        _ = stack.Compile(services);

        Assert.Equal("complete source", compiler.Source);
    }

    /// <summary>
    /// Null section arrays are treated as an empty authored stack.
    /// </summary>
    [Fact]
    public void CompileTreatsNullSectionsArrayAsEmptyPromptSource()
    {
        RecordingPromptWriter writer = new("source");
        RecordingCompiler compiler = new(new RecordingTemplate());
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IPromptWriter>(writer)
            .AddSingleton<ITemplateCompiler>(compiler)
            .BuildServiceProvider();
        PromptStack stack = new()
        {
            Sections = null!,
        };

        _ = stack.Compile(services);

        Assert.Empty(writer.Sections ?? throw new InvalidOperationException("Writer was not invoked."));
    }

    /// <summary>
    /// Compile requires a non-null service provider.
    /// </summary>
    [Fact]
    public void CompileRequiresServiceProvider()
    {
        PromptStack stack = new();

        _ = Assert.Throws<ArgumentNullException>(() => stack.Compile(null!));
    }

    /// <summary>
    /// Missing services fail through normal dependency injection behaviour.
    /// </summary>
    [Fact]
    public void CompileRequiresRegisteredPromptServices()
    {
        PromptStack stack = new();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();

        _ = Assert.Throws<InvalidOperationException>(() => stack.Compile(services));
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

    private sealed class RecordingPromptWriter(string source) : IPromptWriter
    {
        public IReadOnlyCollection<PromptSection>? Sections
        {
            get; private set;
        }

        public string Write(IReadOnlyCollection<PromptSection> sections)
        {
            Sections = sections;
            return source;
        }
    }
}
