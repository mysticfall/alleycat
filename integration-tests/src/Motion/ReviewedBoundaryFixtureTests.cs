using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Raw repetition preserves source rows across the boundary; no native or visual acceptance.</summary>
[Headless]
public sealed class ReviewedBoundaryFixtureTests
{
    /// <summary>Normal forward selection crosses 93→58 without changing context or non-root articulation.</summary>
    [Fact]
    public void RawBoundary_PreservesContextAndOriginalLocalPoses()
    {
        string directory = ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine");
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var source = new ReviewedMotionSource(Path.Combine(directory, "landing-ablation-01.json"), data);
        var sampler = new ReviewedRoleSampler(Path.Combine(directory, "set.json"), data);
        Transform3D[] contextPose = ReviewedPoseFixture.Pose(data);
        Transform3D[][] context = [.. Enumerable.Range(0, 4).Select(_ => (Transform3D[])contextPose.Clone())];
        Transform3D[][] original = [.. context.Select(p => (Transform3D[])p.Clone())];
        _ = sampler.SetIntent("forward", 0);
        Vector3 ground = new(context[3][0].Origin.X, 0, context[3][0].Origin.Z);
        Transform3D[][] target = sampler.Targets(0, 35.0 / 30, Basis.Identity, ground);
        Assert.Equal(93, sampler.Selection.WindowStart);
        int[] frames = [93, 58, 59, 60];
        Assert.Equal(ground.X, target[0][0].Origin.X);
        Assert.Equal(ground.Z, target[0][0].Origin.Z);
        for (int f = 0; f < 4; f++)
        {
            Assert.Equal(original[f], context[f]);
            JsonElement raw = source.Manifest.GetProperty("samples")[frames[f] - 1].GetProperty("pose");
            Assert.Equal(raw[0][1].GetSingle(), target[f][0].Origin.Y);
            for (int j = 1; j < data.Names.Length; j++)
            {
                float[] p = [.. raw[j].EnumerateArray().Select(v => v.GetSingle())];
                var expected = new Transform3D(new Basis(new Quaternion(p[3], p[4], p[5], p[6]).Normalized()), new Vector3(p[0], p[1], p[2]));
                Assert.True(expected.IsEqualApprox(target[f][j]));
            }
        }
        var buffer = new Transform3D[data.Names.Length * 4];
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopyWindow(source.GetRole("feminine", "normal", "forward"), 125, buffer));
    }
}
