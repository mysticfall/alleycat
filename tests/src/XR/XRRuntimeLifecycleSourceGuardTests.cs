using Xunit;

namespace AlleyCat.Tests.XR;

/// <summary>
/// Guards XR runtime scene processing configuration without requiring the Godot test host.
/// </summary>
public sealed class XRRuntimeLifecycleSourceGuardTests
{
    /// <summary>
    /// Verifies each runtime root uses Godot's Always process mode rather than Disabled.
    /// </summary>
    /// <param name="sceneFileName">Runtime scene file under <c>game/assets/xr</c>.</param>
    [Theory]
    [InlineData("openxr_runtime.tscn")]
    [InlineData("mock_runtime.tscn")]
    public void RuntimeSceneRootAlwaysProcesses(string sceneFileName)
    {
        string scene = File.ReadAllText(RepositoryPath.Get("game", "assets", "xr", sceneFileName));

        Assert.Contains("process_mode = 3", scene, StringComparison.Ordinal);
        Assert.DoesNotContain("process_mode = 4", scene, StringComparison.Ordinal);
    }
}
