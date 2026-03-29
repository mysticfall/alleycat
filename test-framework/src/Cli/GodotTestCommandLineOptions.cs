using System.Reflection;
using Microsoft.Testing.Platform.CommandLine;
using Microsoft.Testing.Platform.Extensions;
using Microsoft.Testing.Platform.Extensions.CommandLine;

namespace AlleyCat.TestFramework;

internal static class GodotTestCommandLineOptions
{
    public const string TestClassOptionName = "test-class";
    public const string TestMethodOptionName = "test-method";

    private static readonly IReadOnlyCollection<CommandLineOption> _options =
    [
        new CommandLineOption(
            TestClassOptionName,
            "Run only tests whose declaring type exactly matches a fully qualified class name.",
            ArgumentArity.ExactlyOne,
            isHidden: false),
        new CommandLineOption(
            TestMethodOptionName,
            "Run only the exact fully qualified test method (Type.FullName.MethodName).",
            ArgumentArity.ExactlyOne,
            isHidden: false),
    ];

    public static IReadOnlyCollection<CommandLineOption> GetOptions() => _options;

    public static ValidationResult Validate(ICommandLineOptions commandLineOptions)
    {
        bool hasValidTestClass = TryGetSingleValue(
            commandLineOptions,
            TestClassOptionName,
            out _,
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
            : methodValue is null
            ? ValidationResult.Valid()
            : GodotCliTestSelector.TryParseMethod(methodValue, out _, out _)
            ? ValidationResult.Valid()
            : ValidationResult.Invalid(
                $"Option '{ToCommandLineName(TestMethodOptionName)}' must be in format '<Fully.Qualified.TypeName>.<MethodName>'.");
    }

    public static GodotCliTestSelector Parse(ICommandLineOptions commandLineOptions)
    {
        _ = TryGetSingleValue(commandLineOptions, TestClassOptionName, out string? testClass, out _);
        _ = TryGetSingleValue(commandLineOptions, TestMethodOptionName, out string? testMethod, out _);

        return testMethod is not null
            && GodotCliTestSelector.TryParseMethod(testMethod, out string methodTypeName, out string methodName)
            ? GodotCliTestSelector.ForMethod(methodTypeName, methodName)
            : testClass is null
            ? GodotCliTestSelector.None
            : GodotCliTestSelector.ForClass(testClass);
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

    public Task<ValidationResult> ValidateOptionArgumentsAsync(CommandLineOption commandOption, string[] arguments) =>
        ValidationResult.ValidTask;

    public Task<ValidationResult> ValidateCommandLineOptionsAsync(ICommandLineOptions commandLineOptions) =>
        Task.FromResult(GodotTestCommandLineOptions.Validate(commandLineOptions));
}

internal sealed record GodotCliTestSelector(string? ClassName, string? MethodTypeName, string? MethodName)
{
    public static GodotCliTestSelector None { get; } = new(null, null, null);

    public static GodotCliTestSelector ForClass(string className) => new(className, null, null);

    public static GodotCliTestSelector ForMethod(string methodTypeName, string methodName) =>
        new(null, methodTypeName, methodName);

    public bool Matches(MethodInfo method)
    {
        string? declaringTypeFullName = method.DeclaringType?.FullName;
        return declaringTypeFullName is not null
            && (MethodTypeName is not null && MethodName is not null
            ? string.Equals(declaringTypeFullName, MethodTypeName, StringComparison.Ordinal)
              && string.Equals(method.Name, MethodName, StringComparison.Ordinal)
            : ClassName is null || string.Equals(declaringTypeFullName, ClassName, StringComparison.Ordinal));
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
