using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Runtime-side contract coverage for the reusable integration-test session model
/// (specs/testing/001-test-framework, Session Execution Model). Every test in this class executes
/// inside the same reusable headless session process, which is precisely the arrangement under
/// test.
/// </summary>
/// <remarks>
/// <para>
/// Tests never rely on <see cref="FactAttribute"/> execution order: coordinating tests detect
/// through the session statics in <see cref="ReusableSessionCoordination"/> whether their sibling
/// has already run in this session. Whichever test runs second carries the verification duty; a
/// test that runs first passes trivially after recording its visit.
/// </para>
/// <para>
/// Deliberate state doctrine: the polluting test dirties only runtime-owned process state (root
/// children, current scene, engine time scale, tree pause), which the session baseline restoration
/// owns undoing. The coordination statics are test-owned CLR state the framework does not reset;
/// they persist by design to prove the documented no-CLR-isolation contract and are inert outside
/// these tests.
/// </para>
/// </remarks>
[Headless]
public sealed class ReusableSessionIntegrationTests
{
    private const string GlobalAutoloadName = "Global";
    private const string PollutionProbeRootChildName = "ReusableSessionPollutionProbe";
    private const string PollutedSceneRootChildName = "ReusableSessionPollutedScene";
    private const string PollutedScenePath = "res://assets/testing/test_environment.tscn";
    private const double PollutedTimeScale = 0.25;

    /// <summary>
    /// Records one fixture-instance identity per construction so coordinating tests can prove the
    /// session constructs a fresh class instance for every test.
    /// </summary>
    public ReusableSessionIntegrationTests()
    {
        // Fresh-fixture proof: the session constructs one class instance per test, so recording one
        // GUID per construction lets the second-to-run coordinating test observe at least two
        // distinct fixture instances.
        ReusableSessionCoordination.FixtureInstanceIds.Add(Guid.NewGuid());
    }

    /// <summary>
    /// Pollutes runtime-owned process state (a named root child, the current scene, the engine time
    /// scale, and the tree pause flag) and asserts only the post-conditions this test itself
    /// guarantees synchronously. Undoing this pollution is owned by the session baseline
    /// restoration, verified by <see cref="BaselineChecker_WhenPollutingSiblingAlreadyRan_RuntimeOwnedStateIsRestored"/>.
    /// </summary>
    [Fact]
    public void PollutingTest_DirtiesRuntimeOwnedState_AndItsOwnPostConditionsHold()
    {
        SceneTree tree = GetSceneTree();

        Node pollutionProbe = new()
        {
            Name = PollutionProbeRootChildName
        };
        tree.Root.AddChild(pollutionProbe);

        Node pollutedScene = LoadPackedScene(PollutedScenePath).Instantiate();
        pollutedScene.Name = PollutedSceneRootChildName;
        tree.Root.AddChild(pollutedScene);
        tree.CurrentScene = pollutedScene;

        Engine.TimeScale = PollutedTimeScale;
        tree.Paused = true;

        Assert.True(tree.Paused);
        Assert.Equal(PollutedTimeScale, Engine.TimeScale);
        Assert.Same(pollutionProbe, tree.Root.GetNodeOrNull(PollutionProbeRootChildName));
        Assert.Same(pollutedScene, tree.CurrentScene);
        Assert.Equal(PollutedScenePath, pollutedScene.SceneFilePath);

        ReusableSessionCoordination.PollutingTestCompleted = true;
    }

    /// <summary>
    /// When the polluting sibling has already run in this session, verifies the session baseline
    /// restoration undid every piece of its runtime-owned pollution and left a valid
    /// <c>Global</c> autoload. Passes trivially when the sibling has not run yet, so any test order
    /// is accepted.
    /// </summary>
    [Fact]
    public void BaselineChecker_WhenPollutingSiblingAlreadyRan_RuntimeOwnedStateIsRestored()
    {
        if (!ReusableSessionCoordination.PollutingTestCompleted)
        {
            return;
        }

        SceneTree tree = GetSceneTree();

        Assert.False(tree.Paused);
        Assert.Equal(1d, Engine.TimeScale);
        Assert.Null(tree.Root.GetNodeOrNull(PollutionProbeRootChildName));
        Assert.Null(tree.Root.GetNodeOrNull(PollutedSceneRootChildName));

        Node? currentScene = tree.CurrentScene;
        Assert.NotNull(currentScene);
        Assert.NotEqual(PollutedScenePath, currentScene.SceneFilePath);

        AssertGlobalAutoloadPresentAndFunctional(tree);
    }

