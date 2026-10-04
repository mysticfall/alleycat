using System.Reflection;

#pragma warning disable IDE0130
namespace AlleyCat.Testing;
#pragma warning restore IDE0130

/// <summary>
/// Executes a parameterless test method, or every data row of a parameterised theory method, with per-test
/// lifecycle support.
/// </summary>
public static class PerTestLifecycleExecutor
{
    private const string XunitAsyncLifetimeInterfaceName = "Xunit.IAsyncLifetime";
    private const string XunitDataAttributeTypeName = "Xunit.Sdk.DataAttribute";
    private const string GetDataMethodName = "GetData";
    private const string SkipPropertyName = "Skip";
    private const string InitializeAsyncMethodName = "InitializeAsync";
    private const string DisposeAsyncMethodName = "DisposeAsync";

    /// <summary>
    /// Runs constructor/setup/test/teardown for the provided method and returns captured failures. A method
    /// with parameters is executed as a theory: each non-skipped xUnit data row (for example
    /// <c>InlineData</c> or <c>MemberData</c>) runs with a fresh instance and full lifecycle, and row failures
    /// are aggregated into one result that names every failing row.
    /// </summary>
    public static async Task<PerTestLifecycleExecutionResult> ExecuteAsync(MethodInfo method)
        => method.GetParameters().Length == 0
            ? await ExecuteRowAsync(method, arguments: null)
            : await ExecuteTheoryAsync(method);

    /// <summary>
    /// Returns whether the method carries at least one xUnit data attribute, identifying it as an executable
    /// theory without requiring an xUnit reference from the game assembly.
    /// </summary>
    public static bool HasTheoryData(MethodInfo method)
        => method.GetCustomAttributes(inherit: true).Any(IsXunitDataAttribute);

    private static async Task<PerTestLifecycleExecutionResult> ExecuteTheoryAsync(MethodInfo method)
    {
        IReadOnlyList<object?[]> rows;
        try
        {
            rows = ResolveTheoryRows(method);
        }
        catch (Exception ex)
        {
            return new PerTestLifecycleExecutionResult(UnwrapInvocationException(ex), TeardownFailure: null);
        }

        var testFailures = new List<(int Row, object?[] Arguments, Exception Failure)>();
        var teardownFailures = new List<(int Row, object?[] Arguments, Exception Failure)>();
        for (int row = 0; row < rows.Count; row++)
        {
            object?[] arguments = rows[row];
            PerTestLifecycleExecutionResult rowResult;
            try
            {
                rowResult = await ExecuteRowAsync(method, BindArguments(method, arguments));
            }
            catch (Exception ex)
            {
                rowResult = new PerTestLifecycleExecutionResult(ex, TeardownFailure: null);
            }

            if (rowResult.TestFailure is { } testFailure)
            {
                testFailures.Add((row, arguments, testFailure));
            }

            if (rowResult.TeardownFailure is { } teardownFailure)
            {
                teardownFailures.Add((row, arguments, teardownFailure));
            }
        }

        return new PerTestLifecycleExecutionResult(
            AggregateRowFailures(method, rows.Count, testFailures),
            AggregateRowFailures(method, rows.Count, teardownFailures));
    }

    private static IReadOnlyList<object?[]> ResolveTheoryRows(MethodInfo method)
    {
        if (method.ContainsGenericParameters)
        {
            throw new NotSupportedException(
                $"Generic theory '{method.DeclaringType?.FullName}.{method.Name}' is not supported by the integration runner.");
        }

        var rows = new List<object?[]>();
        bool hasDataAttribute = false;
        foreach (object attribute in method.GetCustomAttributes(inherit: true))
        {
            if (!IsXunitDataAttribute(attribute))
            {
                continue;
            }

            hasDataAttribute = true;
            Type attributeType = attribute.GetType();
            if (attributeType.GetProperty(SkipPropertyName)?.GetValue(attribute) is string { Length: > 0 })
            {
                continue;
            }

            MethodInfo getData = attributeType.GetMethod(GetDataMethodName, [typeof(MethodInfo)])
                ?? throw new MissingMethodException(attributeType.FullName, GetDataMethodName);
            if (getData.Invoke(attribute, [method]) is not IEnumerable<object?[]> data)
            {
                throw new InvalidOperationException(
                    $"Data attribute '{attributeType.FullName}' returned no data for '{method.DeclaringType?.FullName}.{method.Name}'.");
            }

            rows.AddRange(data);
        }

        return !hasDataAttribute
            ? throw new InvalidOperationException(
                $"Parameterised test '{method.DeclaringType?.FullName}.{method.Name}' has no xUnit data attribute.")
            : rows;
    }

