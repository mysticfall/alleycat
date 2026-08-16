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
    /// The explicit ordinal equality helper is case-sensitive without changing legacy eq semantics.
    /// </summary>
    [Fact]
    public void BuiltInEqOrdinalUsesCaseSensitiveComparison()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        Dictionary<string, object?> context = new()
        {
            ["value"] = "speech.observed",
        };

        ITemplate exactTemplate = compiler.Compile("{{eqOrdinal value \"speech.observed\"}}");
        ITemplate caseMismatchTemplate = compiler.Compile("{{eqOrdinal value \"Speech.Observed\"}}");
        ITemplate legacyTemplate = compiler.Compile("{{eq value \"Speech.Observed\"}}");

        Assert.Equal("true", exactTemplate.Render(context));
        Assert.Equal(string.Empty, caseMismatchTemplate.Render(context));
        Assert.Equal("true", legacyTemplate.Render(context));
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

    /// <summary>
    /// The built-in ago tool renders singular relative-time phrases with a deterministic reference timestamp.
    /// </summary>
    [Fact]
    public void BuiltInAgoRendersSingularRelativeTimePhrases()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ago value now 0}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("1 second ago", RenderAgo(template, now.AddSeconds(-1), now));
        Assert.Equal("1 minute ago", RenderAgo(template, now.AddMinutes(-1), now));
        Assert.Equal("1 hour ago", RenderAgo(template, now.AddHours(-1), now));
        Assert.Equal("1 day ago", RenderAgo(template, now.AddDays(-1), now));
        Assert.Equal("1 week ago", RenderAgo(template, now.AddDays(-7), now));
    }

    /// <summary>
    /// The built-in ago tool floors to the largest whole unit with correct plural forms and unit boundaries.
    /// </summary>
    [Fact]
    public void BuiltInAgoFloorsToLargestWholeUnitWithPluralForms()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ago value now}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("30 seconds ago", RenderAgo(template, now.AddSeconds(-30), now));
        Assert.Equal("59 seconds ago", RenderAgo(template, now.AddSeconds(-59), now));
        Assert.Equal("1 minute ago", RenderAgo(template, now.AddSeconds(-60), now));
        Assert.Equal("2 minutes ago", RenderAgo(template, now.AddMinutes(-2), now));
        Assert.Equal("23 hours ago", RenderAgo(template, now.AddHours(-23), now));
        Assert.Equal("1 day ago", RenderAgo(template, now.AddHours(-24), now));
        Assert.Equal("6 days ago", RenderAgo(template, now.AddDays(-6), now));
        Assert.Equal("1 week ago", RenderAgo(template, now.AddDays(-7), now));
        Assert.Equal("1 minute ago", RenderAgo(template, now.AddSeconds(-90), now));
    }

    /// <summary>
    /// The built-in ago tool renders just now below the threshold and at the default boundary.
    /// </summary>
    [Fact]
    public void BuiltInAgoUsesJustNowThresholdAndBoundary()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate defaultTemplate = compiler.Compile("{{ago value now}}");
        ITemplate explicitTemplate = compiler.Compile("{{ago value now 10}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", RenderAgo(defaultTemplate, now.AddSeconds(-4), now));
        Assert.Equal("5 seconds ago", RenderAgo(defaultTemplate, now.AddSeconds(-5), now));
        Assert.Equal("just now", RenderAgo(explicitTemplate, now.AddSeconds(-8), now));
        Assert.Equal("10 seconds ago", RenderAgo(explicitTemplate, now.AddSeconds(-10), now));
    }

    /// <summary>
    /// The built-in ago tool renders just now for future timestamps and empty for null or unparseable input.
    /// </summary>
    [Fact]
    public void BuiltInAgoHandlesFutureNullAndUnparseableInput()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ago value now}}");
        ITemplate emptyTemplate = compiler.Compile("{{ago}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("just now", RenderAgo(template, now.AddSeconds(10), now));
        Assert.Equal(string.Empty, RenderAgo(template, null, now));
        Assert.Equal(string.Empty, RenderAgo(template, "not a timestamp", now));
        Assert.Equal(string.Empty, emptyTemplate.Render(new Dictionary<string, object?>()));
    }

    /// <summary>
    /// The built-in ago tool parses ISO-8601 and round-trip string timestamps as UTC.
    /// </summary>
    [Fact]
    public void BuiltInAgoParsesIso8601AndRoundTripStrings()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ago value now}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal("30 seconds ago", RenderAgo(template, "2026-01-01T11:59:30Z", now));
        Assert.Equal("30 seconds ago", RenderAgo(template, now.AddSeconds(-30).ToString("O"), now));
    }

    /// <summary>
    /// The built-in ago tool treats DateTime values as UTC for Utc and Unspecified kinds.
    /// </summary>
    [Fact]
    public void BuiltInAgoParsesDateTimeValuesAsUtc()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ago value now}}");
        DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            "30 seconds ago",
            RenderAgo(template, new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Utc), now));
        Assert.Equal(
            "30 seconds ago",
            RenderAgo(template, new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Unspecified), now));
    }

    /// <summary>
    /// The built-in ago tool parses round-trip strings under a non-invariant current culture.
    /// </summary>
    [Fact]
    public void BuiltInAgoParsesRoundTripStringsUnderCurrentCulture()
    {
        HandlebarsTemplateCompilerEngine compiler = new();
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            ITemplate template = compiler.Compile("{{ago value now}}");
            DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

            Assert.Equal("30 seconds ago", RenderAgo(template, now.AddSeconds(-30).ToString("O"), now));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static string RenderAgo(ITemplate template, object? value, DateTimeOffset now)
        => template.Render(new Dictionary<string, object?>
        {
            ["value"] = value,
            ["now"] = now,
        });

    private static string CreateTemporaryPartialDirectory()
    {
        string directoryPath = Path.Combine(Path.GetTempPath(), "AlleyCat.Templating", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directoryPath);
        return directoryPath;
    }

}
