namespace AlleyCat.Testing;

/// <summary>
/// Provides runtime-context checks for host-launched integration-test processes.
/// </summary>
public static class RuntimeContext
{
    /// <summary>
    /// Environment variable carrying the explicit runtime-context value.
    /// </summary>
    public const string IntegrationTestContextEnvironmentVariable = "ALLEYCAT_RUNTIME_CONTEXT";

    /// <summary>
    /// Explicit runtime-context value used for integration-test processes.
    /// </summary>
    public const string IntegrationTestContextValue = "integration-test";

    private const string ProbeCommandArg = "--integration-probe";
    private const string RunFactCommandArg = "--integration-run-fact";

    /// <summary>
    /// Returns <c>true</c> when the current process is running integration tests.
    /// </summary>
    public static bool IsIntegrationTest() => IsIntegrationTest(
        Environment.GetEnvironmentVariable(IntegrationTestContextEnvironmentVariable),
        Environment.GetCommandLineArgs());

    /// <summary>
    /// Returns <c>true</c> when the provided runtime context indicates integration-test execution.
    /// </summary>
    public static bool IsIntegrationTest(string? explicitContext, IReadOnlyList<string>? commandLineArguments)
    {
        if (string.Equals(explicitContext, IntegrationTestContextValue, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (commandLineArguments is null)
        {
            return false;
        }

        for (int index = 0; index < commandLineArguments.Count; index++)
        {
            string argument = commandLineArguments[index];
            if (string.Equals(argument, ProbeCommandArg, StringComparison.Ordinal)
                || string.Equals(argument, RunFactCommandArg, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
