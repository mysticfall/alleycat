using AlleyCat.IK;
using AlleyCat.Rigging;
using Godot;
using Xunit;
using Xunit.Abstractions;

namespace AlleyCat.Tests.IK;

/// <summary>Checks the unlimited reference independently of the temporal limiter and final target placement.</summary>
public sealed class LegPoleReferenceTests(ITestOutputHelper output)
{
    /// <summary>TR10 uses foot yaw in ordinary reach and selects the declared fallback when it cannot.</summary>
    [Fact]
    public void WeakCueFollowsUsableFootAndRespectsOrientationExceptions()
    {
        Basis foot = new(Vector3.Up, Mathf.DegToRad(30f));
        foreach (Basis basis in new[] { foot, foot.Inverse() })
        {
            Assert.True(Resolve(Vector3.Down, 0.9f, basis).Dot(basis.Z) > 0.999f);
        }

        Basis tilted = new(Vector3.Right, Mathf.Pi / 2f);
        Assert.True(Resolve(Vector3.Down, 0.9f, tilted).Dot(Vector3.Back) > 0.999f);
        Vector3 bend = new(0.022f, 0f, 0.02f);
        Assert.True(Resolve(Vector3.Down, 0.9f, tilted, bend).Dot(bend.Normalized()) > 0.999f);
        Assert.True(Resolve(Vector3.Back, 0.9f, foot).Dot(Vector3.Up) > 0.999f);
    }

    /// <summary>Horizontal forward crossings must not reverse the reference before limiting.</summary>
    [Fact]
    public void ForwardCrossingsAreContinuousAndFoldedPoleIsNotRearward()
    {
        float worstStep = 0f;
        for (int azimuth = 0; azimuth < 180; azimuth += 45)
        {
            Vector3 transverse = Vector3.Up.Rotated(Vector3.Back, Mathf.DegToRad(azimuth));
            Vector3 previous = Vector3.Zero;
            for (int i = -10; i <= 10; ++i)
            {
                Vector3 axis = (Vector3.Back + (transverse * (i * 0.001f))).Normalized();
                Vector3 current = Resolve(axis, 0.85f, Basis.Identity);
                Assert.True(current.Dot(Vector3.Up) > 0.99f);
                Assert.True(i == -10 || current.AngleTo(previous) < 0.00201f);
                if (i != -10)
                {
                    worstStep = Math.Max(worstStep, current.AngleTo(previous));
                }

                previous = current;
            }
        }

        Vector3 folded = new(0f, 0.6f, 0.8f);
        float alignment = Resolve(folded, 0.3f, Basis.Identity).Dot(Vector3.Back.Slide(folded).Normalized());
        output.WriteLine($"Largest crossing step: {Mathf.RadToDeg(worstStep)}°, folded forward dot: {alignment}.");
        Assert.True(alignment >= 0f);
    }

    /// <summary>Rejecting a rearward cue must not introduce a new snap as it crosses the side plane.</summary>
    [Fact]
    public void TiltedFootAmbiguousCueHasNoLateralHemisphereSwitch()
    {
        Basis tilted = new(Vector3.Right, Mathf.Pi / 2f);
        Vector3 bend = new(0.04f, -0.04f, 0f);
        Vector3 before = new Vector3(0f, -1f, -0.001f).Normalized();
        Vector3 after = new Vector3(0f, -1f, 0.001f).Normalized();
        float swing = Resolve(before, 0.85f, tilted, bend).AngleTo(Resolve(after, 0.85f, tilted, bend));
        Assert.True(swing <= 40f * before.AngleTo(after), $"Side-plane switch: {Mathf.RadToDeg(swing)}°.");
    }

    /// <summary>Neighbour differences sample the whole directional envelope rather than selected meridians.</summary>
    [Fact]
    public void ReferenceIsContinuousAcrossTheAuthoredReachEnvelope()
    {
        float worst = 0f;
        (int Polar, int Azimuth, float Reach, LimbSide Side, int Yaw) location = default;
        foreach (LimbSide side in new[] { LimbSide.Left, LimbSide.Right })
        {
            foreach (float reach in new[] { 0.3f, 0.575f, 0.85f })
            {
                foreach (int yaw in new[] { -30, 0, 30 })
                {
                    Basis foot = yaw == 0 ? new Basis(Vector3.Right, Mathf.Pi / 2f) : new Basis(Vector3.Up, Mathf.DegToRad(yaw));
                    Vector3 bend = yaw == 0 ? new Vector3(0.04f, -0.04f, 0f) : Vector3.Zero;
                    for (int polar = 1; polar <= 150; ++polar)
                    {
                        for (int azimuth = 0; azimuth < 360; ++azimuth)
                        {
                            Vector3 a = Axis(polar, azimuth);
                            Vector3 direction = Resolve(a, reach, foot, bend, side);
                            Assert.True(direction.IsNormalized() && Mathf.Abs(a.Dot(direction)) < 0.0001f);
                            float ratio = Math.Max(Ratio(Axis(polar - 1, azimuth)), Ratio(Axis(polar, azimuth + 1)));
                            if (ratio > worst)
                            {
                                worst = ratio;
                                location = (polar, azimuth, reach, side, yaw);
                            }

                            float Ratio(Vector3 b)
                            {
                                return direction.AngleTo(Resolve(b, reach, foot, bend, side)) / a.AngleTo(b);
                            }
                        }
                    }
                }
            }
        }

        output.WriteLine($"Worst angular ratio {worst} at (polar, azimuth, reach, side, yaw) {location}.");
        Assert.True(worst <= 40f, $"Worst angular ratio {worst} at {location}.");
    }

    private static Vector3 Axis(int polar, int azimuth) =>
        Vector3.Down.Rotated(Vector3.Right, -Mathf.DegToRad(polar)).Rotated(Vector3.Up, Mathf.DegToRad(azimuth));

    private static Vector3 Resolve(Vector3 axis, float reach, Basis foot, Vector3 bend = default, LimbSide side = LimbSide.Left) =>
        LegIKController.ResolveReference(axis, reach, Basis.Identity, foot, side, bend, out _);
}
