using System.Globalization;
using System.Reflection;
using AlleyCat.Core;
using AlleyCat.Templating;
using Fluid;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Templating;

/// <summary>
/// Unit coverage for the TMPL-001 templating contracts over the Fluid (Liquid) engine.
/// </summary>
public sealed class TemplatingTests
{
    /// <summary>
    /// The production compiler is a Godot-authored resource as required by TMPL-001.
    /// </summary>
    [Fact]
    public void FluidCompilerIsGodotAuthorableResource()
    {
        Assert.True(typeof(Resource).IsAssignableFrom(typeof(FluidTemplateCompiler)));
        Assert.NotNull(typeof(FluidTemplateCompiler).GetCustomAttribute<GlobalClassAttribute>());
        Assert.NotNull(typeof(FluidTemplateCompiler).GetCustomAttribute<ToolAttribute>());
    }

    /// <summary>
    /// The production compiler can register itself through the generic service registrar path.
    /// </summary>
    [Fact]
    public void FluidCompilerRegistersTemplateCompilerServiceThroughRegistrarContract()
    {
        Assert.True(typeof(IServiceRegistrar).IsAssignableFrom(typeof(FluidTemplateCompiler)));
        Assert.Contains(
            typeof(ITemplateCompiler),
            typeof(FluidTemplateCompiler).GetInterfaces());
    }

    /// <summary>
    /// Compilation stays synchronous while rendering is asynchronous.
    /// </summary>
    [Fact]
    public void CompileStaysSynchronousWhileRenderingIsAsynchronous()
    {
        Assert.Equal(
            typeof(ITemplate),
            typeof(ITemplateCompiler).GetMethod(nameof(ITemplateCompiler.Compile))!.ReturnType);
        MethodInfo? renderMethod = typeof(ITemplate).GetMethod(nameof(ITemplate.RenderAsync));
        Assert.NotNull(renderMethod);
        Assert.Equal(typeof(ValueTask<string>), renderMethod!.ReturnType);
    }

    /// <summary>
    /// Compiled templates substitute values from the render context.
    /// </summary>
    [Fact]
    public async Task CompileAndRenderSubstitutesContextValues()
    {
        FluidTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("Hello {{ name }}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["name"] = "World",
        });

