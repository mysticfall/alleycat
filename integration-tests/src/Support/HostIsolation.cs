using AlleyCat.TestFramework;
using AlleyCat.Testing;

namespace AlleyCat.IntegrationTests.Support;

/// <summary>
/// Start-of-test guard for isolated tests that observe process activity across the whole host (TEST-001 TR-35).
/// </summary>
internal static class HostIsolation
{
    /// <summary>
    /// Skips the calling test, naming what was found, when a Godot process the framework did not launch is running on
    /// the host. On a clean host it returns and the test runs fully. Call it first, before the test changes any state.
    /// </summary>
    /// <exception cref="IntegrationTestSkippedException">Thrown when another Godot process is running.</exception>
    public static void RequireNoOtherGodotProcess()
    {
        if (HostGodotProcesses.DescribeOthers() is { } found)
        {
            throw new IntegrationTestSkippedException(
                $"This test observes process activity across the whole host and needs no other Godot process running, but found: {found}.");
        }
    }
}
