using System.Reflection;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.Navigation.Recording;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

public sealed partial class PlayerMotionBricksIntegrationTests
{
    private sealed class StationaryConsumptionSession : SomaInferenceWorker.ISession
    {
        public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target) =>
            [.. Enumerable.Range(0, 50).Select(_ => (Transform3D[])context[3].Clone())];
        public void Dispose()
        {
        }
    }

    /// <summary>Retention ends at the owned endpoint and never delays a replacement or cancellation.</summary>
    [Fact]
    public async Task ShortPositioningRetentionReleasesForEndpointReplacementAndCancellation()
    {
        SceneTree tree = TestUtils.GetSceneTree();
        var root = new ProbeGame { Name = "ShortRetentionInterruption" };
        Node scene = GD.Load<PackedScene>(NavigationPlaytestRecorder.PlaytestScene).Instantiate();
        AlleyCat.Character.Character actor = scene.GetNode<AlleyCat.Character.Character>("NavigationTestNpc");
        MotionBricksNavigation? nav = null;
        bool Retained()
        {
            return (bool)typeof(MotionBricksNavigation).GetMethod("RetainShortPositioningTranslation", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nav, null)!;
        }

        double Phase()
        {
            return (double)typeof(MotionBricksNavigation).GetField("_phaseTime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nav)!;
        }

        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation value)
            {
                return;
            }
            nav = value;
            value.SessionFactory = () => new StationaryConsumptionSession();
        }
        async Task Seek(Func<bool> condition)
        {
            for (int i = 0; i < 180 && !condition(); i++)
            {
                await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
            }
            Assert.True(condition());
        }
        void ShortRequest()
        {
            float heading = nav!.RecordingAnatomicalHeading!.Value;
            Vector3 behind = actor.GlobalPosition - (new Vector3(Mathf.Sin(heading), 0, Mathf.Cos(heading)) * .2f);
            Assert.Equal(0, nav.SelectDestination(new Transform3D(new Basis(Vector3.Up, heading), behind), []));
        }
        tree.NodeAdded += Configure;
        root.AddChild(scene);
        tree.Root.AddChild(root);
        try
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
            Assert.NotNull(nav);
            ShortRequest();
            await Seek(() => Retained() && Phase() >= 39.0 / 30 && Phase() < 49.0 / 30);
            Assert.Equal(1, nav.Requests); // Ordinary reserve would already have submitted again.
            await Seek(() => nav.Requests > 1); // Even zero-progress motion cannot retain beyond its finite endpoint.
            Assert.Equal("", nav.Failure);
            await Seek(() => Retained() && Phase() >= 39.0 / 30 && Phase() < 49.0 / 30);
            int beforeReplacement = nav.Requests;
            Assert.Equal(0, nav.SelectDestination(new Transform3D(Basis.Identity, new(-2, 0, -2)), []));
            Assert.False(Retained());
            await Seek(() => nav.Requests > beforeReplacement);
            ShortRequest();
            await Seek(() => Retained() && Phase() >= 39.0 / 30 && Phase() < 49.0 / 30);
            nav.ClearDestination();
            Assert.False(nav.HasDestination);
            Assert.False(Retained());
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            Assert.Equal("", nav.Failure);
        }
        finally
        {
            tree.NodeAdded -= Configure;
            Task? completion = nav?.WorkerCompletion;
            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            if (completion is not null)
            {
                await completion;
            }
        }
    }
}