        Assert.Equal("Hello World", result);
    }

    /// <summary>
    /// Registered partials render through the Liquid include syntax.
    /// </summary>
    [Fact]
    public async Task RegisteredPartialRendersThroughLiquidIncludeSyntax()
    {
        FluidTemplateCompilerEngine compiler = new();
        compiler.RegisterPartial("label", "{{name}}!");

        ITemplate template = compiler.Compile("Hello {% include 'label' %}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["name"] = "Nyx",
        });

        Assert.Equal("Hello Nyx!", result);
    }

    /// <summary>
    /// Rendering a template whose include references an unregistered partial fails loudly instead of rendering
    /// an empty fragment.
    /// </summary>
    [Fact]
    public void UnregisteredPartialIncludeFailsLoudly()
    {
        FluidTemplateCompilerEngine compiler = new();

        ITemplate template = compiler.Compile("Hello {% include 'absent_partial' %}");

        _ = Assert.ThrowsAny<Exception>(() =>
            _ = template.RenderAsync(new Dictionary<string, object?>()).AsTask().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Custom tools can be registered without changing the compiler and receive positional arguments.
    /// </summary>
    [Fact]
    public async Task CustomToolCanBeRegisteredAndInvoked()
    {
        DelegateTemplateTool tool = new("shout", arguments =>
            Convert.ToString(arguments[0], CultureInfo.InvariantCulture)?.ToUpperInvariant() ?? string.Empty);
        FluidTemplateCompilerEngine compiler = new();
        compiler.RegisterTool(tool);

        ITemplate template = compiler.Compile("{{ shout(name) }}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["name"] = "hello",
        });

        Assert.Equal("HELLO", result);
    }

    /// <summary>
    /// Configured tools register after built-in tools.
    /// </summary>
    [Fact]
    public async Task ConfiguredToolCanBeRegisteredAndInvoked()
    {
        FluidTemplateCompilerEngine compiler = new();
        FluidTemplateCompilerConfiguration.Apply(
            compiler,
            string.Empty,
            [new DelegateTemplateTool("bracket", arguments => $"[{arguments[0]}]")],
            []);

        ITemplate template = compiler.Compile("{{ bracket(name) }}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
        {
            ["name"] = "Nyx",
        });

        Assert.Equal("[Nyx]", result);
    }

    /// <summary>
    /// Configured partial directories load files as partials named by file stem.
    /// </summary>
    [Fact]
    public async Task ConfiguredPartialDirectoryLoadsFilePartials()
    {
        string directoryPath = CreateTemporaryPartialDirectory();
        File.WriteAllText(Path.Combine(directoryPath, "subject.hbs"), "{{name}}");
        File.WriteAllText(Path.Combine(directoryPath, "greeting.txt"), "Hello {% include 'subject' %}!");
        FluidTemplateCompilerEngine compiler = new();
        FluidTemplateCompilerConfiguration.Apply(compiler, directoryPath, [], []);

        ITemplate template = compiler.Compile("{% include 'greeting' %}");

        string result = await template.RenderAsync(new Dictionary<string, object?>
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
        FluidTemplateCompilerEngine compiler = new();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            FluidTemplateCompilerConfiguration.Apply(compiler, directoryPath, [], []));

        Assert.Contains("item", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Duplicate partial names are rejected with a clear invalid-operation failure.
    /// </summary>
    [Fact]
    public void DuplicatePartialRegistrationThrows()
    {
        FluidTemplateCompilerEngine compiler = new();
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
        FluidTemplateCompilerEngine compiler = new();
        DelegateTemplateTool tool = new("custom", _ => string.Empty);
        compiler.RegisterTool(tool);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            compiler.RegisterTool(tool));

        Assert.Contains("custom", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nested PascalCase dictionary members resolve through dot paths.
    /// </summary>
    [Fact]
    public async Task MemberAccessResolvesPascalCaseDictionaryPaths()
    {
        string result = await RenderAsync(
            "{{ character.FullId }}/{{ player.FullId }}/{{ record.Content }}/{{ record.ObservedAt }}",
            new Dictionary<string, object?>
            {
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "char:npc_kaori" },
                ["player"] = new Dictionary<string, object?> { ["FullId"] = "char:player_ava" },
                ["record"] = new Dictionary<string, object?> { ["Content"] = "knock", ["ObservedAt"] = 12.5d },
            });

        Assert.Equal("char:npc_kaori/char:player_ava/knock/12.5", result);
    }

    /// <summary>
    /// Variable and member lookup is ordinal case-sensitive, matching the pre-migration engine.
    /// </summary>
    [Fact]
    public async Task MemberAccessIsCaseSensitive()
    {
        string result = await RenderAsync(
            "{{ fullid }}|{{ FullId }}|{{ character.fullid }}|{{ character.FullId }}",
            new Dictionary<string, object?>
            {
                ["FullId"] = "top-level",
                ["character"] = new Dictionary<string, object?> { ["FullId"] = "nested" },
            });

        Assert.Equal("|top-level||nested", result);
    }

    /// <summary>
    /// Conditionals follow Liquid truthiness: only nil and false are falsy while empty strings, zero, and empty
    /// collections remain truthy.
    /// </summary>
    [Theory]
    [InlineData(null, "falsy")]
    [InlineData(false, "falsy")]
    [InlineData("", "truthy")]
    [InlineData(0, "truthy")]
    [InlineData("text", "truthy")]
    [InlineData(true, "truthy")]
    public async Task ConditionalsFollowLiquidTruthiness(object? value, string expectedBranch)
    {
        string result = await RenderAsync(
            "{% if value %}truthy{% else %}falsy{% endif %}",
            new Dictionary<string, object?> { ["value"] = value });

        Assert.Equal(expectedBranch, result);
    }

    /// <summary>
    /// Empty collections stay truthy under Liquid conditional semantics.
    /// </summary>
    [Fact]
    public async Task EmptyCollectionsStayTruthyInConditionals()
    {
        string arrayResult = await RenderAsync(
            "{% if items %}truthy{% else %}falsy{% endif %}",
            new Dictionary<string, object?> { ["items"] = Array.Empty<object?>() });
        string dictionaryResult = await RenderAsync(
            "{% if map %}truthy{% else %}falsy{% endif %}",
            new Dictionary<string, object?> { ["map"] = new Dictionary<string, object?>() });

        Assert.Equal("truthy", arrayResult);
        Assert.Equal("truthy", dictionaryResult);
    }

    /// <summary>
    /// Blank guards reproduce the pre-migration falsy treatment of empty text through explicit comparisons.
    /// </summary>
    [Fact]
    public async Task BlankComparisonReproducesEmptyStringFalsyGuards()
    {
        string result = await RenderAsync(
            "{% if actor != blank %}known:{{ actor }}{% else %}unknown{% endif %}",
            new Dictionary<string, object?>
            {
                ["actor"] = "",
                ["other"] = "char:rin",
            });

        Assert.Equal("unknown", result);
    }

    /// <summary>
    /// Missing variables render as empty text because strict variable mode stays disabled.
    /// </summary>
    [Fact]
    public async Task MissingVariablesRenderEmptyText()
    {
        string result = await RenderAsync("[{{ absent }}]", new Dictionary<string, object?>());

        Assert.Equal("[]", result);
    }

    /// <summary>
    /// Rendered output receives no HTML encoding.
    /// </summary>
    [Fact]
    public async Task RenderedOutputIsNotHtmlEncoded()
    {
        string result = await RenderAsync(
            "{{ markup }}",
            new Dictionary<string, object?> { ["markup"] = "<b>&</b>" });

        Assert.Equal("<b>&</b>", result);
    }

    /// <summary>
    /// Unresolved variables passed to tools keep their positional arity through nil sentinels so argument
    /// positions never shift.
    /// </summary>
    [Fact]
    public async Task UnresolvedFunctionArgumentsKeepPositionalArity()
    {
        string result = await RenderAsync(
            "[{{ nf(absent) }}]|[{{ add(absent, two) }}]",
            new Dictionary<string, object?> { ["two"] = 2 });

        Assert.Equal("[]|[2]", result);
    }

    /// <summary>
    /// Malformed sources such as unclosed blocks throw a parse exception at compilation time.
    /// </summary>
    [Fact]
    public void UnclosedBlockThrowsParseExceptionAtCompileTime()
    {
        FluidTemplateCompilerEngine compiler = new();

        Exception exception = Record.Exception(() => compiler.Compile("{% if value %}unclosed"));

        Assert.NotNull(exception);
        _ = Assert.IsType<ParseException>(exception);
    }

    /// <summary>
    /// Whitespace control markers are supported for byte-exact fragment composition.
    /// </summary>
    [Fact]
    public async Task WhitespaceControlMarkersAreSupported()
    {
        string result = await RenderAsync(
            "A\n{%- if flag -%}\nX\n{%- endif -%}\nB",
            new Dictionary<string, object?> { ["flag"] = true });

        Assert.Equal("AXB", result);
    }

    /// <summary>
    /// The built-in add tool sums the first two integer-like arguments.
    /// </summary>
    [Fact]
    public async Task BuiltInAddAddsFirstTwoIntegerArguments()
    {
        string result = await RenderAsync(
            "{{ add(left, right) }}",
            new Dictionary<string, object?>
            {
                ["left"] = 2,
                ["right"] = "3",
            });

        Assert.Equal("5", result);
    }

    /// <summary>
    /// The built-in add tool renders empty when fewer than two arguments are supplied.
    /// </summary>
    [Fact]
    public async Task BuiltInAddRendersEmptyWithMissingArguments()
    {
        string result = await RenderAsync(
            "|{{ add() }}|{{ add(one) }}|",
            new Dictionary<string, object?> { ["one"] = 7 });

        Assert.Equal("|||", result);
    }

    /// <summary>
    /// The built-in eq tool uses ordinal case-insensitive string comparison.
    /// </summary>
    [Fact]
    public async Task BuiltInEqUsesOrdinalCaseInsensitiveComparison()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate equalTemplate = compiler.Compile("{{ eq(left, right) }}");
        ITemplate notEqualTemplate = compiler.Compile("{{ eq(left, other) }}");
        Dictionary<string, object?> context = new()
        {
            ["left"] = "test",
            ["right"] = "TEST",
            ["other"] = "toast",
        };

        Assert.Equal("true", await equalTemplate.RenderAsync(context));
        Assert.Equal(string.Empty, await notEqualTemplate.RenderAsync(context));
    }

    /// <summary>
    /// The explicit ordinal equality helper is case-sensitive without changing legacy eq semantics.
    /// </summary>
    [Fact]
    public async Task BuiltInEqOrdinalUsesCaseSensitiveComparison()
    {
        FluidTemplateCompilerEngine compiler = new();
        Dictionary<string, object?> context = new()
        {
            ["value"] = "speech.observed",
        };

        ITemplate exactTemplate = compiler.Compile("{{ eqOrdinal(value, 'speech.observed') }}");
        ITemplate caseMismatchTemplate = compiler.Compile("{{ eqOrdinal(value, 'Speech.Observed') }}");
        ITemplate legacyTemplate = compiler.Compile("{{ eq(value, 'Speech.Observed') }}");

        Assert.Equal("true", await exactTemplate.RenderAsync(context));
        Assert.Equal(string.Empty, await caseMismatchTemplate.RenderAsync(context));
        Assert.Equal("true", await legacyTemplate.RenderAsync(context));
    }

    /// <summary>
    /// The built-in nf tool uses fixed-point formatting with clamped precision.
    /// </summary>
    [Fact]
    public async Task BuiltInNumberFormatUsesFixedPointDefaultPrecisionAndClampsPrecision()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        try
        {
            FluidTemplateCompilerEngine compiler = new();
            ITemplate defaultTemplate = compiler.Compile("{{ nf(value) }}");
            ITemplate zeroPrecisionTemplate = compiler.Compile("{{ nf(value, -1) }}");

            string defaultResult = await defaultTemplate.RenderAsync(new Dictionary<string, object?>
            {
                ["value"] = 3.14159,
            });
            string zeroPrecisionResult = await zeroPrecisionTemplate.RenderAsync(new Dictionary<string, object?>
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
    public async Task BuiltInNumberFormatUsesCurrentCultureAndClampsHighPrecision()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            FluidTemplateCompilerEngine compiler = new();
            ITemplate cultureTemplate = compiler.Compile("{{ nf(value, 1) }}");
            ITemplate highPrecisionTemplate = compiler.Compile("{{ nf(value, 120) }}");

            string cultureResult = await cultureTemplate.RenderAsync(new Dictionary<string, object?>
            {
                ["value"] = 3.5,
            });
            string highPrecisionResult = await highPrecisionTemplate.RenderAsync(new Dictionary<string, object?>
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
    /// The built-in repeat tool writes a value a configured number of times.
    /// </summary>
    [Fact]
    public async Task BuiltInRepeatRendersValueCountTimes()
    {
        string result = await RenderAsync(
            "{{ repeat(value, count) }}",
            new Dictionary<string, object?>
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
    public async Task BuiltInAgoRendersSingularRelativeTimePhrases()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ ago(value, now, 0) }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal("1 second ago", await RenderAgo(template, now.AddSeconds(-1), now));
        Assert.Equal("1 minute ago", await RenderAgo(template, now.AddMinutes(-1), now));
        Assert.Equal("1 hour ago", await RenderAgo(template, now.AddHours(-1), now));
        Assert.Equal("1 day ago", await RenderAgo(template, now.AddDays(-1), now));
        Assert.Equal("1 week ago", await RenderAgo(template, now.AddDays(-7), now));
    }

    /// <summary>
    /// The built-in ago tool floors to the largest whole unit with correct plural forms and unit boundaries.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoFloorsToLargestWholeUnitWithPluralForms()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ ago(value, now) }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal("30 seconds ago", await RenderAgo(template, now.AddSeconds(-30), now));
        Assert.Equal("59 seconds ago", await RenderAgo(template, now.AddSeconds(-59), now));
        Assert.Equal("1 minute ago", await RenderAgo(template, now.AddSeconds(-60), now));
        Assert.Equal("2 minutes ago", await RenderAgo(template, now.AddMinutes(-2), now));
        Assert.Equal("23 hours ago", await RenderAgo(template, now.AddHours(-23), now));
        Assert.Equal("1 day ago", await RenderAgo(template, now.AddHours(-24), now));
        Assert.Equal("6 days ago", await RenderAgo(template, now.AddDays(-6), now));
        Assert.Equal("1 week ago", await RenderAgo(template, now.AddDays(-7), now));
        Assert.Equal("1 minute ago", await RenderAgo(template, now.AddSeconds(-90), now));
    }

    /// <summary>
    /// The built-in ago tool renders just now below the threshold and at the default boundary.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoUsesJustNowThresholdAndBoundary()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate defaultTemplate = compiler.Compile("{{ ago(value, now) }}");
        ITemplate explicitTemplate = compiler.Compile("{{ ago(value, now, 10) }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal("just now", await RenderAgo(defaultTemplate, now.AddSeconds(-4), now));
        Assert.Equal("5 seconds ago", await RenderAgo(defaultTemplate, now.AddSeconds(-5), now));
        Assert.Equal("just now", await RenderAgo(explicitTemplate, now.AddSeconds(-8), now));
        Assert.Equal("10 seconds ago", await RenderAgo(explicitTemplate, now.AddSeconds(-10), now));
    }

    /// <summary>
    /// The built-in ago tool renders just now for future timestamps and empty for null or unparseable input.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoHandlesFutureNullAndUnparseableInput()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ ago(value, now) }}");
        ITemplate emptyTemplate = compiler.Compile("{{ ago() }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal("just now", await RenderAgo(template, now.AddSeconds(10), now));
        Assert.Equal(string.Empty, await RenderAgo(template, null, now));
        Assert.Equal(string.Empty, await RenderAgo(template, "not a timestamp", now));
        Assert.Equal(string.Empty, await emptyTemplate.RenderAsync(new Dictionary<string, object?>()));
    }

    /// <summary>
    /// The built-in ago tool parses ISO-8601 and round-trip string timestamps as UTC.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoParsesIso8601AndRoundTripStrings()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ ago(value, now) }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal("30 seconds ago", await RenderAgo(template, "2026-01-01T11:59:30Z", now));
        Assert.Equal("30 seconds ago", await RenderAgo(template, now.AddSeconds(-30).ToString("O"), now));
    }

    /// <summary>
    /// The built-in ago tool treats DateTime values as UTC for Utc and Unspecified kinds.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoParsesDateTimeValuesAsUtc()
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile("{{ ago(value, now) }}");
        DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

        Assert.Equal(
            "30 seconds ago",
            await RenderAgo(template, new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Utc), now));
        Assert.Equal(
            "30 seconds ago",
            await RenderAgo(template, new DateTime(2026, 1, 1, 11, 59, 30, DateTimeKind.Unspecified), now));
    }

    /// <summary>
    /// The built-in ago tool parses round-trip strings under a non-invariant current culture.
    /// </summary>
    [Fact]
    public async Task BuiltInAgoParsesRoundTripStringsUnderCurrentCulture()
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");

        try
        {
            FluidTemplateCompilerEngine compiler = new();
            ITemplate template = compiler.Compile("{{ ago(value, now) }}");
            DateTimeOffset now = TemplatingBaselineScenarios.AgoNow;

            Assert.Equal("30 seconds ago", await RenderAgo(template, now.AddSeconds(-30).ToString("O"), now));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    private static async Task<string> RenderAsync(
        string source,
        IReadOnlyDictionary<string, object?>? context = null)
    {
        FluidTemplateCompilerEngine compiler = new();
        ITemplate template = compiler.Compile(source);
        return await template.RenderAsync(context ?? new Dictionary<string, object?>());
    }

    private static async Task<string> RenderAgo(ITemplate template, object? value, DateTimeOffset now)
        => await template.RenderAsync(new Dictionary<string, object?>
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
