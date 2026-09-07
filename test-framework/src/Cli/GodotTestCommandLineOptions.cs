using System.Reflection;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace AlleyCat.TestFramework;

internal static class GodotTestCommandLineOptions
{
    public const string TestClassOptionName = "test-class";
    public const string TestMethodOptionName = "test-method";
    public const string HeadlessOptionName = "headless";

    private static readonly IReadOnlyCollection<CommandLineOption> _options =
    [
        new CommandLineOption(
            TestClassOptionName,
            "Run only tests whose declaring type exactly matches one of the comma-separated fully qualified class names.",
            ArgumentArity.ExactlyOne,
            isHidden: false),
        new CommandLineOption(
            TestMethodOptionName,
            "Run only the exact fully qualified test methods (Type.FullName.MethodName), comma-separated.",
            ArgumentArity.ExactlyOne,
            isHidden: false),
        new CommandLineOption(
            HeadlessOptionName,
            "Run all integration tests in headless mode. Overrides per-test HeadlessAttribute settings.",
            ArgumentArity.Zero,
            isHidden: false),
    ];

    public static IReadOnlyCollection<CommandLineOption> GetOptions() => _options;

    public static ValidationResult Validate(ICommandLineOptions commandLineOptions)
    {
        bool hasValidTestClass = TryGetSingleValue(
            commandLineOptions,
            TestClassOptionName,
            out string? classValue,
            out string? testClassErrorMessage);
        bool hasValidTestMethod = TryGetSingleValue(
            commandLineOptions,
            TestMethodOptionName,
            out string? methodValue,
            out string? testMethodErrorMessage);

        return !hasValidTestClass
            ? ValidationResult.Invalid(testClassErrorMessage!)
            : !hasValidTestMethod
            ? ValidationResult.Invalid(testMethodErrorMessage!)
            : classValue is not null && !SplitClassList(classValue).Any()
            ? ValidationResult.Invalid(
                $"Option '{ToCommandLineName(TestClassOptionName)}' must contain at least one fully qualified class name.")
            : methodValue is null || TryParseMethodList(methodValue, out _, out _)
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(
                $"Option '{ToCommandLineName(TestMethodOptionName)}' must be in format '<Fully.Qualified.TypeName>.<MethodName>'.");
    }

    public static GodotCliTestSelector Parse(ICommandLineOptions commandLineOptions)
    {
        _ = TryGetSingleValue(commandLineOptions, TestClassOptionName, out string? testClass, out _);
        _ = TryGetSingleValue(commandLineOptions, TestMethodOptionName, out string? testMethod, out _);

        return testMethod is not null
            && TryParseMethodList(testMethod, out string[] methodTypeNames, out string[] methodNames)
            ? GodotCliTestSelector.ForMethods(methodTypeNames, methodNames)
            : testClass is null
            ? GodotCliTestSelector.None
            : GodotCliTestSelector.ForClass([.. SplitClassList(testClass)]);
    }

    /// <summary>
    /// Splits one raw <c>--test-class</c> value into trimmed, non-empty class names.
    /// </summary>
    private static IEnumerable<string> SplitClassList(string rawClassList) =>
        rawClassList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Splits one raw comma-separated <c>--test-method</c> value into parallel type-name and method-name arrays.
    /// </summary>
    private static bool TryParseMethodList(string rawMethodList, out string[] methodTypeNames, out string[] methodNames)
    {
        string[] selectors = [.. rawMethodList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        methodTypeNames = new string[selectors.Length];
        methodNames = new string[selectors.Length];

        for (int index = 0; index < selectors.Length; index++)
        {
            if (!GodotCliTestSelector.TryParseMethod(selectors[index], out methodTypeNames[index], out methodNames[index]))
            {
                return false;
            }
        }

        return selectors.Length > 0;
    }

    private static bool TryGetSingleValue(
        ICommandLineOptions commandLineOptions,
        string optionName,
        out string? value,
        out string? errorMessage)
    {
        if (!commandLineOptions.TryGetOptionArgumentList(optionName, out string[]? arguments))
        {
            value = null;
            errorMessage = null;
            return true;
        }

        if (arguments is null || arguments.Length != 1)
        {
            value = null;
            errorMessage = $"Option '{ToCommandLineName(optionName)}' expects exactly one value.";
            return false;
        }

        string trimmedValue = arguments[0].Trim();
        if (string.IsNullOrWhiteSpace(trimmedValue))
        {
            value = null;
            errorMessage = $"Option '{ToCommandLineName(optionName)}' expects exactly one value.";
            return false;
        }

        value = trimmedValue;
        errorMessage = null;
        return true;
    }

    /// <summary>
    /// Checks whether the <c>--headless</c> CLI flag is set.
    /// </summary>
    public static bool IsHeadless(ICommandLineOptions commandLineOptions)
        => commandLineOptions.IsOptionSet(HeadlessOptionName);

    private static string ToCommandLineName(string optionName) => $"--{optionName}";
}

internal sealed class GodotTestCommandLineOptionsProvider : ICommandLineOptionsProvider
{
    public string Uid => "AlleyCat.TestFramework.GodotTestCommandLineOptionsProvider";

