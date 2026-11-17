using System.Text.Json;
using AlleyCat.Motion;
using AlleyCat.TestFramework;
using Godot;
using Xunit;

namespace AlleyCat.IntegrationTests.Motion;

/// <summary>Bounded prototype contracts; requires the protected-source extraction, not upstream sample motions.</summary>
[Headless]
public sealed class MotionBricksIntegrationTests
{
    private static SomaMotionData Data() => new(ProjectSettings.GlobalizePath("res://temp/motionbricks/sequence-data.json"));

    /// <summary>Actual Kimodo conversion on authored, realised and rest poses; tolerances cover arithmetic only.</summary>
    [Fact]
    public void ModelFrames_MatchExecutedKimodoAndPreserveJointPositions()
    {
        SomaMotionData data = Data();
        string directory = ProjectSettings.GlobalizePath("res://temp/motionbricks/frame-conversion");
        var conversion = new SomaModelFrames(data, Path.Combine(directory, "model-frames.json"));
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "conversion-oracle.json")));
        string[] names = oracle.RootElement.GetProperty("names").Deserialize<string[]>()!;
        foreach (JsonProperty fixture in oracle.RootElement.GetProperty("fixtures").EnumerateObject())
        {
            JsonElement expected = fixture.Value;
            int frame = 0;
            foreach (JsonElement row in expected.GetProperty("input").EnumerateArray())
            {
                Transform3D[] canonical = [.. row.EnumerateArray().Select(p => new Transform3D(
                    new Basis(new Quaternion(p[3].GetSingle(), p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle()).Normalized()),
                    new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())))];
                var model = new Transform3D[canonical.Length];
                var before = new Transform3D[canonical.Length];
                var after = new Transform3D[canonical.Length];
                conversion.ToModel(canonical, model);
                data.WorldPose(canonical, before);
                data.WorldPose(model, after);
                for (int j = 0; j < canonical.Length; j++)
                {
                    int k = Array.IndexOf(names, data.Names[j]);
                    AssertBasis(model[j].Basis, Matrix(expected.GetProperty("model_local")[frame][k]));
                    AssertBasis(after[j].Basis, Matrix(expected.GetProperty("model_world")[frame][k]));
                    Assert.InRange(before[j].Origin.DistanceTo(after[j].Origin), 0, 0.00001f);
                }
                conversion.ToCanonical(model, model);
                for (int j = 0; j < canonical.Length; j++)
                {
                    int k = Array.IndexOf(names, data.Names[j]);
                    // Kimodo's float-matrix round trip itself drifts up to 1.65e-5 on these deep finger chains.
                    // Its source offsets have up to 7.29e-7 orthogonality residual; this is arithmetic, not posture.
                    AssertBasis(model[j].Basis, Matrix(expected.GetProperty("canonical_local")[frame][k]), 0.00003f);
                    AssertBasis(model[j].Basis, canonical[j].Basis);
                    Assert.InRange(model[j].Origin.DistanceTo(canonical[j].Origin), 0, 0.00001f);
                }
                frame++;
            }
        }
    }

    private static Basis Matrix(JsonElement m) => new(
        new Vector3(m[0][0].GetSingle(), m[1][0].GetSingle(), m[2][0].GetSingle()),
        new Vector3(m[0][1].GetSingle(), m[1][1].GetSingle(), m[2][1].GetSingle()),
        new Vector3(m[0][2].GetSingle(), m[1][2].GetSingle(), m[2][2].GetSingle()));

    private static void AssertBasis(Basis actual, Basis expected, float allowance = 0.00001f)
    {
        Assert.InRange(actual.X.DistanceTo(expected.X), 0, allowance);
        Assert.InRange(actual.Y.DistanceTo(expected.Y), 0, allowance);
        Assert.InRange(actual.Z.DistanceTo(expected.Z), 0, allowance);
    }

    private static string ResolveInferenceInput(string variable, string defaultResPath)
    {
        string configured = System.Environment.GetEnvironmentVariable(variable) ?? string.Empty;
        string resolved = ProjectSettings.GlobalizePath(string.IsNullOrWhiteSpace(configured) ? defaultResPath : configured);
        Assert.True(File.Exists(resolved), $"{variable} target does not exist: {resolved}");
        return resolved;
    }

    /// <summary>Real authored source windows exercise the Godot-hosted CUDA binding and complete codec.</summary>
    [Fact]
    public async Task AuthoredWindows_GenerateFiniteTrimmedMotion()
    {
        var data = new SomaMotionData(ProjectSettings.GlobalizePath("res://data/motionbricks/poses.json"));
        // Production defaults keep this suite-runnable without environment setup; explicit overrides still win.
        string path = ResolveInferenceInput("MOTIONBRICKS_MODEL", "res://models/motionbricks/onnx/mmm_v6_soma_fp32.onnx");
        string frames = ResolveInferenceInput("MOTIONBRICKS_FRAMES", "res://data/motionbricks/model-frames.json");
        Transform3D[][] context = [.. data.Idle.Take(4).Select(p => (Transform3D[])p.Clone())];
        var source = new ReviewedMotionSource(ProjectSettings.GlobalizePath("res://data/motionbricks/reviewed-feminine/landing-ablation-01.json"), data);
        var raw = new Transform3D[data.Names.Length * 4];
        source.CopyWindow(source.GetRole("feminine", "normal", "forward"), 58, raw);
        Transform3D[][] target = [.. Enumerable.Range(0, 4).Select(f => raw.AsSpan(f * data.Names.Length, data.Names.Length).ToArray())];
        foreach (Transform3D[] pose in target)
        {
            pose[0].Origin += new Vector3(0, 0, 0.9f);
        }
        SomaInference? engine = null;
        using var worker = new SomaInferenceWorker(() => engine = new SomaInference(data, path, frames));
        try
        {
            long request = worker.Submit(context, target);
            SomaInferenceWorker.Result? result = await Task.Run(() =>
            {
                SomaInferenceWorker.Result? value = null;
                // Cold budget: a fresh process pays CUDA context creation plus the FP32 model load before the
                // first generation; observed to exceed the previous 30s cap. Pure wait headroom — the
                // completion assertions are unchanged.
                Assert.True(SpinWait.SpinUntil(() => worker.TryTake(out value), TimeSpan.FromSeconds(180)),
                    "CUDA worker did not return within the model-load/generation budget.");
                return value;
            });
            Assert.NotNull(result);
            Assert.Equal(request, result.Request);
            Assert.Null(result.Error);
            Transform3D[][] output = Assert.IsType<Transform3D[][]>(result.Poses);
            Assert.Equal((engine!.LastTokenCount * 4) - 6, output.Length);
            Assert.All(output, pose => Assert.All(pose, joint => Assert.True(joint.IsFinite())));
            Assert.True(output[0][0].IsEqualApprox(context[3][0]));
            Assert.True(output[^1][0].IsEqualApprox(target[0][0]));
        }
        finally
        {
            worker.Dispose();
            await worker.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Independent Blender physical landmarks and presentation-frame rotations, including constrained helpers and the full rig.</summary>
    [Fact]
    public async Task CalibratedForward_PreservesBlenderPhysicalPose()
    {
        SomaMotionData data = Data();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        Skeleton3D skeleton = scene.GetNode<Skeleton3D>("NavigationTestNpc/Female/GeneralSkeleton");
        skeleton.GetParent().RemoveChild(skeleton);
        // The adapter now accepts canonical world poses; retain the actual Female container orientation.
        skeleton.Basis = SomaMotionData.ModelToWorld;
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Root.AddChild(skeleton);
        try
        {
            var adapter = new CalibratedSomaRig(data, skeleton);
            Basis axis = SomaMotionData.BlenderToModel;
            using var oracle = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://temp/motionbricks/imported-rest-fix/blender-oracle.json")));
            using var cases = JsonDocument.Parse(File.ReadAllText(ProjectSettings.GlobalizePath("res://temp/motionbricks/arm-boundary/cases.json")));
            MeshInstance3D body = skeleton.GetNode<MeshInstance3D>("Female_body");
            Skin skin = body.Skin;
            var binds = Enumerable.Range(0, skin.GetBindCount()).ToDictionary(i => skin.GetBindName(i).ToString(), skin.GetBindPose);
            Transform3D[]? skinReady = null;
            skeleton.SkeletonUpdated += () => skinReady = [.. Enumerable.Range(0, skeleton.GetBoneCount()).Select(skeleton.GetBoneGlobalPose)];
            foreach (JsonElement sample in cases.RootElement.EnumerateArray())
            {
                string id = sample.GetProperty("id").GetString()!;
                Transform3D[] pose = [.. sample.GetProperty("pose").EnumerateArray().Select(p => new Transform3D(
                    new Basis(new Quaternion(p[3].GetSingle(), p[4].GetSingle(), p[5].GetSingle(), p[6].GetSingle()).Normalized()),
                    new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())))];
                JsonElement target = oracle.RootElement.GetProperty("cases").GetProperty(id).GetProperty("target");
                skinReady = null;
                adapter.Apply(pose);
                await Support.TestUtils.WaitForFramesAsync(tree, 2);
                Assert.NotNull(skinReady);
                Vector3 hips = skinReady[skeleton.FindBone("Hips")].Origin;
                Vector3 blenderHips = SomaMotionData.ReadMatrix(target.GetProperty("Hips").GetProperty("matrix")).Origin;
                float scale = skeleton.MotionScale / 0.938770651817322f;
                foreach (JsonProperty bone in target.EnumerateObject())
                {
                    Transform3D blenderPose = SomaMotionData.ReadMatrix(bone.Value.GetProperty("matrix"));
                    Transform3D actual = skinReady[skeleton.FindBone(bone.Name)];
                    Vector3 expectedPosition = axis * (blenderPose.Origin - blenderHips) * scale;
                    float positionError = (actual.Origin - hips).DistanceTo(expectedPosition);
                    // 0.1 mm is arithmetic fidelity of the independently evaluated/exported rig, not a gait bound.
                    Assert.True(positionError < 0.0001f, $"{id} {bone.Name}: physical landmark error {positionError} m");
                }
                foreach (JsonProperty bone in target.EnumerateObject())
                {
                    Transform3D blenderPose = SomaMotionData.ReadMatrix(bone.Value.GetProperty("matrix"));
                    Transform3D blenderRest = SomaMotionData.ReadMatrix(bone.Value.GetProperty("rest"));
                    Transform3D actual = skinReady[skeleton.FindBone(bone.Name)];
                    Basis expectedDeformation = (axis * blenderPose.Basis * blenderRest.Basis.Inverse() * axis.Inverse()).Orthonormalized();
                    // Compare rigid orientations, not a sheared skin matrix: imported thumb binds have
                    // anisotropic scale (about 0.999392 on two axes). Remove scale in the reference frame first.
                    Basis presentationFrame = binds[bone.Name].AffineInverse().Basis.Orthonormalized();
                    Basis actualDeformation = (actual.Basis.Orthonormalized() * presentationFrame.Inverse()).Orthonormalized();
                    float error = Math.Max(actualDeformation.X.DistanceTo(expectedDeformation.X), Math.Max(actualDeformation.Y.DistanceTo(expectedDeformation.Y), actualDeformation.Z.DistanceTo(expectedDeformation.Z)));
                    Assert.True(error < 0.0001f, $"{id} {bone.Name}: skin deformation basis error {error}");
                }
            }
        }
        finally
        {
            skeleton.Free();
            scene.Free();
        }
    }

    /// <summary>Changing actor yaw and world placement must rotate physical landmarks and observed root together.</summary>
    [Fact]
    public async Task ActorYaw_PreservesPhysicalPoseAndWorldFeedback()
    {
        SomaMotionData data = Data();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        Skeleton3D skeleton = scene.GetNode<Skeleton3D>("NavigationTestNpc/Female/GeneralSkeleton");
        skeleton.GetParent().RemoveChild(skeleton);
        scene.Free();
        skeleton.Basis = SomaMotionData.ModelToWorld;
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Root.AddChild(skeleton);
        try
        {
            var rig = new CalibratedSomaRig(data, skeleton);
            var before = new Transform3D[data.Names.Length];
            var after = new Transform3D[data.Names.Length];
            Vector3[]? landmarks = null;
            string[] names = ["Hips", "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot"];
            skeleton.SkeletonUpdated += () =>
            {
                rig.Observe(after);
                landmarks = [.. names.Select(n => skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(skeleton.FindBone(n)).Origin)];
            };
            rig.Apply(data.Idle[0]);
            await Support.TestUtils.WaitForFramesAsync(tree, 2);
            Assert.NotNull(landmarks);
            after.CopyTo(before, 0);
            Vector3[] original = landmarks;
            Basis yaw = new(Vector3.Up, 0.7f);
            Vector3 translation = new(2, 0, -1);
            skeleton.GlobalTransform = new Transform3D(yaw * SomaMotionData.ModelToWorld, translation);
            var input = (Transform3D[])data.Idle[0].Clone();
            input[0] = new Transform3D(yaw * input[0].Basis,
                (yaw * input[0].Origin) + (SomaMotionData.ModelToWorld.Inverse() * translation));
            rig.Apply(input);
            await Support.TestUtils.WaitForFramesAsync(tree, 2);
            for (int i = 0; i < original.Length; i++)
            {
                Assert.InRange(landmarks![i].DistanceTo((yaw * original[i]) + translation), 0, 0.0001f);
            }
            AssertBasis(after[0].Basis, yaw * before[0].Basis, 0.0001f);
            Assert.InRange(after[0].Origin.DistanceTo((yaw * before[0].Origin)
                + (SomaMotionData.ModelToWorld.Inverse() * translation)), 0, 0.0001f);
        }
        finally { skeleton.Free(); }
    }

    /// <summary>Irregular observations must interpolate the prescribed 0.1-second context, not frame indices.</summary>
    [Fact]
    public void RealisedHistory_ResamplesFourPosesAtThirtyHz()
    {
        var history = new RealisedPoseHistory(1);
        foreach (double time in new[] { 0.0, 0.025, 0.05, 0.085, 0.12, 0.15 })
        {
            history.Record(time, [new Transform3D(Basis.Identity, new Vector3((float)(time * 2), 1, 0))]);
        }
        Transform3D[][] context = history.Context();
        for (int i = 0; i < 4; i++)
        {
            Assert.InRange(Math.Abs(context[i][0].Origin.X - (2 * (0.05 + (i / 30.0)))), 0, 0.000001);
        }
        Assert.Equal(6, history.SamplesRecorded);
    }

    /// <summary>Endpoint treatment uses world-linear root offsets, and exactly the supplied local endpoint poses.</summary>
    [Fact]
    public void EndpointCorrection_MatchesIndependentGuideEndpoints()
    {
        Transform3D[][] frames = [.. Enumerable.Range(0, 5).Select(i => new[] { new Transform3D(new Basis(Vector3.Up, i * 0.1f), new Vector3(i, 1, 0)), Transform3D.Identity })];
        Transform3D[] start = [new(new Basis(Vector3.Up, 0.4f), new Vector3(3, 1, 2)), new(new Basis(Vector3.Right, 0.2f), Vector3.Up)];
        Transform3D[] end = [new(new Basis(Vector3.Up, 1.2f), new Vector3(9, 1, 5)), new(new Basis(Vector3.Right, 0.6f), Vector3.Up)];
        SomaInference.CorrectEndpoints(frames, start, end);
        for (int j = 0; j < 2; j++)
        {
            Assert.True(frames[0][j].IsEqualApprox(start[j]));
            Assert.True(frames[^1][j].IsEqualApprox(end[j]));
        }
        Assert.True(frames[2][0].Origin.IsEqualApprox(new Vector3(6, 1, 3.5f)));
    }

    /// <summary>A target-side change reaches the context; missing Neck2 remains rest-local rather than cached input.</summary>
    [Fact]
    public async Task SkinReadyFeedback_ObservesTargetPerturbation()
    {
        SomaMotionData data = Data();
        Node scene = GD.Load<PackedScene>("res://assets/testing/navigation/navigation_test.tscn").Instantiate();
        Skeleton3D skeleton = scene.GetNode<Skeleton3D>("NavigationTestNpc/Female/GeneralSkeleton");
        skeleton.GetParent().RemoveChild(skeleton);
        scene.Free();
        var holder = new Node3D { Transform = new Transform3D(SomaMotionData.ModelToWorld, new Vector3(1, 0, -2)) };
        holder.AddChild(skeleton);
        var tree = (SceneTree)Engine.GetMainLoop();
        tree.Root.AddChild(holder);
        try
        {
            skeleton.ModifierCallbackModeProcess = Skeleton3D.ModifierCallbackModeProcessEnum.Manual;
            var adapter = new CalibratedSomaRig(data, skeleton);
            var observed = new Transform3D[data.Names.Length];
            var history = new RealisedPoseHistory(data.Names.Length);
            int observations = 0;
            void Observe()
            {
                adapter.Observe(observed);
                history.Record(++observations / 60.0, observed);
            }

            skeleton.SkeletonUpdated += Observe;
            adapter.Apply(data.Idle[0]);
            skeleton.Advance(1.0 / 30);
            await Support.TestUtils.WaitForFramesAsync(tree, 2);
            foreach ((string target, string source) in new[] { ("Head", "Head"), ("LeftLowerArm", "LeftForeArm"), ("LeftHand", "LeftHand") })
            {
                var before = new Transform3D[data.Names.Length];
                data.WorldPose(observed, before);
                int bone = skeleton.FindBone(target);
                int parent = skeleton.GetBoneParent(bone);
                Basis changed = new Basis(Vector3.Up, 0.125f) * skeleton.GetBoneGlobalPose(bone).Basis;
                skeleton.SetBonePoseRotation(bone, (skeleton.GetBoneGlobalPose(parent).Basis.Inverse() * changed).GetRotationQuaternion());
                skeleton.Advance(1.0 / 30);
                await Support.TestUtils.WaitForFramesAsync(tree, 2);
                var after = new Transform3D[data.Names.Length];
                data.WorldPose(observed, after);
                int sourceBone = Array.IndexOf(data.Names, source);
                Basis expected = new Basis(Vector3.Up, 0.125f) * before[sourceBone].Basis;
                AssertBasis(after[sourceBone].Basis, expected);
                // A particular basis axis can align with the perturbation axis; compare the complete orientation.
                Assert.True(Math.Max(after[sourceBone].Basis.X.DistanceTo(before[sourceBone].Basis.X),
                    Math.Max(after[sourceBone].Basis.Y.DistanceTo(before[sourceBone].Basis.Y), after[sourceBone].Basis.Z.DistanceTo(before[sourceBone].Basis.Z))) > 0.05f);
                AssertBasis(history.Context()[3][sourceBone].Basis, observed[sourceBone].Basis);
            }
            Assert.True(observed[Array.IndexOf(data.Names, "Neck2")].Basis.IsEqualApprox(Basis.Identity));
            Assert.InRange(observed[0].Origin.Z, 1.9f, 2.1f);
            skeleton.SkeletonUpdated -= Observe;
        }
        finally
        {
            holder.Free();
        }
    }
}
