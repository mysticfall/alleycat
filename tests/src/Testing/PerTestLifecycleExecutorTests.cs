using System.Reflection;
using AlleyCat.Testing;
using Xunit;

namespace AlleyCat.Tests.Testing;

/// <summary>
/// Unit coverage for per-test lifecycle execution policy.
/// </summary>
public sealed class PerTestLifecycleExecutorTests
{
    /// <summary>
    /// Verifies async initialisation is invoked before the test body.
    /// </summary>
    [Fact]
    public async Task InitializeAsync_RunsBeforeTestBody()
    {
        LifecycleProbe.Reset();
        MethodInfo method = typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.AssertInitialised))!;

        PerTestLifecycleExecutionResult result = await PerTestLifecycleExecutor.ExecuteAsync(method);

        Assert.True(result.Passed);
        Assert.Equal(new[] { "constructor", "initialize", "test", "xunit-dispose", "async-dispose", "dispose" }, LifecycleProbe.Events);
    }

    /// <summary>
    /// Verifies teardown hooks run for successful test execution.
    /// </summary>
    [Fact]
    public async Task Teardown_RunsWhenTestPasses()
    {
        LifecycleProbe.Reset();
        MethodInfo method = typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.Pass))!;

        PerTestLifecycleExecutionResult result = await PerTestLifecycleExecutor.ExecuteAsync(method);

        Assert.True(result.Passed);
        Assert.Contains("xunit-dispose", LifecycleProbe.Events);
        Assert.Contains("async-dispose", LifecycleProbe.Events);
        Assert.Contains("dispose", LifecycleProbe.Events);
    }

    /// <summary>
    /// Verifies teardown still runs when the test body fails.
    /// </summary>
    [Fact]
    public async Task Teardown_RunsWhenTestFails()
    {
        LifecycleProbe.Reset();
        LifecycleProbe.ThrowFromTest = true;
        MethodInfo method = typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.Pass))!;

        PerTestLifecycleExecutionResult result = await PerTestLifecycleExecutor.ExecuteAsync(method);

        Assert.NotNull(result.TestFailure);
        Assert.Contains("xunit-dispose", LifecycleProbe.Events);
        Assert.Contains("async-dispose", LifecycleProbe.Events);
        Assert.Contains("dispose", LifecycleProbe.Events);
    }

    /// <summary>
    /// Verifies combined failures keep the body failure as primary with teardown diagnostics appended.
    /// </summary>
    [Fact]
    public async Task CombinedFailures_PreservePrimaryFailureAndAppendTeardownDiagnostics()
    {
        LifecycleProbe.Reset();
        LifecycleProbe.ThrowFromTest = true;
        LifecycleProbe.ThrowFromXunitDisposeAsync = true;
        MethodInfo method = typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.Pass))!;

        PerTestLifecycleExecutionResult result = await PerTestLifecycleExecutor.ExecuteAsync(method);
        (string message, string? stackTrace) = PerTestLifecycleExecutor.BuildFailureDiagnostics(result);

        Assert.NotNull(result.TestFailure);
        Assert.NotNull(result.TeardownFailure);
        Assert.StartsWith("Primary test failure.", message, StringComparison.Ordinal);
        Assert.Contains("Additional teardown failure", message, StringComparison.Ordinal);
        Assert.Contains("xUnit teardown failure.", message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(stackTrace));
    }

    /// <summary>
    /// Verifies static test methods execute without instance lifecycle hooks.
    /// </summary>
    [Fact]
    public async Task StaticMethod_DoesNotUseInstanceLifecycle()
    {
        StaticLifecycleProbe.Reset();
        MethodInfo method = typeof(StaticLifecycleProbe).GetMethod(nameof(StaticLifecycleProbe.StaticPass))!;

        PerTestLifecycleExecutionResult result = await PerTestLifecycleExecutor.ExecuteAsync(method);

        Assert.True(result.Passed);
        Assert.Equal(1, StaticLifecycleProbe.StaticTestCallCount);
        Assert.Equal(0, StaticLifecycleProbe.ConstructorCallCount);
        Assert.Equal(0, StaticLifecycleProbe.InitializeCallCount);
        Assert.Equal(0, StaticLifecycleProbe.DisposeAsyncCallCount);
        Assert.Equal(0, StaticLifecycleProbe.DisposeCallCount);
    }

    /// <summary>
    /// Verifies repeated executions construct a fresh fixture instance and run the full lifecycle each time,
    /// underpinning safe reuse of a session process across tests.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ConstructsFreshInstance_AndRunsFullLifecycle_PerInvocation()
    {
        LifecycleProbe.Reset();
        MethodInfo method = typeof(LifecycleProbe).GetMethod(nameof(LifecycleProbe.Pass))!;

        PerTestLifecycleExecutionResult first = await PerTestLifecycleExecutor.ExecuteAsync(method);
        PerTestLifecycleExecutionResult second = await PerTestLifecycleExecutor.ExecuteAsync(method);
        PerTestLifecycleExecutionResult third = await PerTestLifecycleExecutor.ExecuteAsync(method);

        Assert.True(first.Passed);
        Assert.True(second.Passed);
        Assert.True(third.Passed);

        Assert.Equal(3, LifecycleProbe.Instances.Count);
        Assert.Equal(3, LifecycleProbe.Instances.Distinct().Count());

        string[] expectedSequence = ["constructor", "initialize", "test", "xunit-dispose", "async-dispose", "dispose"];
        Assert.Equal([.. expectedSequence, .. expectedSequence, .. expectedSequence], LifecycleProbe.Events);
    }

    private sealed class LifecycleProbe : IAsyncLifetime, IAsyncDisposable, IDisposable
    {
        public static List<string> Events { get; } = [];

        public static List<LifecycleProbe> Instances { get; } = [];

        public static bool ThrowFromTest
        {
            get; set;
        }

        public static bool ThrowFromXunitDisposeAsync
        {
            get; set;
        }

        private bool _initialised;

        public LifecycleProbe()
        {
            Events.Add("constructor");
            Instances.Add(this);
        }

        public static void Reset()
        {
            Events.Clear();
            Instances.Clear();
            ThrowFromTest = false;
            ThrowFromXunitDisposeAsync = false;
        }

        public Task InitializeAsync()
        {
            _initialised = true;
            Events.Add("initialize");
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            Events.Add("xunit-dispose");

            return ThrowFromXunitDisposeAsync
                ? Task.FromException(new InvalidOperationException("xUnit teardown failure."))
                : Task.CompletedTask;
        }

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            Events.Add("async-dispose");
            return ValueTask.CompletedTask;
        }

        public void Dispose() => Events.Add("dispose");

        public void AssertInitialised()
        {
            Events.Add("test");

            if (!_initialised)
            {
                throw new InvalidOperationException("InitializeAsync did not run before test body.");
            }
        }

        public void Pass()
        {
            Events.Add("test");

            if (!_initialised)
            {
                throw new InvalidOperationException("InitializeAsync did not run before test body.");
            }

            if (ThrowFromTest)
            {
                throw new InvalidOperationException("Primary test failure.");
            }
        }
    }

    private sealed class StaticLifecycleProbe : IAsyncLifetime, IDisposable
    {
        public static int ConstructorCallCount
        {
            get; private set;
        }

        public static int InitializeCallCount
        {
            get; private set;
        }

        public static int DisposeAsyncCallCount
        {
            get; private set;
        }

        public static int DisposeCallCount
        {
            get; private set;
        }

        public static int StaticTestCallCount
        {
            get; private set;
        }

        public StaticLifecycleProbe()
        {
            ConstructorCallCount++;
            throw new InvalidOperationException("Constructor should not run for static methods.");
        }

        public static void Reset()
        {
            ConstructorCallCount = 0;
            InitializeCallCount = 0;
            DisposeAsyncCallCount = 0;
            DisposeCallCount = 0;
            StaticTestCallCount = 0;
        }

        public static void StaticPass() => StaticTestCallCount++;

        public Task InitializeAsync()
        {
            InitializeCallCount++;
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            DisposeAsyncCallCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => DisposeCallCount++;
    }
}
