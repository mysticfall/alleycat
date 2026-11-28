using System.Reflection;
using AlleyCat.Motion;
using AlleyCat.Navigation;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Target-space conditioning on authored inputs, not native inference or displayed footwork.</summary>
public sealed class DepartureConditioningTests
{
    private const double Horizon = 49.0 / 30;
    // Approved left-turn metadata (ANIM-002 turn-angular-reference.json), not the result under test.
    private const double SourceTurnRate = 0.583303714883936;

    /// <summary>When the angular budget cannot face travel, shorten the first advance and suffix together.</summary>
    [Headless]
    [Fact]
    public void RearwardFirstWindowShortensAdvanceAndMatchingSuffixOnly()
    {
        const float heading = -3f;
        const float speed = .5785f;
        Vector3[] path = [Vector3.Zero, Vector3.Back * 10];
        using ReachabilityOnlyNavigation nav = CreateNavigation(out SomaMotionData data);
        Transform3D[][] authored = AuthoredWindow(data);
        Transform3D[][] first = Clone(authored);
        MotionBricksNavigation.CurveIntent intent = nav.PlaceNativeTravelTargets(first, path, 1, Vector3.Zero, heading, speed);
        float unconditionedAdvance = speed * (float)Horizon;
        float advance = World(first[0]).Z;
        Assert.True(advance > 0 && advance < unconditionedAdvance);
        Assert.InRange(advance / unconditionedAdvance, 0, (SourceTurnRate * Horizon / 3) + .00002);
        float shortenedSpeed = advance / (float)Horizon;
        float suffixSpeed = (World(first[1]).Z - advance) * 30;
        Assert.InRange(Math.Abs((suffixSpeed / speed) - (advance / unconditionedAdvance)), 0, .00002f);
        CheckWindow(authored, first, shortenedSpeed);

        Transform3D[][] subsequent = Clone(authored);
        MotionBricksNavigation.CurveIntent later = nav.PlaceNativeTravelTargets(subsequent, path, 1, Vector3.Zero, heading, speed);
        CheckWindow(authored, subsequent, speed);
        for (int i = 0; i < 4; i++)
        {
            double angularBudget = SourceTurnRate * (Horizon + (i / 30.0));
            float progress = Mathf.Wrap(intent.EncodedHeadings[i] - heading, -Mathf.Pi, Mathf.Pi);
            Assert.InRange(progress, (float)angularBudget - .000002f, (float)angularBudget + .000002f);
            Assert.True(Math.Abs(Mathf.Wrap(intent.EncodedHeadings[i], -Mathf.Pi, Mathf.Pi)) > Mathf.Pi / 2);
            Assert.Equal(first[i][0].Basis, subsequent[i][0].Basis);
            Assert.Equal(intent.EncodedHeadings[i], later.EncodedHeadings[i]);
        }
    }

    /// <summary>Compatible departures and later straight windows retain ordinary placement, source bones and pacing.</summary>
    [Headless]
    [Fact]
    public void CompatibleDepartureAndLongStraightKeepOriginalPlacement()
    {
        using ReachabilityOnlyNavigation nav = CreateNavigation(out SomaMotionData data);
        const float heading = 0;
        const float speed = .6f;
        Vector3[] path = [Vector3.Zero, new(0, 0, 100)];
        Transform3D[][] authored = AuthoredWindow(data);
        // First placement and one later placement exercise the two distinct states.
        for (int window = 0; window < 2; window++)
        {
            Transform3D[][] actual = Clone(authored);
            MotionBricksNavigation.CurveIntent intent = nav.PlaceNativeTravelTargets(actual, path, 1, Vector3.Zero, heading, speed);
            CheckWindow(authored, actual, speed);
            Assert.All(intent.EncodedHeadings, facing => Assert.InRange(Math.Abs(Mathf.Wrap(facing, -Mathf.Pi, Mathf.Pi)), 0, .000002f));
        }
    }

    private static void CheckWindow(Transform3D[][] authored, Transform3D[][] actual, float speed)
    {
        float lastStation = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector3 root = World(actual[i]);
            Assert.True(root.Z > lastStation);
            Assert.InRange(Math.Abs(root.X), 0, .000002f);
            Assert.InRange(Math.Abs(root.Z - (speed * (Horizon + (i / 30.0)))), 0, .000002);
            Assert.Equal(authored[i][0].Origin.Y, root.Y);
            // A yaw-only placement cannot change the vertical component or length of a root axis.
            for (int axis = 0; axis < 3; axis++)
            {
                Assert.Equal(authored[i][0].Basis[axis].Y, actual[i][0].Basis[axis].Y, .000002f);
                Assert.Equal(authored[i][0].Basis[axis].Length(), actual[i][0].Basis[axis].Length(), .000002f);
            }
            Assert.Equal(authored[i][1..], actual[i][1..]);
            lastStation = root.Z;
        }
    }

    private static Transform3D[][] AuthoredWindow(SomaMotionData data)
    {
        Transform3D[][] poses = [.. Enumerable.Range(0, 4).Select(_ => (Transform3D[])data.Rest.Clone())];
        int foot = Array.IndexOf(data.Names, "LeftFoot");
        for (int i = 0; i < poses.Length; i++)
        {
            poses[i][0] = new Transform3D(new Basis(Vector3.Right, .1f * (i + 1)), new Vector3(.2f, 1 + (.01f * i), .03f * i));
            poses[i][foot].Basis = new Basis(Vector3.Right, .15f * (i + 1));
        }
        return poses;
    }

    private static ReachabilityOnlyNavigation CreateNavigation(out SomaMotionData data)
    {
        data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        var sampler = new ReviewedRoleSampler(ProjectSettings.GlobalizePath("res://assets/animations/locomotion/feminine/set.json"), data);
        var nav = new ReachabilityOnlyNavigation();
        typeof(MotionBricksNavigation).GetField("_data", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, data);
        typeof(MotionBricksNavigation).GetField("_reviewed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(nav, sampler);
        return nav;
    }

    private static Transform3D[][] Clone(Transform3D[][] poses) => [.. poses.Select(p => (Transform3D[])p.Clone())];
    private static Vector3 World(Transform3D[] pose) => SomaMotionData.ModelToWorld * pose[0].Origin;
}
