using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <inheritdoc/>
[Headless]
public sealed class ReviewedRawCycleTests
{
    /// <summary>TR4 raw trial: original frame identity, wrapping each sample and composing nonzero-yaw travel.</summary>
    [Fact]
    public void ExplicitCycle_WrapsEachSampleAndAccumulatesSE2WithoutAdmittingFiniteRole()
    {
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://temp/motionbricks/source-pipeline-05/reviewed-ingestion-01/reviewed-source.json"), data);
        ReviewedMotionSource.Role role = source.GetRole("reviewed_probe", "normal", "forward");
        ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(ProjectSettings.GlobalizePath("res://temp/motionbricks/source-pipeline-06/raw-cycle-01/candidate.json"), role);
        Assert.Equal(48, cycle.First);
        Assert.Equal(87, cycle.EndExclusive);
        Assert.Equal(39, cycle.PeriodFrames);
        Assert.Equal([86, 48, 49, 50], Enumerable.Range(86, 4).Select(i => cycle.Locate(i).OriginalFrame));
        Assert.Equal([0, 1, 1, 1], Enumerable.Range(86, 4).Select(i => cycle.Locate(i).CycleIndex));
        Assert.Equal([0, 1, 1, 2], Enumerable.Range(1, 4).Select(i => ReviewedMotionSource.LocateCycleSample(0, 2, i).CycleIndex));
        var quarterTurn = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2), new Vector3(1, 0, 0));
        Transform3D twice = ReviewedMotionSource.AccumulatedTravel(quarterTurn, 2);
        Assert.True(twice.Origin.DistanceTo(new Vector3(1, 0, -1)) < 1e-5);
        Assert.True(twice.Basis.Z.DistanceTo(new Vector3(0, 0, -1)) < 1e-5);
        Assert.True(ReviewedMotionSource.AccumulatedTravel(quarterTurn, 4).Origin.Length() < 1e-5);
        var output = new Transform3D[4 * source.JointCount];
        var raw = new Transform3D[output.Length];
        source.CopyCycleWindow(cycle, 86, output);
        for (int i = 0; i < 4; i++)
        {
            int frame = i == 0 ? 86 : 47 + i;
            // Independent raw pose from the immutable whole-take JSON, not a second cyclic calculation.
            System.Text.Json.JsonElement row = source.Manifest.GetProperty("samples")[frame - 1].GetProperty("pose");
            for (int j = 0; j < source.JointCount; j++)
            {
                System.Text.Json.JsonElement p = row[j];
                var pose = new Transform3D(new Basis(new Quaternion(p[3].GetSingle(), p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle()).Normalized()),
                    new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle()));
                if (j == 0 && i > 0)
                {
                    pose = cycle.Travel * pose;
                }
                Assert.True(pose.Origin.DistanceTo(output[(i * source.JointCount) + j].Origin) < 1e-6);
                Assert.True(pose.Basis.Z.DistanceTo(output[(i * source.JointCount) + j].Basis.Z) < 1e-6);
            }
        }
        source.CopyCycleWindow(cycle, 126, output);
        source.CopyWindow(role, 48, raw);
        Transform3D expectedRoot = cycle.Travel * (cycle.Travel * raw[0]);
        Assert.True(expectedRoot.Origin.DistanceTo(output[0].Origin) < 1e-6);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopyWindow(role, 104, raw));
        _ = Assert.Throws<InvalidOperationException>(() => source.GetRole("reviewed_probe", "normal", "backward"));
        var clock = new SourcePhaseClock();
        clock.Activate("raw-trial", 48, 86, 0, 39);
        SourcePhaseClock.Selection first = clock.Select(0, 49.0 / 30);
        SourcePhaseClock.Selection later = clock.Select(3.9, 49.0 / 30);
        Assert.Equal(3, later.CycleIndex);
        Assert.Equal(165, later.SourceFrame, 8);
        Assert.Equal(48, later.InCycleFrame, 8);
        Assert.True(later.Available);
        Assert.True(clock.IsCurrent(first));
        clock.Stop(4);
        Assert.False(clock.IsCurrent(first));
        Assert.Equal(clock.Select(4, 0).SourceFrame, clock.Select(20, 0).SourceFrame);
        Assert.False(clock.Select(20, 0).Available);
        clock.Activate("finite", 46, 106, 21);
        Assert.False(clock.Select(22, 49.0 / 30).Available);
    }
}
