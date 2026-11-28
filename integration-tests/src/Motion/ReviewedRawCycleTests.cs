using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Raw-cycle and finite-role boundaries on a synthetic, non-admitted source.</summary>
[Headless]
public sealed class ReviewedRawCycleTests
{
    /// <summary>TR4: preserve original frame identities and articulation while composing nonzero-yaw raw travel.</summary>
    [Fact]
    public void ExplicitCycle_WrapsEachSampleAndAccumulatesSE2WithoutAdmittingFiniteRole()
    {
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://assets/animations/motionbricks/poses.json"));
        string path = ProjectSettings.GlobalizePath($"res://temp/motionbricks-fixture-repair/raw-cycle-{Guid.NewGuid():N}.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            byte[] bundle = AuthoredBundle(data);
            File.WriteAllBytes(path, bundle);
            var source = new ReviewedMotionSource(path, data);
            ReviewedMotionSource.Role role = source.GetRole("synthetic", "normal", "forward");
            JsonElement trial = JsonSerializer.SerializeToElement(new
            {
                schema = "anim002_experimental_raw_cycle_v1",
                status = "EXPERIMENTAL_NOT_ADMITTED",
                bundle_sha256 = Convert.ToHexStringLower(SHA256.HashData(bundle)),
                variant = "synthetic",
                stage = "normal",
                role = "forward",
                fps = 30,
                endpoint_policy = "next_boundary_reference_not_duplicated",
                source_processing = "none",
                first = 48,
                end_exclusive = 87,
                period_frames = 39,
                cycle_transform = new
                {
                    translation_m = new[] { 1, 0, 0 },
                    yaw_radians = Math.PI / 2,
                },
            });
            ReviewedMotionSource.ExperimentalCycle cycle = source.LoadExperimentalCycle(trial, role);
            Assert.Equal(48, cycle.First);
            Assert.Equal(87, cycle.EndExclusive);
            Assert.Equal(39, cycle.PeriodFrames);
            Assert.Equal([86, 48, 49, 50], Enumerable.Range(86, 4).Select(i => cycle.Locate(i).OriginalFrame));
            Assert.Equal([0, 1, 1, 1], Enumerable.Range(86, 4).Select(i => cycle.Locate(i).CycleIndex));
            Assert.Equal([0, 1, 1, 2], Enumerable.Range(1, 4).Select(i => ReviewedMotionSource.LocateCycleSample(0, 2, i).CycleIndex));

            var output = new Transform3D[4 * source.JointCount];
            source.CopyCycleWindow(cycle, 86, output);
            // The source roots of 48–50 are (0, 1, 0/.01/.02); +90° maps +Z to +X.
            int[] frames = [86, 48, 49, 50];
            float[] x = [0, 1, 1.01f, 1.02f], z = [.38f, 0, 0, 0];
            for (int i = 0; i < 4; i++)
            {
                CheckRoot(output[i * source.JointCount], new Vector3(x[i], 1, z[i]), i == 0 ? Vector3.Back : Vector3.Right);
                for (int j = 1; j < source.JointCount; j++)
                {
                    Transform3D joint = AuthoredJoint(data, frames[i], j);
                    Transform3D sampled = output[(i * source.JointCount) + j];
                    Assert.InRange(joint.Origin.DistanceTo(sampled.Origin), 0, 1e-6f);
                    Assert.InRange(joint.Basis.X.DistanceTo(sampled.Basis.X), 0, 1e-6f);
                    Assert.InRange(joint.Basis.Y.DistanceTo(sampled.Basis.Y), 0, 1e-6f);
                    Assert.InRange(joint.Basis.Z.DistanceTo(sampled.Basis.Z), 0, 1e-6f);
                }
            }
            source.CopyCycleWindow(cycle, 126, output);
            CheckRoot(output[0], new Vector3(1, 1, -1), Vector3.Forward);
            Assert.InRange(ReviewedMotionSource.AccumulatedTravel(cycle.Travel, 4).Origin.Length(), 0, 1e-6f);
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => source.CopyWindow(role, 104, output));
            _ = Assert.Throws<InvalidOperationException>(() => source.GetRole("synthetic", "normal", "backward"));
        }
        finally
        {
            File.Delete(path);
        }

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

