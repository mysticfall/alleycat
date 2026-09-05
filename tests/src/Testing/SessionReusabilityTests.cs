using AlleyCat.Testing;
using Xunit;

namespace AlleyCat.Tests.Testing;

/// <summary>
/// Unit coverage for the session-reusability derivation following one executed session test.
/// </summary>
public sealed class SessionReusabilityTests
{
    /// <summary>
    /// Verifies a clean lifecycle with a restored baseline keeps the session reusable.
    /// </summary>
    [Fact]
    public void CleanLifecycle_AndRestoredBaseline_IsReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(testFailure: null, teardownFailure: null);

        Assert.True(SessionReusability.IsSessionReusable(result, baselineRestored: true));
    }

    /// <summary>
    /// Verifies an assertion failure with successful cleanup and a restored baseline still reuses the
    /// session, because only teardown or cleanup-validation failure taints it.
    /// </summary>
    [Fact]
    public void AssertionFailureWithCleanTeardown_AndRestoredBaseline_IsReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(
            testFailure: new InvalidOperationException("Assertion failed."),
            teardownFailure: null);

        Assert.True(SessionReusability.IsSessionReusable(result, baselineRestored: true));
    }

    /// <summary>
    /// Verifies a baseline-restoration failure taints the session even when the test lifecycle was clean.
    /// </summary>
    [Fact]
    public void CleanLifecycle_AndBaselineFailure_IsNotReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(testFailure: null, teardownFailure: null);

        Assert.False(SessionReusability.IsSessionReusable(result, baselineRestored: false));
    }

    /// <summary>
    /// Verifies a teardown failure taints the session even when the baseline validates, because
    /// test-owned cleanup failure leaves state baseline validation cannot observe.
    /// </summary>
    [Fact]
    public void TeardownFailure_AndRestoredBaseline_IsNotReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(
            testFailure: null,
            teardownFailure: new InvalidOperationException("DisposeAsync failed."));

        Assert.False(SessionReusability.IsSessionReusable(result, baselineRestored: true));
    }

    /// <summary>
    /// Verifies a teardown failure keeps the session tainted when the baseline also fails to restore.
    /// </summary>
    [Fact]
    public void TeardownFailure_AndBaselineFailure_IsNotReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(
            testFailure: null,
            teardownFailure: new InvalidOperationException("DisposeAsync failed."));

        Assert.False(SessionReusability.IsSessionReusable(result, baselineRestored: false));
    }

    /// <summary>
    /// Verifies a combined body-and-teardown failure taints the session even when the baseline validates.
    /// </summary>
    [Fact]
    public void CombinedBodyAndTeardownFailure_AndRestoredBaseline_IsNotReusable()
    {
        PerTestLifecycleExecutionResult result = CreateResult(
            testFailure: new InvalidOperationException("Assertion failed."),
            teardownFailure: new InvalidOperationException("DisposeAsync failed."));

        Assert.False(SessionReusability.IsSessionReusable(result, baselineRestored: true));
    }

    /// <summary>
    /// Verifies a framework error that prevented lifecycle capture adds no teardown taint of its own,
    /// so baseline restoration alone decides.
    /// </summary>
    [Fact]
    public void MissingLifecycleResult_AndRestoredBaseline_IsReusable()
        => Assert.True(SessionReusability.IsSessionReusable(executionResult: null, baselineRestored: true));

    /// <summary>
    /// Verifies a framework error that prevented lifecycle capture still taints the session when the
    /// baseline fails to restore.
    /// </summary>
    [Fact]
    public void MissingLifecycleResult_AndBaselineFailure_IsNotReusable()
        => Assert.False(SessionReusability.IsSessionReusable(executionResult: null, baselineRestored: false));

    private static PerTestLifecycleExecutionResult CreateResult(Exception? testFailure, Exception? teardownFailure)
        => new(testFailure, teardownFailure);
}
