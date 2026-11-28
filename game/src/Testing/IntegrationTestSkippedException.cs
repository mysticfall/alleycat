namespace AlleyCat.Testing;

/// <summary>
/// Thrown by an integration test that finds at run time that its precondition cannot hold in this environment, for
/// example a host-wide observation that another process would disturb. The session reports the test as
/// <c>skipped</c> with <see cref="Exception.Message" /> as the reason instead of as a failure.
/// </summary>
/// <remarks>
/// Throw it from the test body before the test changes any state; teardown still runs, and a teardown failure turns
/// the outcome into a failure. Theory rows are aggregated as failures, so only facts may skip themselves.
/// </remarks>
/// <param name="reason">Human-readable reason naming what was found.</param>
public sealed class IntegrationTestSkippedException(string reason) : Exception(reason);