    /// <summary>
    /// First of two symmetric participants proving CLR static state persists between tests inside
    /// one session. Whichever participant runs first merely records its marker; the second asserts
    /// the first participant's value survived the test boundary (the documented no-CLR-isolation
    /// contract) and that at least two freshly constructed fixture instances served the pair.
    /// </summary>
    [Fact]
    public void StaticStateSharing_WriterOne_ExchangesSessionStaticMarker()
    {
        VerifySharedStaticMarkerExchange(
            ownMarker: ReusableSessionCoordination.FirstWriterMarker,
            siblingMarker: ReusableSessionCoordination.SecondWriterMarker);
    }

    /// <summary>
    /// Second of two symmetric participants proving CLR static state persists between tests inside
    /// one session; see <see cref="StaticStateSharing_WriterOne_ExchangesSessionStaticMarker"/> for
    /// the order-tolerant exchange rules.
    /// </summary>
    [Fact]
    public void StaticStateSharing_WriterTwo_ExchangesSessionStaticMarker()
    {
        VerifySharedStaticMarkerExchange(
            ownMarker: ReusableSessionCoordination.SecondWriterMarker,
            siblingMarker: ReusableSessionCoordination.FirstWriterMarker);
    }

    /// <summary>
    /// Verifies an ordinary (non-isolated-<c>Game</c>) test sees the <c>Global</c> autoload under
    /// the scene-tree root and that it backs the <see cref="Game.Instance"/> runtime surface.
    /// </summary>
    [Fact]
    public void OrdinaryTest_GlobalAutoloadIsPresentAndFunctional()
        => AssertGlobalAutoloadPresentAndFunctional(GetSceneTree());

    /// <summary>
    /// When the isolated-<c>Game</c> sibling class has already run in this session, verifies a
    /// fresh valid <c>Global</c> autoload exists again after that class's tests executed without
    /// it. Passes trivially when the sibling has not run yet.
    /// </summary>
    [Fact]
    public void IsolatedGameChecker_WhenIsolatedSiblingAlreadyRan_FreshGlobalAutoloadIsRestored()
    {
        if (!ReusableSessionCoordination.IsolatedGameTestCompleted)
        {
            return;
        }

        AssertGlobalAutoloadPresentAndFunctional(GetSceneTree());
    }

    private static void VerifySharedStaticMarkerExchange(string ownMarker, string siblingMarker)
    {
        string? observedMarker = ReusableSessionCoordination.SharedStaticMarker;
        if (observedMarker is null)
        {
            // First participant to run in this session: record the visit and pass trivially; the
            // verification duty passes to the sibling.
            ReusableSessionCoordination.SharedStaticMarker = ownMarker;
            return;
        }

        // Second participant to run: the sibling's CLR static value persisted across the test
        // boundary, proving the session shares one process without CLR isolation.
        Assert.Equal(siblingMarker, observedMarker);

        // Both fixture instances were constructed fresh, one constructor run per test.
        int recordedInstanceCount = ReusableSessionCoordination.FixtureInstanceIds.Count;
        Assert.True(
            recordedInstanceCount >= 2,
            $"Expected at least two freshly constructed fixture instances once both static-sharing tests ran, found {recordedInstanceCount}.");
    }

    private static void AssertGlobalAutoloadPresentAndFunctional(SceneTree tree)
    {
        Node? globalNode = tree.Root.GetNodeOrNull(GlobalAutoloadName);
        Assert.NotNull(globalNode);

        Game globalGame = Assert.IsAssignableFrom<Game>(globalNode);
        Assert.True(GodotObject.IsInstanceValid(globalGame));

        // Availability per current semantics: the Global autoload owns the active Game singleton
        // and exposes its service-provider surface. Game.Instance throws when unavailable, which
        // fails this test directly.
        Game gameInstance = Game.Instance;
        Assert.Same(globalGame, gameInstance);
        _ = Assert.IsAssignableFrom<IServiceProvider>(gameInstance);
    }
}
