using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AlleyCat.Motion;
using AlleyCat.Navigation.Recording;
using Godot;

namespace AlleyCat.Control;

/// <summary>Opt-in value-only companion to the legacy player input trace; owns no scene nodes.</summary>
internal sealed class PlayerPoseRecording
{
    // A separate static scope prevents C# closure sharing with the player's other (node-owning) lambdas.
    internal static Action<SomaInferenceWorker.Result> ObserveReturns(StrongBox<PlayerPoseRecording?> link) =>
        result => Volatile.Read(ref link.Value)?.Returned(result);

    internal NavigationSessionWriter Writer
    {
        get;
    }
    internal Task Completion => Writer.Completion;
    private readonly Task _identity;
    private readonly Lock _finishGate = new();
    private Task? _finish;

    internal PlayerPoseRecording(string path, object identity, string root, string model)
    {
        Writer = new(path, identity);
        _identity = Task.Run(() =>
        {
            try
            {
                string[] paths = ["data/motionbricks/poses.json", "data/motionbricks/catalogue.json", "data/motionbricks/model-frames.json",
                    "data/motionbricks/reviewed-feminine/set.json", "data/motionbricks/reviewed-feminine/landing-ablation-01.json",
                    "data/motionbricks/reviewed-feminine/turn-angular-reference.json", "src/Control/PlayerMotionBricks.cs",
                    "src/Control/PlayerMotionBricks.InputTrace.cs", "src/Control/PlayerMotionBricks.PoseTrace.cs",
                    "src/Control/WalkingExperimentDriver.cs", "src/Control/PlayerInputTrace.cs", "src/Control/PlayerPoseRecording.cs",
                    "src/Motion/ReviewedRoleSampler.cs", "src/Motion/SourcePhaseClock.cs", "src/Motion/SomaInference.cs",
                    "src/Motion/SomaModelFrames.cs", "src/Motion/NativeSomaRig.cs", "src/Motion/RealisedPoseHistory.cs",
                    "src/Motion/SomaInferenceWorker.cs", "src/IK/PlayerVRIK.cs", "src/Navigation/Recording/NavigationSessionWriter.cs",
                    "assets/characters/templates/reference_female/reference_female_player.tscn"];
                var hashes = new Dictionary<string, string>();
                foreach (string relative in paths)
                {
                    Hash(Path.Combine(root, relative), relative);
                }
                string assemblyFile = typeof(Game).Assembly.Location;
                if (!string.IsNullOrEmpty(assemblyFile))
                {
                    Hash(assemblyFile, "loaded-assembly-file");
                }
                if (!string.IsNullOrEmpty(model))
                {
                    Hash(model, "model-file");
                }
                Record("content-identity", new
                {
                    hashes,
                    model,
                    assemblyFile = string.IsNullOrEmpty(assemblyFile) ? "unavailable: memory-loaded assembly; identity MVID binds loaded build" : assemblyFile,
                    meaning = "SHA256 at capture; loaded assembly MVID is in identity. No git-clean claim."
                });
                void Hash(string file, string key)
                {
                    var before = new FileInfo(file);
                    long length = before.Length;
                    DateTime modified = before.LastWriteTimeUtc;
                    using FileStream stream = File.OpenRead(file);
                    hashes[key] = Convert.ToHexString(SHA256.HashData(stream));
                    var after = new FileInfo(file);
                    if (length != after.Length || modified != after.LastWriteTimeUtc)
                    {
                        Writer.Fault("identity-file-changed");
                    }
                }
            }
            catch (Exception error)
            {
                Record("identity-error", new
                {
                    error = error.ToString()
                });
                Writer.Fault("identity-" + error.GetType().Name);
            }
        });
    }

    internal void Record(string kind, object metadata, int count = 0, Func<float[]>? copy = null) =>
        _ = Writer.Record(kind, metadata, count, copy);

    internal void Motion(string kind, object metadata, params Transform3D[][] poses)
    {
        try
        {
            Record(kind, metadata, checked(poses.Sum(p => p.Length) * 12), () => NavigationRecordingSink.Pack(poses));
        }
        catch (Exception error) { Writer.Fault("motion-" + error.GetType().Name); }
    }

    // Worker callback. No Godot node/scene access; outputs may already be cancelled and must remain identifiable.
    internal void Returned(SomaInferenceWorker.Result result)
    {
        try
        {
            Transform3D[][] poses = result.Poses ?? [];
            Motion("provider-returned", new
            {
                ticket = result.Request,
                clock = Stopwatch.GetTimestamp(),
                shape = poses.Select(p => p.Length).ToArray(),
                error = result.Error?.ToString(),
                stage = "ISession.Generate output: production SomaInference has already corrected endpoints and converted to canonical; synthetic fixtures are labelled separately",
                rawPreEndpoint = "not recorded",
                disposition = "Not proof of playback. Join input-retire/input-shutdown, input-consume and skin-pose by ticket; a returned cancelled ticket is stale-not-played."
            }, poses);
        }
        catch (Exception error) { Writer.Fault("returned-" + error.GetType().Name); }
    }

    internal Task FinishAfterAsync(Task? worker)
    {
        lock (_finishGate)
        {
            return _finish ??= FinishAsync(worker);
        }
    }
    internal Task FinishAfterWorkersAsync() => FinishAfterAsync(null);
    private async Task FinishAsync(Task? worker)
    {
        try
        {
            if (worker is not null)
            {
                await worker.ConfigureAwait(false);
            }
        }
        catch (Exception error) { Record("worker-drain-error", new { error = error.ToString() }); }
        await _identity.ConfigureAwait(false);
        Record("session-finalise", new
        {
            clock = Stopwatch.GetTimestamp(),
            meaning = "Transport end after requested worker drain; not motion success or complete rendered-frame coverage."
        });
        _ = await Writer.FinishAsync().ConfigureAwait(false);
        await Writer.Completion.ConfigureAwait(false);
    }
}