    private static object?[] BindArguments(MethodInfo method, object?[] arguments)
    {
        ParameterInfo[] parameters = method.GetParameters();
        if (arguments.Length > parameters.Length)
        {
            throw new ArgumentException(
                $"Theory data row supplies {arguments.Length} values for {parameters.Length} parameters.");
        }

        object?[] bound = new object?[parameters.Length];
        for (int index = 0; index < parameters.Length; index++)
        {
            bound[index] = index < arguments.Length
                ? arguments[index]
                : parameters[index].HasDefaultValue
                    ? parameters[index].DefaultValue
                    : throw new ArgumentException(
                        $"Theory data row supplies {arguments.Length} values for {parameters.Length} parameters.");
        }

        return bound;
    }

    private static Exception? AggregateRowFailures(
        MethodInfo method,
        int rowCount,
        List<(int Row, object?[] Arguments, Exception Failure)> failures)
    {
        if (failures.Count == 0)
        {
            return null;
        }

        var message = new System.Text.StringBuilder();
        _ = message.Append(
            $"{failures.Count} of {rowCount} theory rows of '{method.DeclaringType?.FullName}.{method.Name}' failed.");
        foreach ((int row, object?[] arguments, Exception failure) in failures)
        {
            _ = message.AppendLine();
            _ = message.Append($"[row {row}] ({FormatArguments(arguments)}): {failure.Message}");
        }

        return new TheoryRowFailureException(message.ToString(), failures[0].Failure);
    }

    private static string FormatArguments(object?[] arguments)
        => string.Join(", ", arguments.Select(argument => argument switch
        {
            null => "null",
            string text => $"\"{text}\"",
            IFormattable formattable => formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => argument.ToString() ?? string.Empty,
        }));

