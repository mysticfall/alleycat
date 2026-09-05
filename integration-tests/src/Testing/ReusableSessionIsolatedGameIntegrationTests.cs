using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Runtime-side coverage for the isolated-<c>Game</c> session policy: <c>TestRuntimeRunner</c>
/// frees the <c>Global</c> autoload root child before every test whose type name is listed in
/// <c>RequiresIsolatedGameSingleton</c> (this class is on that list), so the test body executes
/// without the <c>Global</c> autoload while the hosting runner stays alive. Baseline restoration
/// re-creates a fresh <c>Global</c> afterwards, verified by
/// <see cref="ReusableSessionIntegrationTests.IsolatedGameChecker_WhenIsolatedSiblingAlreadyRan_FreshGlobalAutoloadIsRestored"/>
/// through the shared session static.
/// </summary>
[Headless]
public sealed class ReusableSessionIsolatedGameIntegrationTests
{
    private const string GlobalAutoloadName = "Global";
    private const string TestRuntimeRunnerAutoloadName = "TestRuntimeRunner";

    /// <summary>
    /// Verifies this test runs without the <c>Global</c> autoload root child while the session
    /// runner itself remains in the tree, then flags completion for the sibling checker test.
    /// </summary>
    [Fact]
    public void IsolatedGameTest_RunsWithoutGlobalAutoload()
    {
        SceneTree tree = GetSceneTree();

        Assert.Null(tree.Root.GetNodeOrNull(GlobalAutoloadName));
        Assert.DoesNotContain(tree.Root.GetChildren(), child => child is Game);
        Assert.NotNull(tree.Root.GetNodeOrNull(TestRuntimeRunnerAutoloadName));

        ReusableSessionCoordination.IsolatedGameTestCompleted = true;
    }
}
