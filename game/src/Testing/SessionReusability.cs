namespace AlleyCat.Testing;

/// <summary>
/// Pure derivation of whether an integration-test session process may execute further tests after one
/// completed <c>run</c> command, per specs/testing/001-test-framework (Session Restart Semantics).
/// </summary>
internal static class SessionReusability
{
    /// <summary>
    /// A session stays reusable only when test-owned teardown succeeded and the session baseline was
    /// restored and validated. Teardown failure leaves CLR-static and native state that baseline
    /// validation cannot observe, so only a process restart clears it; an assertion failure alone with
    /// successful cleanup keeps the session reusable.
    /// </summary>
    /// <param name="executionResult">
    /// Captured lifecycle result of the executed test; <c>null</c> when a framework error prevented
    /// lifecycle capture, which contributes no teardown taint of its own.
    /// </param>
    /// <param name="baselineRestored">Whether the session baseline was restored and validated.</param>
    /// <returns><c>true</c> when the session may execute further tests; otherwise <c>false</c>.</returns>
    internal static bool IsSessionReusable(PerTestLifecycleExecutionResult? executionResult, bool baselineRestored)
        => executionResult?.TeardownFailure is null && baselineRestored;
}
