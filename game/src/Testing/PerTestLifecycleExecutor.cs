using System.Reflection;

#pragma warning disable IDE0130
namespace AlleyCat.Testing;
#pragma warning restore IDE0130

/// <summary>
/// Executes a parameterless test method with per-test lifecycle support.
/// </summary>
public static class PerTestLifecycleExecutor
{
    private const string XunitAsyncLifetimeInterfaceName = "Xunit.IAsyncLifetime";
    private const string InitializeAsyncMethodName = "InitializeAsync";
    private const string DisposeAsyncMethodName = "DisposeAsync";

    /// <summary>
    /// Runs constructor/setup/test/teardown for the provided method and returns captured failures.
    /// </summary>
    public static async Task<PerTestLifecycleExecutionResult> ExecuteAsync(MethodInfo method)
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

            object? invocationResult = method.Invoke(instance, null);
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
    /// Builds run-fact failure details where the test-body exception remains primary.
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
