using Godot;

namespace AlleyCat.Testing;

/// <summary>
/// Provides runtime-context checks for host-launched integration-test processes.
/// </summary>
public static class RuntimeContext
{
    private const string TestScenePrefix = "res://tests/";

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
        System.Environment.GetEnvironmentVariable(IntegrationTestContextEnvironmentVariable),
        System.Environment.GetCommandLineArgs());

    /// <summary>
    /// Returns <c>true</c> when global startup should be bypassed for the active scene.
    /// </summary>
    public static bool ShouldBypassGlobalStartup(SceneTree tree)
    {
        string configuredMainScenePath = ResolveMainScenePath(ProjectSettings.GetSetting("application/run/main_scene").AsString());
        return ShouldBypassGlobalStartup(
            System.Environment.GetEnvironmentVariable(IntegrationTestContextEnvironmentVariable),
            System.Environment.GetCommandLineArgs(),
            ResolveStartupScenePath(tree, configuredMainScenePath),
            configuredMainScenePath);
    }

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

    internal static bool ShouldBypassGlobalStartup(
        string? explicitContext,
        IReadOnlyList<string>? commandLineArguments,
        string? currentScenePath,
        string? configuredMainScenePath)
        => IsIntegrationTest(explicitContext, commandLineArguments)
            || (!string.IsNullOrWhiteSpace(currentScenePath)
                && !string.IsNullOrWhiteSpace(configuredMainScenePath)
                && currentScenePath.StartsWith(TestScenePrefix, StringComparison.Ordinal)
                && !string.Equals(currentScenePath, configuredMainScenePath, StringComparison.Ordinal));

    private static string ResolveMainScenePath(string configuredMainScenePath)
        => string.IsNullOrWhiteSpace(configuredMainScenePath)
            ? string.Empty
            : GD.Load<PackedScene>(configuredMainScenePath)?.ResourcePath ?? configuredMainScenePath;

    private static string? ResolveStartupScenePath(SceneTree tree, string configuredMainScenePath)
    {
        if (!string.IsNullOrWhiteSpace(tree.CurrentScene?.SceneFilePath))
        {
            return tree.CurrentScene.SceneFilePath;
        }

        Window root = tree.Root;
        for (int index = 0; index < root.GetChildCount(); index++)
        {
            if (root.GetChild(index) is not Node child)
            {
                continue;
            }

            string sceneFilePath = child.SceneFilePath;
            if (!string.IsNullOrWhiteSpace(sceneFilePath)
                && sceneFilePath.StartsWith(TestScenePrefix, StringComparison.Ordinal)
                && !string.Equals(sceneFilePath, configuredMainScenePath, StringComparison.Ordinal))
            {
                return sceneFilePath;
            }
        }

        return null;
    }
}
