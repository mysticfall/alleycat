// Portions derived from NVIDIA MotionBricks soma/kernels.py and utils/pose_utils.py.
// Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. Licensed under Apache-2.0.
using System.Diagnostics;
using Godot;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace AlleyCat.Motion;

/// <summary>Single-request SOMA codec and in-process CUDA inference. Call on a worker, not the scene thread.</summary>
public sealed class SomaInference : SomaInferenceWorker.ISession
{
    // Optional passive test observer. Runs on the owning worker; it must never access scene nodes or mutate poses.
    internal Action<Transform3D[][], ReadOnlyMemory<float>>? GenerationObserved
    {
        get; set;
    }
    // Passive evidence boundary before the existing endpoint processing. Worker-only; recipients must copy.
    internal Action<Transform3D[][]>? DecodedObserved
    {
        get; set;
    }
    /// <summary>Joint order from the SOMA N1 model manifest.</summary>
    public static readonly string[] ModelNames = ["Hips", "Spine1", "Spine2", "Chest", "Neck1", "Neck2", "Head", "Jaw", "LeftEye", "RightEye", "LeftShoulder", "LeftArm", "LeftForeArm", "LeftHand", "LeftHandThumbEnd", "LeftHandMiddleEnd", "RightShoulder", "RightArm", "RightForeArm", "RightHand", "RightHandThumbEnd", "RightHandMiddleEnd", "LeftLeg", "LeftShin", "LeftFoot", "LeftToeBase", "RightLeg", "RightShin", "RightFoot", "RightToeBase"];
    private readonly InferenceSession _session;
    private readonly SomaMotionData _data;
    private readonly SomaModelFrames _frames;
    private readonly Transform3D[] _modelRest;
    private readonly int[] _modelToPose;
    private readonly int[] _poseToModel;
    private readonly Action<string>? _nativeObservation;
    /// <inheritdoc/>
    public double LastMilliseconds
    {
        get; private set;
    }
    /// <inheritdoc/>
    public int LastTokenCount
    {
        get; private set;
    }