    private static void CheckRoot(Transform3D actual, Vector3 position, Vector3 forward)
    {
        Assert.InRange(actual.Origin.DistanceTo(position), 0, 1e-6f);
        Assert.InRange(actual.Basis.Z.DistanceTo(forward), 0, 1e-6f);
        Assert.InRange(actual.Basis.Y.DistanceTo(Vector3.Up), 0, 1e-6f);
    }

    private static Transform3D AuthoredJoint(SomaMotionData data, int frame, int joint) =>
        joint == 1 ? new Transform3D(new Basis(Vector3.Right, frame / 100f), data.Rest[joint].Origin) : data.Rest[joint];

    private static byte[] AuthoredBundle(SomaMotionData data)
    {
        const int first = 46, last = 106;
        int[] range = [first, last], representative = [46, 48];
        var source = new
        {
            frames = range,
            fps = 30,
        };
        var role = new
        {
            role = "forward",
            usable = range,
            representative,
            travel_direction = new[] { 0, 0, 1 },
            loopability = "non_cyclic",
            phase = new
            {
                intended = "test-only",
                observed_support = "unknown",
                transfer_compatibility = "unverified",
            },
        };
        object Descriptor(object roleData)
        {
            var stage = new
            {
                id = "normal",
                coverage = "partial",
                roles = new[] { roleData },
            };
            var variant = new
            {
                id = "synthetic",
                posture = "test-only",
                stages = new[] { stage },
            };
            return new
            {
                schema = "anim002_reviewed_descriptor_v1",
                source,
                variants = new[] { variant },
            };
        }
        string descriptorText = JsonSerializer.Serialize(Descriptor(role));
        string descriptorHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(descriptorText)));
        JsonObject physicalRole = JsonSerializer.SerializeToNode(role)!.AsObject();
        physicalRole["physical_reference"] = JsonSerializer.SerializeToNode(new
        {
            interval = representative,
            velocity_mps = new[] { 0, 0, 0 },
            planar_net_speed_mps = 0,
            planar_path_speed_mps = 0,
            angular_velocity_rps = 0,
        });
        static double[] Root(int frame)
        {
            return frame < 87 ? [0, 1, Math.Max(0, frame - 48) / 100.0] : [1, 1, 0];
        }
        static double Yaw(int frame)
        {
            return frame < 87 ? 0 : Math.PI / 2;
        }
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "anim002_reviewed_source_v1",
            disposition = "HUMAN_ACCEPTED_SOURCE_ONLY", // Synthetic parser-contract marker, not a real approval.
            coordinates = new
            {
                units = "metres",
                up = "+Y",
                forward = "+Z",
                anatomical_left = "+X",
                root = "evaluated_Hips_not_ground_Root",
                pose = "canonical_identity_rest_parent_local",
                heading = "atan2_forward_x_forward_z_radians",
                foot = "joint_not_sole",
            },
            names = data.Names,
            parents = data.Parents,
            descriptor = Descriptor(physicalRole),
            descriptor_text = descriptorText,
            descriptor_sha256 = descriptorHash,
            review = new
            {
                schema = "anim002_reviewed_approval_v1",
                decision = "accepted",
                reviewer = "synthetic fixture",
                approval_origin = "synthetic fixture",
                preview_rig = "Female",
                calibration_preserved = true,
                source,
                descriptor_sha256 = descriptorHash,
                full_window = range,
                accepted_ranges = new Dictionary<string, int[]> { ["synthetic/normal/forward"] = range },
            },
            samples = Enumerable.Range(first, last - first + 1).Select(frame =>
            {
                double[] root = Root(frame);
                double yaw = Yaw(frame);
                double[] Local(int joint)
                {
                    if (joint == 0)
                    {
                        return [root[0], root[1], root[2], 0, Math.Sin(yaw / 2), 0, Math.Cos(yaw / 2)];
                    }
                    Transform3D value = AuthoredJoint(data, frame, joint);
                    Quaternion q = value.Basis.GetRotationQuaternion();
                    return [value.Origin.X, value.Origin.Y, value.Origin.Z, q.X, q.Y, q.Z, q.W];
                }
                return new
                {
                    frame,
                    time_seconds = (frame - first) / 30.0,
                    pose = Enumerable.Range(0, data.Names.Length).Select(Local),
                    root_m = root,
                    heading_radians = yaw,
                    velocity_to_next_mps = frame == last ? null : Root(frame + 1).Zip(root, (next, current) => (next - current) * 30),
                    angular_velocity_to_next_rps = frame == last ? (double?)null : (Yaw(frame + 1) - yaw) * 30,
                };
            }),
        });
    }
}
