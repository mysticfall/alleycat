using AlleyCat.IK;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.IK;

/// <summary>Ensures the foot basis reaches the installed pole controller, not only its reference helper.</summary>
public sealed class LegPoleFootOrientationTests
{
    /// <summary>A weak bend and a fixed goal position leave the yawed foot in charge of the settled pole.</summary>
    [Headless]
    [Fact]
    public void FixedGoalYawRedirectsWeakCuePole()
    {
        var skeleton = new Skeleton3D { ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual };
        try
        {
            GetSceneTree().Root.AddChild(skeleton);
            skeleton.GlobalBasis = new Basis(Vector3.Up, Mathf.Pi);
            string[] names = ["LeftUpperLeg", "LeftLowerLeg", "LeftFoot"];
            for (int i = 0; i < names.Length; ++i)
            {
                _ = skeleton.AddBone(names[i]);
                skeleton.SetBoneParent(i, i - 1);
                skeleton.SetBoneRest(i, new Transform3D(Basis.Identity, i > 0 ? Vector3.Down * 0.4f : Vector3.Zero));
            }

            skeleton.ResetBonePoses();
            var foot = new Node3D { Position = Vector3.Down * 0.8f };
            var pole = new Node3D();
            skeleton.AddChild(foot);
            skeleton.AddChild(pole);
            var controller = new LegIKController { FootTarget = foot, PoleTarget = pole };
            skeleton.AddChild(controller);
            foreach (int degrees in new[] { 30, -30 })
            {
                foot.Basis = new Basis(Vector3.Up, Mathf.DegToRad(degrees));
                // One simulated second covers the 60° reversal at the specified 75°/s, without a wall-clock wait.
                for (int i = 0; i < 60; ++i)
                {
                    controller._ProcessModificationWithDelta(1d / 60d);
                }

                Vector3 actual = pole.GlobalPosition.Slide(Vector3.Up).Normalized();
                Assert.True(actual.Dot(foot.GlobalBasis.Z) > 0.999f, $"Foot yaw {degrees}°, pole {actual}.");
            }
        }
        finally
        {
            skeleton.Free();
        }
    }
}
