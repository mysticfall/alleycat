using AlleyCat.IK;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>
/// Integration coverage for scene-wireable IK target intent providers.
/// </summary>
public sealed partial class IKTargetIntentProviderIntegrationTests
{
    /// <summary>
    /// Verifies the provider API always returns a world-space transform and desired influence.
    /// </summary>
    [Fact]
    public async Task TargetIntentProvider_WhenAddedToScene_ReturnsTransformAndInfluence()
    {
        SceneTree sceneTree = GetSceneTree();
        Node3D root = new()
        {
            Name = "IKTargetIntentProviderFixture",
        };

        TestIKTargetIntentProvider provider = new()
        {
            Name = "Provider",
            TargetIntent = new IKTargetIntent(
                new Transform3D(Basis.Identity, new Vector3(1.0f, 2.0f, -3.0f)),
                0.35f),
        };

        root.AddChild(provider);
        sceneTree.Root.AddChild(root);

        try
        {
            await WaitForNextFrameAsync(sceneTree);

            IKTargetIntent intent = provider.GetTargetIntent();

            AssertVectorApproximately(new Vector3(1.0f, 2.0f, -3.0f), intent.WorldTransform.Origin);
            Assert.Equal(0.35f, intent.DesiredInfluence);
        }
        finally
        {
            root.QueueFree();
            await WaitForNextFrameAsync(sceneTree);
        }
    }

    /// <summary>
    /// Verifies target intents constructed from only a world transform request full IK influence.
    /// </summary>
    [Fact]
    public void IKTargetIntent_WhenConstructedWithTransformOnly_DefaultsToFullInfluence()
    {
        Transform3D worldTransform = new(Basis.Identity, new Vector3(-0.5f, 1.25f, 0.75f));

        IKTargetIntent intent = new(worldTransform);

        Assert.Equal(worldTransform, intent.WorldTransform);
        Assert.Equal(1.0f, intent.DesiredInfluence);
    }

    private static void AssertVectorApproximately(Vector3 expected, Vector3 actual, float epsilon = 1e-4f)
    {
        Assert.InRange(actual.X, expected.X - epsilon, expected.X + epsilon);
        Assert.InRange(actual.Y, expected.Y - epsilon, expected.Y + epsilon);
        Assert.InRange(actual.Z, expected.Z - epsilon, expected.Z + epsilon);
    }

    private sealed partial class TestIKTargetIntentProvider : IKTargetIntentProvider
    {
        public IKTargetIntent TargetIntent
        {
            get;
            init;
        }

        public override IKTargetIntent GetTargetIntent()
            => TargetIntent;
    }
}
