using System.Reflection;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;
using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Tests command-line selection and deterministic test identity behaviour.
/// </summary>
public sealed class TestSelectionOptionsTests
{
    /// <summary>
    /// Ensures the custom provider exposes the expected selector options.
    /// </summary>
    [Fact]
    public void CommandLineOptionsProvider_ReturnsExpectedOptions()
    {
        var provider = new GodotTestCommandLineOptionsProvider();

        IReadOnlyCollection<CommandLineOption> options = provider.GetCommandLineOptions();

        Assert.Contains(options, option => option.Name == GodotTestCommandLineOptions.TestClassOptionName);
        Assert.Contains(options, option => option.Name == GodotTestCommandLineOptions.TestMethodOptionName);
        Assert.Contains(options, option => option.Name == GodotTestCommandLineOptions.HeadlessOptionName);

        CommandLineOption headlessOption = options.Single(o => o.Name == GodotTestCommandLineOptions.HeadlessOptionName);
        Assert.Equal(ArgumentArity.Zero, headlessOption.Arity);
    }

    /// <summary>
    /// Ensures invalid method selector format is rejected during validation.
    /// </summary>
    [Fact]
    public async Task ValidateCommandLineOptionsAsync_RejectsInvalidMethodSelectorFormat()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestMethodOptionName] = ["InvalidMethodSelector"],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);

        Assert.False(validation.IsValid);
        Assert.Contains("<Fully.Qualified.TypeName>.<MethodName>", validation.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures selector values are trimmed before validation and parsing.
    /// </summary>
    [Fact]
    public async Task ValidateCommandLineOptionsAsync_TrimsSelectorValuesBeforeParsing()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        string methodSelector = $" {typeof(SelectorFixtureA).FullName}.{nameof(SelectorFixtureA.TargetMethod)} ";
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestMethodOptionName] = [methodSelector],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);
        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);
        MethodInfo selectedMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;

        Assert.True(validation.IsValid);
        Assert.True(selector.Matches(selectedMethod));
    }

    /// <summary>
    /// Ensures class selector values are trimmed and match class methods.
    /// </summary>
    [Fact]
    public async Task ValidateCommandLineOptionsAsync_TrimsClassSelectorValuesBeforeParsing()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        string classSelector = $" {typeof(SelectorFixtureA).FullName} ";
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestClassOptionName] = [classSelector],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);
        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);
        MethodInfo selectedMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;

        Assert.True(validation.IsValid);
        Assert.True(selector.Matches(selectedMethod));
    }

    /// <summary>
    /// Ensures whitespace-only selector values remain invalid.
    /// </summary>
    [Theory]
    [InlineData(GodotTestCommandLineOptions.TestClassOptionName)]
    [InlineData(GodotTestCommandLineOptions.TestMethodOptionName)]
    public async Task ValidateCommandLineOptionsAsync_RejectsWhitespaceOnlySelector(string optionName)
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [optionName] = ["   \t   "],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);

        Assert.False(validation.IsValid);
        Assert.Contains("expects exactly one value", validation.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures method selector takes precedence over class selector.
    /// </summary>
    [Fact]
    public void Parse_UsesMethodSelectorPrecedenceOverClassSelector()
    {
        string classSelector = typeof(SelectorFixtureA).FullName!;
        string methodSelector = $"{typeof(SelectorFixtureB).FullName}.{nameof(SelectorFixtureB.TargetMethod)}";

        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestClassOptionName] = [classSelector],
            [GodotTestCommandLineOptions.TestMethodOptionName] = [methodSelector],
        });

        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);

        MethodInfo classMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;
        MethodInfo selectedMethod = typeof(SelectorFixtureB).GetMethod(nameof(SelectorFixtureB.TargetMethod))!;

        Assert.False(selector.Matches(classMethod));
        Assert.True(selector.Matches(selectedMethod));
    }

    /// <summary>
    /// Ensures selector matching works for both class and method modes.
    /// </summary>
    [Fact]
    public void Selector_MatchesClassAndMethodSelectorsCorrectly()
    {
        var classSelector = GodotCliTestSelector.ForClass(typeof(SelectorFixtureA).FullName!);
        var methodSelector = GodotCliTestSelector.ForMethod(
            typeof(SelectorFixtureA).FullName!,
            nameof(SelectorFixtureA.TargetMethod));

        MethodInfo targetMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;
        MethodInfo otherMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.OtherMethod))!;
        MethodInfo externalMethod = typeof(SelectorFixtureB).GetMethod(nameof(SelectorFixtureB.TargetMethod))!;

        Assert.True(classSelector.Matches(targetMethod));
        Assert.True(classSelector.Matches(otherMethod));
        Assert.False(classSelector.Matches(externalMethod));

        Assert.True(methodSelector.Matches(targetMethod));
        Assert.False(methodSelector.Matches(otherMethod));
        Assert.False(methodSelector.Matches(externalMethod));
    }

    /// <summary>
    /// Ensures a comma-separated <c>--test-class</c> value selects every listed class.
    /// </summary>
    [Fact]
    public void Parse_AcceptsCommaSeparatedClassList_AndMatchesAllListedClasses()
    {
        string classSelector = $"{typeof(SelectorFixtureA).FullName!},{typeof(SelectorFixtureB).FullName!}";
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestClassOptionName] = [classSelector],
        });

        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);

        MethodInfo fixtureAMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;
        MethodInfo fixtureBMethod = typeof(SelectorFixtureB).GetMethod(nameof(SelectorFixtureB.TargetMethod))!;
        MethodInfo externalMethod = typeof(UidFixtureB).GetMethod(nameof(UidFixtureB.Target))!;

        Assert.True(selector.Matches(fixtureAMethod));
        Assert.True(selector.Matches(fixtureBMethod));
        Assert.False(selector.Matches(externalMethod));
    }

    /// <summary>
    /// Ensures a comma-separated <c>--test-method</c> value selects every listed method.
    /// </summary>
    [Fact]
    public void Parse_AcceptsCommaSeparatedMethodList_AndMatchesAllListedMethods()
    {
        string methodSelector =
            $"{typeof(SelectorFixtureA).FullName!}.{nameof(SelectorFixtureA.TargetMethod)}," +
            $"{typeof(SelectorFixtureB).FullName!}.{nameof(SelectorFixtureB.TargetMethod)}";
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestMethodOptionName] = [methodSelector],
        });

        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);

        MethodInfo fixtureAMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;
        MethodInfo fixtureBMethod = typeof(SelectorFixtureB).GetMethod(nameof(SelectorFixtureB.TargetMethod))!;
        MethodInfo fixtureAOtherMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.OtherMethod))!;

        Assert.True(selector.Matches(fixtureAMethod));
        Assert.True(selector.Matches(fixtureBMethod));
        Assert.False(selector.Matches(fixtureAOtherMethod));
    }

    /// <summary>
    /// Ensures validation rejects a comma-separated method list containing a malformed entry.
    /// </summary>
    [Fact]
    public async Task ValidateCommandLineOptionsAsync_RejectsMalformedEntryInCommaSeparatedMethodList()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestMethodOptionName] =
            [$"{typeof(SelectorFixtureA).FullName!}.{nameof(SelectorFixtureA.TargetMethod)},NotAMethodSelector"],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);

        Assert.False(validation.IsValid);
        Assert.Contains("<Fully.Qualified.TypeName>.<MethodName>", validation.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures validation rejects a comma-separated class list whose entries are all empty.
    /// </summary>
    [Fact]
    public async Task ValidateCommandLineOptionsAsync_RejectsAllEmptyClassList()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestClassOptionName] = [","],
        });

        ValidationResult validation = await provider.ValidateCommandLineOptionsAsync(commandLineOptions);

        Assert.False(validation.IsValid);
        Assert.Contains("at least one fully qualified class name", validation.ErrorMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ensures class list entries are trimmed and empty entries are ignored.
    /// </summary>
    [Fact]
    public void Parse_TrimsCommaSeparatedClassEntries_AndIgnoresEmptyEntries()
    {
        string classSelector = $" {typeof(SelectorFixtureA).FullName!} ,,{typeof(SelectorFixtureB).FullName!}";
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>
        {
            [GodotTestCommandLineOptions.TestClassOptionName] = [classSelector],
        });

        GodotCliTestSelector selector = GodotTestCommandLineOptions.Parse(commandLineOptions);

        MethodInfo fixtureAMethod = typeof(SelectorFixtureA).GetMethod(nameof(SelectorFixtureA.TargetMethod))!;
        MethodInfo fixtureBMethod = typeof(SelectorFixtureB).GetMethod(nameof(SelectorFixtureB.TargetMethod))!;

        Assert.True(selector.Matches(fixtureAMethod));
        Assert.True(selector.Matches(fixtureBMethod));
    }

    /// <summary>
    /// Ensures deterministic UID generation remains stable and unique for distinct inputs.
    /// </summary>
    [Fact]
    public void TestCaseUidFactory_IsDeterministicForSameInput_AndDifferentForDistinctInput()
    {
        const string firstIdentity = "AlleyCat.Tests.SampleFixture.FirstMethod";
        const string secondIdentity = "AlleyCat.Tests.SampleFixture.SecondMethod";

        string firstUid = TestCaseUidFactory.Create(firstIdentity);
        string repeatedFirstUid = TestCaseUidFactory.Create(firstIdentity);
        string secondUid = TestCaseUidFactory.Create(secondIdentity);

        Assert.Equal(firstUid, repeatedFirstUid);
        Assert.NotEqual(firstUid, secondUid);
    }

    /// <summary>
    /// Ensures canonical method identity differentiates signatures and declaring types.
    /// </summary>
    [Fact]
    public void TestCaseUidFactory_CreateFromMethodInfo_IsStableAndDifferentiatesCanonicalIdentity()
    {
        MethodInfo targetNoArgs = typeof(UidFixtureA).GetMethod(nameof(UidFixtureA.Target), Type.EmptyTypes)!;
        MethodInfo targetWithInt = typeof(UidFixtureA).GetMethod(nameof(UidFixtureA.Target), [typeof(int)])!;
        MethodInfo genericTarget = typeof(UidFixtureA).GetMethod(nameof(UidFixtureA.GenericTarget))!;
        MethodInfo sameNameOtherType = typeof(UidFixtureB).GetMethod(nameof(UidFixtureB.Target), Type.EmptyTypes)!;

        string targetNoArgsUid = TestCaseUidFactory.Create(targetNoArgs);
        string repeatedTargetNoArgsUid = TestCaseUidFactory.Create(targetNoArgs);
        string targetWithIntUid = TestCaseUidFactory.Create(targetWithInt);
        string genericTargetUid = TestCaseUidFactory.Create(genericTarget);
        string sameNameOtherTypeUid = TestCaseUidFactory.Create(sameNameOtherType);

        Assert.Equal(targetNoArgsUid, repeatedTargetNoArgsUid);
        Assert.NotEqual(targetNoArgsUid, targetWithIntUid);
        Assert.NotEqual(targetNoArgsUid, genericTargetUid);
        Assert.NotEqual(targetNoArgsUid, sameNameOtherTypeUid);
    }

    /// <summary>
    /// Ensures <see cref="GodotTestCommandLineOptions.IsHeadless"/> returns <c>true</c> when the flag is set.
    /// </summary>
    [Fact]
    public void IsHeadless_ReturnsTrue_WhenFlagIsSet()
    {
        var commandLineOptions = new StubCommandLineOptions(
            new Dictionary<string, string[]> { [GodotTestCommandLineOptions.HeadlessOptionName] = [] });

        bool result = GodotTestCommandLineOptions.IsHeadless(commandLineOptions);

        Assert.True(result);
    }

    /// <summary>
    /// Ensures <see cref="GodotTestCommandLineOptions.IsHeadless"/> returns <c>false</c> when the flag is not set.
    /// </summary>
    [Fact]
    public void IsHeadless_ReturnsFalse_WhenFlagIsNotSet()
    {
        var commandLineOptions = new StubCommandLineOptions(new Dictionary<string, string[]>());

        bool result = GodotTestCommandLineOptions.IsHeadless(commandLineOptions);

        Assert.False(result);
    }

    /// <summary>
    /// Ensures validation rejects arguments supplied to the <c>--headless</c> flag.
    /// </summary>
    [Fact]
    public async Task ValidateOptionArgumentsAsync_RejectsArgumentsForHeadless()
    {
        var provider = new GodotTestCommandLineOptionsProvider();
        CommandLineOption headlessOption = provider.GetCommandLineOptions()
            .Single(o => o.Name == GodotTestCommandLineOptions.HeadlessOptionName);

        ValidationResult validation = await provider.ValidateOptionArgumentsAsync(headlessOption, ["unexpected"]);

        Assert.False(validation.IsValid);
        Assert.Contains("does not accept any arguments", validation.ErrorMessage, StringComparison.Ordinal);
    }

    private sealed class StubCommandLineOptions(IReadOnlyDictionary<string, string[]> options) : ICommandLineOptions
    {
        public bool IsOptionSet(string optionName) => options.ContainsKey(optionName);

        public bool TryGetOptionArgumentList(string optionName, out string[] arguments)
        {
            if (options.TryGetValue(optionName, out string[]? configuredArguments))
            {
                arguments = configuredArguments;
                return true;
            }

            arguments = [];
            return false;
        }
    }

    private sealed class SelectorFixtureA
    {
        public static void TargetMethod()
        {
        }

        public static void OtherMethod()
        {
        }
    }

    private sealed class SelectorFixtureB
    {
        public static void TargetMethod()
        {
        }
    }

    private sealed class UidFixtureA
    {
        public static void Target()
        {
        }

        public static void Target(int _)
        {
        }

        public static void GenericTarget<T>()
        {
        }
    }

    private sealed class UidFixtureB
    {
        public static void Target()
        {
        }
    }
}
