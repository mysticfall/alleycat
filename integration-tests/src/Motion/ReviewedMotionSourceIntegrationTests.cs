using System.IO.Compression;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Real CLI-export/production-reader boundary; requires the documented reviewed probe artefacts.</summary>
[Headless]
public sealed class ReviewedMotionSourceIntegrationTests
{
    /// <summary>Compare dynamic windows to raw Blender 5.3 pose matrices of the committed approved take.</summary>
    [Fact]
    public void ReviewedExport_SamplesBothRolesAgainstIndependentBlenderMatrices()
    {
        var calibration = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath(
            "res://assets/animations/locomotion/feminine/landing-ablation-01.json"), calibration);
        using FileStream file = File.OpenRead(ProjectSettings.GlobalizePath(
            "res://../integration-tests/fixtures/motion-frame-oracles/blender-matrices.f32.gz"));
        using var stream = new GZipStream(file, CompressionMode.Decompress);
        using var oracle = new BinaryReader(stream);
        Assert.Equal("3da77b8ab373dc503b789b8377d3023ea2496eab34086ef91e400a31f68de80a",
            source.Manifest.GetProperty("descriptor").GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(1200, source.FrameCount);
        Assert.Equal(8, source.Roles.Count);
        Assert.Equal(1, source.FirstFrame);
        var buffer = new Transform3D[source.JointCount * 4];
        var world = new Transform3D[source.JointCount];
        float maximumPositionError = 0, maximumBasisError = 0;
        int windowCount = 0;
        Basis inverseAxis = SomaMotionData.BlenderToModel.Inverse();
        foreach ((string roleName, int first, int last) in new[] { ("forward", 46, 106), ("left_noncross", 346, 406) })
        {
            var observed = new Transform3D[last - first + 2, source.JointCount];
            for (int frame = 0; frame < observed.GetLength(0); frame++)
            {
                for (int joint = 0; joint < source.JointCount; joint++)
                {
                    observed[frame, joint] = ReadBlenderMatrix(oracle);
                }
            }
            ReviewedMotionSource.Role role = source.GetRole("feminine", "normal", roleName);
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
                        Transform3D expected = observed[start + f - first, j];
                        // Re-express canonical output in Blender's raw rest axes for comparison.
                        Vector3 rawPosition = inverseAxis * world[j].Origin;
                        Basis rawBasis = inverseAxis * world[j].Basis * SomaMotionData.BlenderToModel * calibration.BlenderRest[j].Basis;
                        maximumPositionError = Math.Max(maximumPositionError, rawPosition.DistanceTo(expected.Origin));
                        for (int axis = 0; axis < 3; axis++)
                        {
                            Vector3 error = (rawBasis[axis] - expected.Basis[axis]).Abs();
                            maximumBasisError = Math.Max(maximumBasisError, Math.Max(error.X, Math.Max(error.Y, error.Z)));
                        }
                    }
                    Transform3D current = observed[start + f - first, 0];
                    Transform3D next = observed[start + f - first + 1, 0];
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
        Assert.Equal(-1, stream.ReadByte());
        _ = Assert.Throws<InvalidOperationException>(() => source.GetRole("feminine", "missing-stage", "forward"));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.FrameAtTime(double.NaN));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.FrameAtTime(40));
    }

    private static Transform3D ReadBlenderMatrix(BinaryReader reader)
    {
        float xx = reader.ReadSingle(), xy = reader.ReadSingle(), xz = reader.ReadSingle(), px = reader.ReadSingle();
        float yx = reader.ReadSingle(), yy = reader.ReadSingle(), yz = reader.ReadSingle(), py = reader.ReadSingle();
        float zx = reader.ReadSingle(), zy = reader.ReadSingle(), zz = reader.ReadSingle(), pz = reader.ReadSingle();
        return new Transform3D(new Basis(new(xx, yx, zx), new(xy, yy, zy), new(xz, yz, zz)), new(px, py, pz));
    }
}
