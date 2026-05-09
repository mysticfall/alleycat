using AlleyCat.IK;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for scene-wireable IK target-state providers.
/// </summary>
public sealed partial class IKTargetStateProviderIntegrationTests
{
    /// <summary>
    /// Verifies the provider API always returns a world-space transform and desired influence.
    /// </summary>
    [Fact]
    public async Task TargetStateProvider_WhenAddedToScene_ReturnsTransformAndInfluence()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "IKTargetStateProviderFixture",
        };

        TestIKTargetStateProvider provider = new()
        {
            Name = "Provider",
            TargetState = new IKTargetState(
                new Transform3D(Basis.Identity, new Vector3(1.0f, 2.0f, -3.0f)),
                0.35f),
        };

        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForNextFrameAsync(sceneTree);

            IKTargetState state = provider.GetTargetState();

            AssertVectorApproximately(new Vector3(1.0f, 2.0f, -3.0f), state.WorldTransform.Origin);
            Assert.Equal(0.35f, state.DesiredInfluence);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies target states constructed from only a world transform request full IK influence.
    /// </summary>
    [Fact]
    public void IKTargetState_WhenConstructedWithTransformOnly_DefaultsToFullInfluence()
    {
        Transform3D worldTransform = new(Basis.Identity, new Vector3(-0.5f, 1.25f, 0.75f));

        IKTargetState state = new(worldTransform);

        Assert.Equal(worldTransform, state.WorldTransform);
        Assert.Equal(1.0f, state.DesiredInfluence);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    private sealed partial class TestIKTargetStateProvider : IKTargetStateProvider
    {
        public IKTargetState TargetState
        {
            get;
            init;
        }

        public override IKTargetState GetTargetState()
            => TargetState;
    }
}