    private static bool IsXunitDataAttribute(object attribute)
    {
        for (Type? type = attribute.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, XunitDataAttributeTypeName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<PerTestLifecycleExecutionResult> ExecuteRowAsync(MethodInfo method, object?[]? arguments)
    {
        object? instance = null;
        Exception? testFailure = null;
        Exception? teardownFailure = null;

        try
        {
            if (!method.IsStatic)
            {
                instance = Activator.CreateInstance(method.DeclaringType!);
                if (instance is null)
                {
                    throw new InvalidOperationException($"Unable to instantiate test class '{method.DeclaringType!.FullName}'.");
                }

                await RunInitializeAsyncIfPresent(instance);
            }

            object? invocationResult = method.Invoke(instance, arguments);
            await AwaitResultIfNeededAsync(invocationResult);
        }
        catch (Exception ex)
        {
            testFailure = UnwrapInvocationException(ex);
        }
        finally
        {
            if (instance is not null)
            {
                teardownFailure = await RunTeardownAsync(instance);
            }
        }

        return new PerTestLifecycleExecutionResult(testFailure, teardownFailure);
    }

    /// <summary>
    /// Builds failure details where the test-body exception remains primary.
    /// </summary>
    public static (string Message, string? StackTrace) BuildFailureDiagnostics(PerTestLifecycleExecutionResult result)
    {
        if (result.TestFailure is null && result.TeardownFailure is null)
        {
            throw new InvalidOperationException("Cannot build failure diagnostics for a successful execution result.");
        }

        if (result.TestFailure is not null && result.TeardownFailure is not null)
        {
            return (
                $"{result.TestFailure.Message}{Environment.NewLine}Additional teardown failure: {result.TeardownFailure.Message}",
                BuildCombinedStackTrace(result.TestFailure.StackTrace, result.TeardownFailure.StackTrace));
        }

        Exception failure = result.TestFailure ?? result.TeardownFailure!;
        return (failure.Message, failure.StackTrace);
    }

    private static string? BuildCombinedStackTrace(string? testFailureStack, string? teardownFailureStack)
        => string.IsNullOrWhiteSpace(testFailureStack)
            ? teardownFailureStack
            : string.IsNullOrWhiteSpace(teardownFailureStack)
            ? testFailureStack
            : $"{testFailureStack}{Environment.NewLine}--- Additional teardown stack trace ---{Environment.NewLine}{teardownFailureStack}";

    private static async Task RunInitializeAsyncIfPresent(object instance)
    {
        Type? asyncLifetimeType = GetXunitAsyncLifetimeInterface(instance.GetType());
        if (asyncLifetimeType is null)
        {
            return;
        }

        MethodInfo initializeAsync = asyncLifetimeType.GetMethod(InitializeAsyncMethodName, Type.EmptyTypes)
            ?? throw new MissingMethodException(asyncLifetimeType.FullName, InitializeAsyncMethodName);

        object? initializeResult = initializeAsync.Invoke(instance, null);
        await AwaitResultIfNeededAsync(initializeResult);
    }

    private static async Task<Exception?> RunTeardownAsync(object instance)
    {
        var failures = new List<Exception>(capacity: 3);

        await TryRunTeardownStepAsync(failures, async () =>
        {
            Type? asyncLifetimeType = GetXunitAsyncLifetimeInterface(instance.GetType());
            if (asyncLifetimeType is null)
            {
                return;
            }

            MethodInfo disposeAsyncMethod = asyncLifetimeType.GetMethod(DisposeAsyncMethodName, Type.EmptyTypes)
                ?? throw new MissingMethodException(asyncLifetimeType.FullName, DisposeAsyncMethodName);
            object? disposeResult = disposeAsyncMethod.Invoke(instance, null);
            await AwaitResultIfNeededAsync(disposeResult);
        });

        await TryRunTeardownStepAsync(failures, async () =>
        {
            if (instance is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        });

        try
        {
            if (instance is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception ex)
        {
            failures.Add(UnwrapInvocationException(ex));
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException("Multiple teardown failures were observed.", failures)
        };
    }

    private static async Task TryRunTeardownStepAsync(List<Exception> failures, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (Exception ex)
        {
            failures.Add(UnwrapInvocationException(ex));
        }
    }

    private static Type? GetXunitAsyncLifetimeInterface(Type type)
        => type.GetInterfaces().FirstOrDefault(interfaceType =>
            string.Equals(interfaceType.FullName, XunitAsyncLifetimeInterfaceName, StringComparison.Ordinal));

    private static async Task AwaitResultIfNeededAsync(object? result)
    {
        switch (result)
        {
            case null:
                return;
            case Task task:
                await task;
                return;
            case ValueTask valueTask:
                await valueTask;
                return;
            default:
                return;
        }
    }

    private static Exception UnwrapInvocationException(Exception exception)
        => exception is TargetInvocationException { InnerException: not null } invocationException
            ? invocationException.InnerException!
            : exception;
}

/// <summary>
/// Aggregated failure of one or more theory data rows; the stack trace is that of the first failing row.
/// </summary>
/// <param name="message">Summary naming every failing row and its arguments.</param>
/// <param name="firstFailure">Failure of the first failing row.</param>
public sealed class TheoryRowFailureException(string message, Exception firstFailure) : Exception(message, firstFailure)
{
    /// <inheritdoc/>
    public override string? StackTrace => InnerException?.StackTrace;
}

/// <summary>
/// Captured result of per-test lifecycle execution.
/// </summary>
/// <param name="TestFailure">Failure thrown by constructor/setup/test invocation.</param>
/// <param name="TeardownFailure">Failure thrown during teardown.</param>
public sealed record PerTestLifecycleExecutionResult(Exception? TestFailure, Exception? TeardownFailure)
{
    /// <summary>
    /// Indicates whether test invocation and teardown both succeeded.
    /// </summary>
    public bool Passed => TestFailure is null && TeardownFailure is null;
}
