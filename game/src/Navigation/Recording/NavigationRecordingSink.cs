using System.Diagnostics;
using System.Security.Cryptography;
using AlleyCat.Motion;
using Godot;

namespace AlleyCat.Navigation.Recording;

/// <summary>Value-only native/IO boundary. It deliberately owns no scene nodes.</summary>
internal sealed class NavigationRecordingSink
{
    internal NavigationSessionWriter Writer
    {
        get;
    }
    private readonly Task _identity;

    internal NavigationRecordingSink(string path, object identity, string root, string model)
    {
        Writer = new(path, identity);
        _identity = Task.Run(() =>
        {
            try
            {
                string[] paths = ["assets/testing/navigation/navigation_test.tscn", "assets/testing/navigation/navigation_test_npc.tscn",
                    "assets/animations/motionbricks/poses.json", "assets/animations/motionbricks/model-frames.json",
                    "assets/animations/locomotion/feminine/set.json", "src/Navigation/navigation_playtest.gd",
                    "src/Navigation/MotionBricksNavigation.cs", "src/Navigation/MotionBricksNavigation.Roles.cs",
                    "src/Navigation/MotionBricksNavigation.NativeHandoff.cs", "src/Motion/SomaInference.cs",
                    "src/Navigation/Recording/NavigationPlaytestRecorder.cs", "src/Navigation/Recording/NavigationSessionWriter.cs",
                    "src/Navigation/Recording/NavigationRecordingSink.cs", "src/Motion/SomaInferenceWorker.cs", "src/Game.cs",
                    "assets/characters/templates/reference_female/reference_female_npc.tscn",
                    "src/Navigation/MotionBricksNavigation.RefusalRecording.cs", "src/Navigation/MotionBricksNavigation.Diagnostics.cs",
                    "src/Navigation/NavigationBase.cs", "src/Navigation/NativePathMotionTargets.cs",
                    "src/Navigation/MotionBricksNavigation.PathTargets.cs", "src/Navigation/MotionBricksNavigation.FiniteTurn.cs",
                    "src/Navigation/FiniteTerminalTurnPlan.cs", "src/Motion/StationaryTurnReference.cs", "src/Motion/ReviewedRoleSampler.cs",
                    "assets/animations/locomotion/feminine/turn-angular-reference.json"];
                var hashes = new Dictionary<string, string>();
                foreach (string relative in paths)
                {
                    string file = Path.Combine(root, relative);
                    if (File.Exists(file))
                    {
                        using FileStream stream = File.OpenRead(file);
                        hashes[relative] = Convert.ToHexString(SHA256.HashData(stream));
                    }
                }
                var info = new FileInfo(model);
                _ = Writer.Record("content-identity", new
                {
                    hashes,
                    model = Path.GetFileName(model),
                    modelBytes = info.Exists ? info.Length : 0,
                    modelLastWriteUtc = info.Exists ? info.LastWriteTimeUtc : (DateTime?)null,
                    modelIdentity = "Name/size/mtime only; model bytes not hashed. Assembly MVID identifies loaded build; source hashes describe files at capture, not a clean git HEAD."
                });
            }
            catch (Exception error) { Writer.Fault("identity-" + error.GetType().Name); }
        });
    }

    internal void Returned(SomaInferenceWorker.Result result)
    {
        try
        {
            Transform3D[][] poses = result.Poses ?? [];
            int count = checked(poses.Sum(p => p.Length) * 12);
            _ = Writer.Record("native-returned", new
            {
                ticket = result.Request,
                returnedAt = Stopwatch.GetTimestamp(),
                error = result.Error?.ToString(),
                shape = poses.Select(p => p.Length).ToArray(),
                meaning = "Unmodified provider output, copied before publication. May be stale/unplayed; display and handoff records establish consumption."
            },
                count, () => Pack(poses));
        }
        catch (Exception error) { Writer.Fault("returned-" + error.GetType().Name); }
    }

    internal async Task<bool> CheckpointAsync()
    {
        await _identity.ConfigureAwait(false);
        return await Writer.CheckpointAsync().ConfigureAwait(false);
    }

    internal async Task FinishAfterAsync(Task? worker)
    {
        try
        {
            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
        }
        finally
        {
            await _identity.ConfigureAwait(false);
            _ = Writer.Record("session-finalise", new
            {
                clock = Stopwatch.GetTimestamp(),
                meaning = "Transport end after worker drain, not a navigation success/terminal outcome."
            });
            _ = await Writer.FinishAsync().ConfigureAwait(false);
            await Writer.Completion.ConfigureAwait(false);
        }
    }

    internal static float[] Pack(params Transform3D[][] arrays)
    {
        float[] values = new float[checked(arrays.Sum(a => a.Length) * 12)];
        int offset = 0;
        foreach (Transform3D[] array in arrays)
        {
            foreach (Transform3D transform in array)
            {
                Put(values, ref offset, transform);
            }
        }
        return values;
    }

    internal static void Put(float[] values, ref int offset, Transform3D transform)
    {
        PutVector(values, ref offset, transform.Basis.X);
        PutVector(values, ref offset, transform.Basis.Y);
        PutVector(values, ref offset, transform.Basis.Z);
        PutVector(values, ref offset, transform.Origin);
    }

    private static void PutVector(float[] values, ref int offset, Vector3 value)
    {
        values[offset++] = value.X;
        values[offset++] = value.Y;
        values[offset++] = value.Z;
    }
}
