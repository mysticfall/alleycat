using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Real CLI-export/production-reader boundary; requires the documented reviewed probe artefacts.</summary>
[Headless]
public sealed class ReviewedMotionSourceIntegrationTests
{
    private const string Evidence = "res://temp/motionbricks/source-pipeline-05/reviewed-ingestion-01/";

    /// <summary>No renderer, inference or character fixture: compare dynamic windows to prior Blender observations.</summary>
    [Fact]
    public void ReviewedExport_SamplesBothRolesAgainstIndependentBlenderMatrices()
    {
        var calibration = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath(Evidence + "reviewed-source.json"), calibration);
        using var document = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath(Evidence + "blender-oracle.json")));
        JsonElement oracle = document.RootElement;
        string[] names = oracle.GetProperty("names").Deserialize<string[]>()!;
        JsonElement observed = oracle.GetProperty("matrices");
        Assert.Equal(oracle.GetProperty("source_sha256").GetString(), source.Manifest.GetProperty("descriptor").GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(300, source.FrameCount);
        Assert.Equal(2, source.Roles.Count);
        Assert.Equal(1, source.FirstFrame);
        var buffer = new Transform3D[source.JointCount * 4];
        var world = new Transform3D[source.JointCount];
        float maximumPositionError = 0, maximumBasisError = 0;
        int windowCount = 0;
        foreach ((string roleName, int first, int last) in new[] { ("forward", 46, 106), ("left_noncross", 196, 256) })
        {
            ReviewedMotionSource.Role role = source.GetRole("reviewed_probe", "normal", roleName);
            Assert.Equal(first, role.First);
            Assert.Equal(last, role.Last);
            Assert.Equal("unknown", role.Phase.GetProperty("observed_support").GetString());
            Transform3D firstFoot = default;
            bool varied = false;
            int foot = Array.IndexOf(calibration.Names, "LeftFoot");
            for (int start = first; start <= last - 3; start++)
            {
                Assert.Equal(start, source.FrameAtTime((start - 1) / 30.0));
                source.CopyWindow(role, start, buffer);
                if (start == first)
                {
                    firstFoot = buffer[foot];
                }
                varied |= !buffer[foot].IsEqualApprox(firstFoot);
                windowCount++;
                for (int f = 0; f < 4; f++)
                {
                    calibration.WorldPose(buffer.AsSpan(f * source.JointCount, source.JointCount), world);
                    for (int j = 0; j < source.JointCount; j++)
                    {
                        int k = Array.IndexOf(names, calibration.Names[j]);
                        Transform3D expected = SomaMotionData.ReadMatrix(observed[start + f - 1][k]);
                        // Invert canonical rest axes to raw Blender pose, independently observed before this exporter existed.
                        Basis inverseAxis = SomaMotionData.BlenderToModel.Inverse();
                        Vector3 rawPosition = inverseAxis * world[j].Origin;
                        Basis rawBasis = inverseAxis * world[j].Basis * SomaMotionData.BlenderToModel * calibration.BlenderRest[j].Basis;
                        maximumPositionError = Math.Max(maximumPositionError, rawPosition.DistanceTo(expected.Origin));
                        for (int axis = 0; axis < 3; axis++)
                        {
                            Vector3 error = (rawBasis[axis] - expected.Basis[axis]).Abs();
                            maximumBasisError = Math.Max(maximumBasisError, Math.Max(error.X, Math.Max(error.Y, error.Z)));
                        }
                    }
                    int index = Array.IndexOf(names, "Hips");
                    Transform3D current = SomaMotionData.ReadMatrix(observed[start + f - 1][index]);
                    Transform3D next = SomaMotionData.ReadMatrix(observed[start + f][index]);
                    Vector3 velocity = SomaMotionData.BlenderToModel * ((next.Origin - current.Origin) * 30);
                    Assert.InRange(source.VelocityToNext(start + f).DistanceTo(velocity), 0, 0.00001f);
                }
            }
            Assert.True(varied, "Different source phases must not collapse to a fixed window.");
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopyWindow(role, first - 1, buffer));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopyWindow(role, last - 2, buffer));
        }
        Assert.Equal(116, windowCount);
        Assert.InRange(maximumPositionError, 0, 0.00001f);
        Assert.InRange(maximumBasisError, 0, 0.00001f);
        _ = Assert.Throws<InvalidOperationException>(() => source.GetRole("reviewed_probe", "normal", "backward"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.FrameAtTime(double.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.FrameAtTime(10));
        File.WriteAllText(ProjectSettings.GlobalizePath(Evidence + "reader-evidence.json"), JsonSerializer.Serialize(new
        {
            windows = windowCount,
            framesCompared = windowCount * 4,
            jointsPerFrame = source.JointCount,
            maximumPositionError,
            maximumBasisError,
            nonCyclicOverrunRefused = true,
            phase = "intended_only_observed_unknown",
            actualRigPlayback = "not_exercised_reader_boundary_only"
        }));
    }
}
