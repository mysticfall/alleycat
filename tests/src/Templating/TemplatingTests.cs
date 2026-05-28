using System.Globalization;
using System.Reflection;
using AlleyCat.Core;
using AlleyCat.Templating;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Templating;

/// <summary>
/// Unit coverage for the TMPL-001 templating contracts.
/// </summary>
public sealed class TemplatingTests
{
    /// <summary>
    /// The production compiler is a Godot-authored resource as required by TMPL-001.
    /// </summary>
    [Fact]
    public void HandlebarsCompilerIsGodotAuthorableResource()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(HandlebarsTemplateCompiler)));
        Assert.NotNull(typeof(HandlebarsTemplateCompiler).GetCustomAttribute<GlobalClassAttribute>());
        Assert.NotNull(typeof(HandlebarsTemplateCompiler).GetCustomAttribute<ToolAttribute>());
    }

    /// <summary>
    /// The production compiler can register itself through the generic service registrar path.
    /// </summary>
    [Fact]
    public void HandlebarsCompilerRegistersTemplateCompilerServiceThroughRegistrarContract()
    {
        Assert.True(typeof(IServiceRegistrar).IsAssignableFrom(typeof(HandlebarsTemplateCompiler)));
        Assert.Contains(
            typeof(ITemplateCompiler),
            typeof(HandlebarsTemplateCompiler).GetInterfaces());
    }

    /// <summary>
    /// Compiled templates substitute values from the render context.
    /// </summary>
    [Fact]
    public void CompileAndRenderSubstitutesContextValues()
    {
        HandlebarsTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("Hello {{name}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "World",
        });

        Assert.Equal("Hello World", result);
    }

    /// <summary>
    /// Registered partials render through Handlebars partial syntax.
    /// </summary>
    [Fact]
    public void RegisteredPartialRendersThroughHandlebarsSyntax()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        compiler.RegisterPartial("label", "{{name}}!");

        ITemplate template = compiler.Compile("Hello {{> label}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "Nyx",
        });

        Assert.Equal("Hello Nyx!", result);
    }

    /// <summary>
    /// Custom tools can be registered without changing the compiler.
    /// </summary>
    [Fact]
    public void CustomToolCanBeRegisteredAndInvoked()
    {
        DelegateTemplateTool tool = new("shout", arguments =>
            Convert.ToString(arguments[0], CultureInfo.InvariantCulture)?.ToUpperInvariant() ?? string.Empty);
        HandlebarsTemplateCompilerEngine compiler = new();
        compiler.RegisterTool(tool);

        ITemplate template = compiler.Compile("{{shout name}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "hello",
        });

        Assert.Equal("HELLO", result);
    }

    /// <summary>
    /// Configured tools register after built-in tools.
    /// </summary>
    [Fact]
    public void ConfiguredToolCanBeRegisteredAndInvoked()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        HandlebarsTemplateCompilerConfiguration.Apply(
            compiler,
            string.Empty,
            [new DelegateTemplateTool("bracket", arguments => $"[{arguments[0]}]")],
            []);

        ITemplate template = compiler.Compile("{{bracket name}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "Nyx",
        });

        Assert.Equal("[Nyx]", result);
    }

    /// <summary>
    /// Configured partial directories load files as partials named by file stem.
    /// </summary>
    [Fact]
    public void ConfiguredPartialDirectoryLoadsFilePartials()
    {
        string directoryPath = CreateTemporaryPartialDirectory();
        File.WriteAllText(Path.Combine(directoryPath, "subject.hbs"), "{{name}}");
        File.WriteAllText(Path.Combine(directoryPath, "greeting.txt"), "Hello {{> subject}}!");
        HandlebarsTemplateCompilerEngine compiler = new();
        HandlebarsTemplateCompilerConfiguration.Apply(compiler, directoryPath, [], []);

        ITemplate template = compiler.Compile("{{> greeting}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["name"] = "Mira",
        });

        Assert.Equal("Hello Mira!", result);
    }

    /// <summary>
    /// Duplicate configured partial names fail clearly before compilation succeeds.
    /// </summary>
    [Fact]
    public void ConfiguredPartialDirectoryRejectsDuplicatePartialNames()
    {
        string directoryPath = CreateTemporaryPartialDirectory();
        File.WriteAllText(Path.Combine(directoryPath, "item.hbs"), "one");
        File.WriteAllText(Path.Combine(directoryPath, "item.txt"), "two");
        HandlebarsTemplateCompilerEngine compiler = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            HandlebarsTemplateCompilerConfiguration.Apply(compiler, directoryPath, [], []));

        Assert.Contains("item", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The built-in add tool sums the first two integer-like arguments.
    /// </summary>
    [Fact]
    public void BuiltInAddAddsFirstTwoIntegerArguments()
    {
        HandlebarsTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("{{add left right}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["left"] = 2,
            ["right"] = "3",
        });

        Assert.Equal("5", result);
    }

    /// <summary>
    /// The built-in eq tool uses ordinal case-insensitive string comparison.
    /// </summary>
    [Fact]
    public void BuiltInEqUsesOrdinalCaseInsensitiveComparison()
    {
        HandlebarsTemplateCompilerEngine compiler = new();

        ITemplate equalTemplate = compiler.Compile("{{eq left right}}");
        ITemplate notEqualTemplate = compiler.Compile("{{eq left other}}");
        Dictionary<string, object?> context = new()
        {
            ["left"] = "test",
            ["right"] = "TEST",
            ["other"] = "toast",
        };

        Assert.Equal("true", equalTemplate.Render(context));
        Assert.Equal(string.Empty, notEqualTemplate.Render(context));
    }

    /// <summary>
    /// The built-in nf tool uses fixed-point formatting with clamped precision.
    /// </summary>
    [Fact]
    public void BuiltInNumberFormatUsesFixedPointDefaultPrecisionAndClampsPrecision()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            ITemplate defaultTemplate = compiler.Compile("{{nf value}}");
            ITemplate zeroPrecisionTemplate = compiler.Compile("{{nf value -1}}");

            string defaultResult = defaultTemplate.Render(new Dictionary<string, object?>
            {
                ["value"] = 3.14159,
            });
            string zeroPrecisionResult = zeroPrecisionTemplate.Render(new Dictionary<string, object?>
            {
                ["value"] = "3.9",
            });

            Assert.Equal("3.142", defaultResult);
            Assert.Equal("4", zeroPrecisionResult);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// The built-in nf tool uses current-culture decimal separators and clamps high precision.
    /// </summary>
    [Fact]
    public void BuiltInNumberFormatUsesCurrentCultureAndClampsHighPrecision()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            ITemplate cultureTemplate = compiler.Compile("{{nf value 1}}");
            ITemplate highPrecisionTemplate = compiler.Compile("{{nf value 120}}");

            string cultureResult = cultureTemplate.Render(new Dictionary<string, object?>
            {
                ["value"] = 3.5,
            });
            string highPrecisionResult = highPrecisionTemplate.Render(new Dictionary<string, object?>
            {
                ["value"] = 1,
            });

            Assert.Equal("3,5", cultureResult);
            Assert.Equal(101, highPrecisionResult.Length);
            Assert.StartsWith("1,", highPrecisionResult, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    /// <summary>
    /// Duplicate partial names are rejected with a clear invalid-operation failure.
    /// </summary>
    [Fact]
    public void DuplicatePartialRegistrationThrows()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        compiler.RegisterPartial("item", "{{name}}");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            compiler.RegisterPartial("item", "{{other}}"));

        Assert.Contains("item", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duplicate tool names are rejected with a clear invalid-operation failure.
    /// </summary>
    [Fact]
    public void DuplicateToolRegistrationThrows()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        DelegateTemplateTool tool = new("custom", _ => string.Empty);
        compiler.RegisterTool(tool);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            compiler.RegisterTool(tool));

        Assert.Contains("custom", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The built-in repeat tool writes a value a configured number of times.
    /// </summary>
    [Fact]
    public void BuiltInRepeatRendersValueCountTimes()
    {
        HandlebarsTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("{{repeat value count}}");

        string result = template.Render(new Dictionary<string, object?>
        {
            ["value"] = "A",
            ["count"] = 3,
        });

        Assert.Equal("AAA", result);
    }

    private static string CreateTemporaryPartialDirectory()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "AlleyCat.Templating", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

}