    /// <inheritdoc/>
    public SomaInference(SomaMotionData data, string modelPath, string modelFramesPath, Action<string>? nativeObservation = null)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("MotionBricks SOMA FP32 model is missing. Provision game/models/motionbricks/onnx or set ModelPath/MOTIONBRICKS_MODEL.", modelPath);
        }
        _nativeObservation = nativeObservation;
        _data = data;
        _frames = new SomaModelFrames(data, modelFramesPath);
        _modelRest = new Transform3D[data.Names.Length];
        _frames.ToModel(data.Rest, _modelRest);
        _modelToPose = [.. ModelNames.Select(n => Array.IndexOf(data.Names, n))];
        if (_modelToPose.Any(i => i < 0))
        {
            throw new InvalidDataException("Source is missing a required SOMA joint.");
        }
        _poseToModel = [.. Enumerable.Repeat(-1, data.Names.Length)];
        for (int i = 0; i < _modelToPose.Length; i++)
        {
            int j = _modelToPose[i];
            if (data.Parents[j] < 0 || _poseToModel[data.Parents[j]] >= 0)
            {
                _poseToModel[j] = i;
            }
        }
        using var options = new SessionOptions();
        options.AppendExecutionProvider_CUDA(0);
        _nativeObservation?.Invoke("load-enter");
        try
        {
            _session = new InferenceSession(modelPath, options);
        }
        finally { _nativeObservation?.Invoke("load-exit"); }
    }

    /// <inheritdoc/>
    public static float Heading(Vector3 right, Vector3 left)
    {
        Vector3 across = right - left;
        across.Y = 0;
        if (across.Length() <= 1e-6f)
        {
            return 0;
        }
        Vector3 forward = Vector3.Up.Cross(across.Normalized());
        return Mathf.Atan2(forward.X, forward.Z);
    }

    /// <inheritdoc/>
    public Transform3D[][] Generate(Transform3D[][] context, Transform3D[][] target)
    {
        if (context.Length != 4 || target.Length != 4)
        {
            throw new ArgumentException("This prototype requires four context and four target poses at 30 Hz.");
        }
        var timer = Stopwatch.StartNew();
        var guides = new Transform3D[8][];
        var world = new Transform3D[8][];
        for (int f = 0; f < 8; f++)
        {
            guides[f] = new Transform3D[_data.Names.Length];
            _frames.ToModel(f < 4 ? context[f] : target[f - 4], guides[f]);
            world[f] = new Transform3D[_data.Names.Length];
            _data.WorldPose(guides[f], world[f]);
        }
        int right = _modelToPose[26], left = _modelToPose[22];
        Vector3 ground = world[3][0].Origin;
        ground.Y = 0;
        var alignment = new Transform3D(new Basis(Vector3.Up, Heading(world[3][right].Origin, world[3][left].Origin) - (Mathf.Pi / 2)), ground);
        Transform3D inverse = alignment.AffineInverse();
        float[] global = new float[40];
        float[] local = new float[32];
        float[] poses = new float[8 * 267];
        for (int f = 0; f < 8; f++)
        {
            int next = f == 3 ? f : Math.Min(f + 1, 7);
            Vector3 hips = inverse * world[f][0].Origin;
            Vector3 nextHips = inverse * world[next][0].Origin;
            float heading = Heading(inverse * world[f][right].Origin, inverse * world[f][left].Origin);
            float nextHeading = Heading(inverse * world[next][right].Origin, inverse * world[next][left].Origin);
            Put(global, f * 5, hips);
            global[(f * 5) + 3] = Mathf.Cos(heading);
            global[(f * 5) + 4] = Mathf.Sin(heading);
            local[f * 4] = Mathf.Atan2(Mathf.Sin(nextHeading - heading), Mathf.Cos(nextHeading - heading)) * 30;
            local[(f * 4) + 1] = (nextHips.X - hips.X) * 30;
            local[(f * 4) + 2] = (nextHips.Z - hips.Z) * 30;
            local[(f * 4) + 3] = hips.Y;
            for (int j = 0; j < 30; j++)
            {
                Transform3D t = inverse * world[f][_modelToPose[j]];
                if (j > 0)
                {
                    Put(poses, (f * 267) + ((j - 1) * 3), t.Origin - new Vector3(hips.X, 0, hips.Z));
                }
                Put(poses, (f * 267) + 87 + (j * 6), t.Basis.X);
                Put(poses, (f * 267) + 90 + (j * 6), t.Basis.Y);
            }
        }
        int[] mask = [1, 1, 1, 1, 1, 1, 1, 1];
        int[] velocityMask = [1, 1, 1, 0, 1, 1, 1, 0];
        int[] allowed = [0, 0, 1, 1, 1, 1, 1, 1, 1, 0, 0];
        NamedOnnxValue[] inputs = [
            NamedOnnxValue.CreateFromTensor("global_root_values", new DenseTensor<float>(global, [1,8,5])),
            NamedOnnxValue.CreateFromTensor("local_root_values", new DenseTensor<float>(local, [1,8,4])),
            NamedOnnxValue.CreateFromTensor("local_poses", new DenseTensor<float>(poses, [1,8,267])),
            NamedOnnxValue.CreateFromTensor("has_global_root_values", new DenseTensor<int>(mask, [1,8])),
            NamedOnnxValue.CreateFromTensor("has_local_root_values", new DenseTensor<int>(velocityMask, [1,8])),
            NamedOnnxValue.CreateFromTensor("has_local_poses", new DenseTensor<int>(mask, [1,8])),
            NamedOnnxValue.CreateFromTensor("allowed_pred_num_tokens", new DenseTensor<int>(allowed, [1,11]))];
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> output = RunNative(inputs);
        int tokens = output.Single(o => o.Name == "pred_num_tokens").AsTensor<int>().First();
        if (tokens is < 8 or > 14)
        {
            throw new InvalidDataException($"Model returned disallowed token count {tokens}.");
        }
        float[] raw = [.. output.Single(o => o.Name == "pred_global_motions").AsTensor<float>()];
        if (raw.Take(tokens * 4 * 366).Any(v => !float.IsFinite(v)))
        {
            throw new InvalidDataException("Model returned non-finite motion.");
        }
        int count = (tokens * 4) - 6;
        var result = new Transform3D[count][];
        for (int f = 0; f < count; f++)
        {
            int offset = (f + 3) * 366;
            result[f] = new Transform3D[_data.Names.Length];
            result[f][0] = alignment * new Transform3D(Rotation(raw, offset + 92), Get(raw, offset));
            for (int j = 1; j < _data.Names.Length; j++)
            {
                int m = _poseToModel[j];
                result[f][j] = m < 0 ? _modelRest[j] : new Transform3D(
                    Rotation(raw, offset + 92 + (_poseToModel[_data.Parents[j]] * 6)).Inverse() * Rotation(raw, offset + 92 + (m * 6)), _modelRest[j].Origin);
            }
        }
        DecodedObserved?.Invoke(result);
        CorrectEndpoints(result, guides[3], guides[4]);
        foreach (Transform3D[] pose in result)
        {
            _frames.ToCanonical(pose, pose);
        }
        LastTokenCount = tokens;
        LastMilliseconds = timer.Elapsed.TotalMilliseconds;
        GenerationObserved?.Invoke(result, raw);
        return result;
    }

    /// <inheritdoc/>
    public static void CorrectEndpoints(Transform3D[][] frames, Transform3D[] start, Transform3D[] end)
    {
        for (int j = 0; j < start.Length; j++)
        {
            Transform3D a = frames[0][j].AffineInverse() * start[j];
            Transform3D b = frames[^1][j].AffineInverse() * end[j];
            Vector3 rootA = start[j].Origin - frames[0][j].Origin;
            Vector3 rootB = end[j].Origin - frames[^1][j].Origin;
            for (int f = 0; f < frames.Length; f++)
            {
                float w = (float)f / (frames.Length - 1);
                Transform3D original = frames[f][j];
                Transform3D corrected = original * SomaMotionData.Blend(Transform3D.Identity, a, 1 - w) * SomaMotionData.Blend(Transform3D.Identity, b, w);
                if (j == 0)
                {
                    corrected.Origin = original.Origin + (rootA * (1 - w)) + (rootB * w);
                }
                frames[f][j] = corrected;
            }
        }
    }

    private static Basis Rotation(float[] values, int index)
    {
        Vector3 x = Get(values, index).Normalized();
        Vector3 z = x.Cross(Get(values, index + 3)).Normalized();
        return x.LengthSquared() < 0.5f || z.LengthSquared() < 0.5f
            ? throw new InvalidDataException("Degenerate model 6D rotation.")
            : new Basis(x, z.Cross(x), z);
    }

    private static Vector3 Get(float[] values, int i) => new(values[i], values[i + 1], values[i + 2]);
    private static void Put(float[] values, int i, Vector3 v)
    {
        values[i] = v.X;
        values[i + 1] = v.Y;
        values[i + 2] = v.Z;
    }
    /// <inheritdoc/>
    public void Dispose()
    {
        _nativeObservation?.Invoke("dispose-enter");
        try
        {
            _session.Dispose();
        }
        finally { _nativeObservation?.Invoke("dispose-exit"); }
    }

    private IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunNative(NamedOnnxValue[] inputs)
    {
        _nativeObservation?.Invoke("generate-enter");
        try
        {
            return _session.Run(inputs);
        }
        finally { _nativeObservation?.Invoke("generate-exit"); }
    }
}
