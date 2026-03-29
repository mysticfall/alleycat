using System.Reflection;
using System.Text.Json;
using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Behaviour-focused tests for key Godot test framework logic.
/// </summary>
public sealed class GodotTestFrameworkTests
{
    private const string ResultMarkerPrefix = "ALLEYCAT_INTEGRATION_TEST_RESULT:";
    private static readonly Type _godotTestFrameworkType = typeof(TestingPlatformBuilderHook).Assembly
        .GetType("AlleyCat.TestFramework.GodotTestFramework", throwOnError: true)!;
    private static readonly Lock _environmentLock = new();

    /// <summary>
    /// Ensures the Godot binary path defaults to <c>godot-mono</c>.
    /// </summary>
    [Fact]
    public void ResolveGodotBinaryPath_ReturnsDefault_WhenEnvironmentVariableIsUnset()
    {
        const string environmentVariable = "GODOT_BIN";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, null);

            string resolvedPath = InvokePrivateStatic<string>("ResolveGodotBinaryPath");

            Assert.Equal("godot-mono", resolvedPath);
        }
    }

    /// <summary>
    /// Ensures the configured Godot binary path is honoured.
    /// </summary>
    [Fact]
    public void ResolveGodotBinaryPath_ReturnsConfiguredValue_WhenEnvironmentVariableIsSet()
    {
        const string environmentVariable = "GODOT_BIN";
        const string expectedPath = "custom-godot";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, expectedPath);

            string resolvedPath = InvokePrivateStatic<string>("ResolveGodotBinaryPath");

            Assert.Equal(expectedPath, resolvedPath);
        }
    }

    /// <summary>
    /// Ensures timeout values fall back unless explicitly configured to a positive integer.
    /// </summary>
    [Theory]
    [InlineData("1200", 500, 1200)]
    [InlineData("0", 500, 500)]
    [InlineData("-15", 500, 500)]
    [InlineData("not-a-number", 500, 500)]
    public void ResolveTimeout_UsesPositiveConfiguredValue_OtherwiseFallsBack(string configuredValue, int fallbackMs, int expectedMs)
    {
        const string environmentVariable = "ALLEYCAT_FRAMEWORK_TEST_TIMEOUT_MS";

        lock (_environmentLock)
        {
            using var _ = new EnvironmentVariableScope(environmentVariable, configuredValue);

            int resolvedTimeout = InvokePrivateStatic<int>("ResolveTimeout", environmentVariable, fallbackMs);

            Assert.Equal(expectedMs, resolvedTimeout);
        }
    }

    /// <summary>
    /// Ensures only parameterless <see cref="FactAttribute"/> methods are considered supported.
    /// </summary>
    [Fact]
    public void IsSupportedFactMethod_OnlyAcceptsParameterlessFactMethods()
    {
        MethodInfo supportedMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(BuildStructuredErrorMessage_UsesFallbackMessage_WhenNoDetailsAreProvided))!;
        MethodInfo parameterisedFactMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(ResolveTimeout_UsesPositiveConfiguredValue_OtherwiseFallsBack))!;
        MethodInfo nonFactMethod = typeof(GodotTestFrameworkTests)
            .GetMethod(nameof(HelperMethodWithoutTestAttribute), BindingFlags.NonPublic | BindingFlags.Static)!;

        bool isSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", supportedMethod);
        bool isParameterisedSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", parameterisedFactMethod);
        bool isNonFactSupported = InvokePrivateStatic<bool>("IsSupportedFactMethod", nonFactMethod);

        Assert.True(isSupported);
        Assert.False(isParameterisedSupported);
        Assert.False(isNonFactSupported);
    }

    /// <summary>
    /// Ensures the parser consumes the most recent structured run-fact line.
    /// </summary>
    [Fact]
    public void TryParseStructuredRunFactResult_ParsesTheLastStructuredResultLine()
    {
        string failedPayload = JsonSerializer.Serialize(new
        {
            Outcome = "failed",
            Message = "first",
            Stack = "trace-1"
        });
        string passedPayload = JsonSerializer.Serialize(new
        {
            Outcome = "passed",
            Message = (string?)null,
            Stack = (string?)null
        });

        string[] outputLines =
        [
            "non-structured-line",
            ResultMarkerPrefix + failedPayload,
            ResultMarkerPrefix + passedPayload,
        ];

        object? parsedResult = InvokePrivateStatic<object?>("TryParseStructuredRunFactResult", (object)outputLines);

        Assert.NotNull(parsedResult);
        Assert.Equal("passed", ReadStructuredResultProperty(parsedResult!, "Outcome"));
        Assert.Null(ReadStructuredResultProperty(parsedResult!, "Message"));
        Assert.Null(ReadStructuredResultProperty(parsedResult!, "Stack"));
    }

    /// <summary>
    /// Ensures malformed structured run-fact payloads are rejected.
    /// </summary>
    [Fact]
    public void TryParseStructuredRunFactResult_ReturnsNull_WhenStructuredPayloadIsInvalidJson()
    {
        string[] outputLines =
        [
            $"{ResultMarkerPrefix}{{not-valid-json}}",
        ];

        object? parsedResult = InvokePrivateStatic<object?>("TryParseStructuredRunFactResult", (object)outputLines);

        Assert.Null(parsedResult);
    }

    /// <summary>
    /// Ensures message and stack values are combined into one failure string.
    /// </summary>
    [Fact]
    public void BuildStructuredErrorMessage_CombinesMessageAndStack_WhenBothArePresent()
    {
        object structuredResult = CreateStructuredResult("failed", "Assertion failed", "stack trace");

        string message = InvokePrivateStatic<string>("BuildStructuredErrorMessage", structuredResult);

        Assert.Equal($"Assertion failed{Environment.NewLine}stack trace", message);
    }

    /// <summary>
    /// Ensures a fallback failure message is returned when no details are present.
    /// </summary>
    [Fact]
    public void BuildStructuredErrorMessage_UsesFallbackMessage_WhenNoDetailsAreProvided()
    {
        object structuredResult = CreateStructuredResult("failed", " ", "\t");

        string message = InvokePrivateStatic<string>("BuildStructuredErrorMessage", structuredResult);

        Assert.Equal("Godot run-fact reported an unknown failure.", message);
    }

    private static T InvokePrivateStatic<T>(string methodName, params object?[] args)
    {
        MethodInfo method = _godotTestFrameworkType.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(_godotTestFrameworkType.FullName, methodName);

        object? result = method.Invoke(null, args);
        return (T)result!;
    }

    private static object CreateStructuredResult(string outcome, string? message, string? stack)
    {
        Type structuredResultType = _godotTestFrameworkType.GetNestedType("StructuredRunFactResult", BindingFlags.NonPublic)
            ?? throw new MissingMemberException(_godotTestFrameworkType.FullName, "StructuredRunFactResult");

        return Activator.CreateInstance(structuredResultType, outcome, message, stack)
            ?? throw new InvalidOperationException("Failed to construct structured result instance.");
    }

    private static object? ReadStructuredResultProperty(object structuredResult, string propertyName)
    {
        PropertyInfo property = structuredResult.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(structuredResult.GetType().FullName, propertyName);
        return property.GetValue(structuredResult);
    }

    private static void HelperMethodWithoutTestAttribute()
    {
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _variableName;
        private readonly string? _previousValue;

        public EnvironmentVariableScope(string variableName, string? value)
        {
            _variableName = variableName;
            _previousValue = Environment.GetEnvironmentVariable(variableName);
            Environment.SetEnvironmentVariable(variableName, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_variableName, _previousValue);
    }

}