    public string Version => typeof(GodotTestCommandLineOptionsProvider).Assembly.GetName().Version?.ToString() ?? "1.0.0";

    public string DisplayName => "AlleyCat test selection options";

    public string Description => "Command-line options for selecting specific AlleyCat integration tests.";

    public IReadOnlyCollection<CommandLineOption> GetCommandLineOptions() => GodotTestCommandLineOptions.GetOptions();

    public Task<bool> IsEnabledAsync() => Task.FromResult(true);

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments)
    {
        return string.Equals(commandOption.Name, GodotTestCommandLineOptions.HeadlessOptionName, StringComparison.Ordinal)
            && arguments.Length > 0
            ? Task.FromResult(ValidationResult.Invalid(
                $"Option '--{GodotTestCommandLineOptions.HeadlessOptionName}' does not accept any arguments."))
            : ValidationResult.ValidTask;
    }

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions) =>
        Task.FromResult(GodotTestCommandLineOptions.Validate(commandLineOptions));
}

internal sealed record GodotCliTestSelector(
    string?[]? ClassNames,
    string?[]? MethodTypeNames,
    string?[]? MethodNames)
{
    public static GodotCliTestSelector None { get; } = new(null, null, null);

    public static GodotCliTestSelector ForClass(params string[] classNames) => new(classNames, null, null);

    public static GodotCliTestSelector ForMethod(string methodTypeName, string methodName) =>
        new(null, [methodTypeName], [methodName]);

    public static GodotCliTestSelector ForMethods(string[] methodTypeNames, string[] methodNames) =>
        new(null, methodTypeNames, methodNames);

    public bool Matches(MethodInfo method)
    {
        string? declaringTypeFullName = method.DeclaringType?.FullName;
        return declaringTypeFullName is not null
            && (MethodTypeNames is not null && MethodNames is not null
            ? MatchesMethod(declaringTypeFullName, method.Name)
            : ClassNames is null || ClassNames.Contains(declaringTypeFullName, StringComparer.Ordinal));
    }

    private bool MatchesMethod(string declaringTypeFullName, string methodName)
    {
        for (int index = 0; index < MethodTypeNames!.Length; index++)
        {
            if (string.Equals(declaringTypeFullName, MethodTypeNames[index], StringComparison.Ordinal)
                && index < MethodNames!.Length
                && string.Equals(methodName, MethodNames[index], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryParseMethod(string value, out string methodTypeName, out string methodName, out string? errorMessage)
    {
        if (!TryParseMethod(value, out methodTypeName, out methodName))
        {
            errorMessage =
                $"Method selector '{value}' must be in format '<Fully.Qualified.TypeName>.<MethodName>'.";
            return false;
        }

        errorMessage = null;
        return true;
    }

    public static bool TryParseMethod(string value, out string methodTypeName, out string methodName)
    {
        int separatorIndex = value.LastIndexOf('.');
        if (separatorIndex <= 0 || separatorIndex == value.Length - 1)
        {
            methodTypeName = string.Empty;
            methodName = string.Empty;
            return false;
        }

        methodTypeName = value[..separatorIndex];
        methodName = value[(separatorIndex + 1)..];
        return true;
    }
}
