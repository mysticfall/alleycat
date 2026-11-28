using System.Diagnostics;
using System.Text.Json;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Navigation;
using AlleyCat.Speech.Generation;
using AlleyCat.Speech.LipSync;
using AlleyCat.Speech.Voice;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Models.NativeModelLoadMonitor;

namespace AlleyCat.IntegrationTests.Models;

/// <summary>
/// Acceptance regression for the reported per-NPC duplication of native AI models (MotionBricks and Supertonic 3):
/// real NPC templates must not each load their own copy of a model. The criterion is one native load per model key,
/// whatever the number of NPCs.
/// </summary>
/// <remarks>
/// <para>
/// Each NPC completes a real CUDA MotionBricks generation and a real Supertonic synthesis through the production
/// wiring; no session factory or pipeline override is installed. Native loads are counted below every managed caller
/// (see <see cref="NativeModelLoadMonitor" />), so the criterion does not depend on how consumers reach a model. The
/// native file-open count is cross-checked against the ONNX Runtime CUDA arena count, which also stops a CPU fallback
/// of a CUDA model from passing silently.
/// </para>
/// <para>
/// Each fact configures the process-wide ONNX Runtime environment and counts model-file opens by every process on
/// the host, so each needs its own fresh Godot process with no other running. The facts are therefore
/// <see cref="IsolatedProcessAttribute" />: default, class and exact-method runs all include them, and each runs in
/// its own session with no other session alive. Each fact first checks for Godot processes the framework did not
/// launch and reports itself skipped, naming them, if any is running.
/// </para>
/// <para>
/// Raw measurements are written to <c>res://temp/shared-ai-models/&lt;fact&gt;/</c> before the assertions run.
/// Headless is valid: the measured behaviour is native model loading, which needs no renderer.
/// </para>
/// </remarks>
[Headless]
[IsolatedProcess]
public sealed partial class SharedModelLoadingTests
{
    private const string NpcFile = "res://assets/testing/navigation/navigation_test_npc.tscn";
    private const string ModelsDirectory = "res://models";
    private const string MotionBricksKey = "motionbricks/onnx/mmm_v6_soma_fp32.onnx";
    private const string Wav2ArkitKey = "wav2arkit_cpu/wav2arkit_cpu.onnx";
    private const string Utterance = "Hello there. I am checking that we all share one voice model.";
    // Cold CUDA context creation plus a 0.76 GB FP32 load per consumer; wait headroom only, not a criterion.
    private static readonly TimeSpan _completionBudget = TimeSpan.FromSeconds(300);

    private static readonly string[] _supertonicKeys =
    [
        "supertonic-3/onnx/duration_predictor.onnx",
        "supertonic-3/onnx/text_encoder.onnx",
        "supertonic-3/onnx/vector_estimator.onnx",
        "supertonic-3/onnx/vocoder.onnx",
    ];

    private static readonly Vector3[] _spawnPositions = [new(-2, 0, -3), new(0, 0, -3), new(2, 0, -3)];

    /// <summary>
    /// Starting with one NPC and then adding two more (loading concurrently) must not load any model again.
    /// </summary>
    [Fact]
    public Task AddingNpcs_ReusesLoadedModels() =>
        RunAsync(nameof(AddingNpcs_ReusesLoadedModels), [1, 2]);

    /// <summary>
    /// Three NPCs entering the scene together must load each model exactly once.
    /// </summary>
    [Fact]
    public Task ThreeNpcsAtSceneLoad_LoadEachModelOnce() =>
        RunAsync(nameof(ThreeNpcsAtSceneLoad_LoadEachModelOnce), [3]);

    /// <summary>
    /// Three NPCs entering the scene together each move, speak through their voice and lip-sync through the real
    /// Wav2Arkit model, with every model, Wav2Arkit included, loaded exactly once (CORE-011-UAC-001, UAC-002).
    /// </summary>
    /// <remarks>
    /// The shipped templates lip-sync through the remote A2F player; this fact swaps in the local
    /// <see cref="Wav2ArkitLipSyncPlayer" /> on each test instance only, so the lip-sync model is a shared local model.
    /// Wav2Arkit runs on the CPU, so it is excluded from the CUDA cross-check and counted on its own.
    /// </remarks>
    [Fact]
    public Task ThreeNpcsAtSceneLoad_MoveSpeakAndLipSyncWithEachModelLoadedOnce() =>
        RunAsync(nameof(ThreeNpcsAtSceneLoad_MoveSpeakAndLipSyncWithEachModelLoadedOnce), [3], lipSync: true);

    private static async Task RunAsync(string fact, int[] phases, bool lipSync = false)
    {
        HostIsolation.RequireNoOtherGodotProcess();
        SceneTree tree = TestUtils.GetSceneTree();
        Dictionary<string, string> files = ModelFiles();
        Assert.Contains(MotionBricksKey, files.Keys);
        Assert.All(_supertonicKeys, key => Assert.Contains(key, files.Keys));
        if (lipSync)
        {
            Assert.Contains(Wav2ArkitKey, files.Keys);
        }

        using NativeModelLoadMonitor monitor = Install(files);
        var phaseRecords = new List<object>();
        var npcs = new List<NpcProbe>();
        var clock = Stopwatch.StartNew();

        var root = new ProbeGame { Name = "SharedModelLoading" };
        Node3D scene = CreateFixture();
        void Configure(Node node)
        {
            if (node is not MotionBricksNavigation navigation)
            {
                return;
            }

            NpcProbe? owner = npcs.FirstOrDefault(n => n.Actor.IsAncestorOf(navigation));
            if (owner is null)
            {
                return;
            }

            owner.Navigation = navigation;
            // Passive worker-thread sink captured before worker start; it never replaces the loading path.
            navigation.ReturnedMotion = result =>
            {
                if (result.Error is null && result.Poses is { Length: > 0 })
                {
                    _ = Interlocked.Increment(ref owner.Generations);
                }
                else
                {
                    owner.LastMotionError = result.Error?.GetType().Name ?? "empty";
                }
            };
        }

        tree.NodeAdded += Configure;
        try
        {
            for (int phase = 0; phase < phases.Length; phase++)
            {
                List<NpcProbe> added = AddNpcs(scene, npcs, phases[phase]);
                if (phase == 0)
                {
                    root.AddChild(scene);
                    tree.Root.AddChild(root);
                }

                await TestUtils.WaitForPhysicsFramesAsync(tree, 40);
                await GenerateMotionAsync(tree, added);
                await SynthesiseAsync(added);
                phaseRecords.Add(new
                {
                    phase = phase + 1,
                    addedNpcs = added.Count,
                    totalNpcs = npcs.Count,
                    elapsedSeconds = clock.Elapsed.TotalSeconds,
                    cudaArenas = monitor.CudaArenas,
                    loggedSessions = monitor.LoggedSessions,
                    modelFileOpens = monitor.SnapshotOpens(),
                });
            }

            if (lipSync)
            {
                await SpeakWithLipSyncAsync(tree, npcs);
            }
        }
        finally
        {
            tree.NodeAdded -= Configure;
            foreach (NpcProbe npc in npcs)
            {
                npc.NavigationPhase = npc.Navigation?.Phase;
                npc.NavigationFailure = npc.Navigation?.Failure;
            }

            Task?[] workers = [.. npcs.Select(n => n.Navigation?.WorkerCompletion)];
            if (root.IsInsideTree())
            {
                await root.DrainMotionAndDiagnosticsAsync();
            }

            root.QueueFree();
            await TestUtils.WaitForPhysicsFramesAsync(tree, 2);
            foreach (Task? worker in workers)
            {
                if (worker is not null)
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }

        // Disposing joins the inotify reader after a final drain, so the counts below are complete.
        monitor.Dispose();
        IReadOnlyDictionary<string, int> opens = monitor.SnapshotOpens();
        IReadOnlyDictionary<string, int> closes = monitor.SnapshotCloses();
        // The kernel can merge two near-simultaneous opens (or closes) of one file into one event, so each is a lower
        // bound; the larger is the load count, and the CUDA arena cross-check below proves it complete.
        var loads = opens.ToDictionary(p => p.Key, p => Math.Max(p.Value, closes[p.Key]), StringComparer.Ordinal);
        WriteArtefact(fact, new
        {
            fact,
            scenario = "Flat navigation fixture with real navigation_test_npc instances (production reference_female_npc template), real CUDA " +
                "MotionBricks generation then real Supertonic synthesis per NPC; no factory overrides.",
            phases,
            processId = System.Environment.ProcessId,
            npcs = npcs.Select(n => new
            {
                n.Name,
                n.Generations,
                n.LastMotionError,
                n.DestinationResult,
                n.NavigationPhase,
                n.NavigationFailure,
                n.AudioBytes,
                n.SpeechError,
                n.LipSync,
            }),
            cudaArenas = monitor.CudaArenas,
            loggedSessions = monitor.LoggedSessions,
            modelFileOpens = opens,
            modelFileCloses = closes,
            modelLoads = loads,
            phaseRecords,
        });

        // Preconditions: without these a red result would reflect setup, not the reported symptom.
        Assert.All(npcs, n => Assert.True(n.Generations > 0,
            $"{n.Name} completed no MotionBricks generation (last error {n.LastMotionError}, " +
            $"failure {n.NavigationFailure})."));
        Assert.All(npcs, n => Assert.True(n.AudioBytes > 44, $"{n.Name} produced no Supertonic audio: {n.SpeechError}"));
        Assert.True(monitor.CudaArenas > 0, "No CUDA execution provider was created.");
        // Every CUDA model load creates one CUDA arena, so loads without a matching arena mean a CUDA model fell back
        // to the CPU (or a load went unobserved). Wav2Arkit is a CPU model, so its load has no CUDA execution provider.
        int cudaLoads = loads.Values.Sum() - (lipSync ? loads[Wav2ArkitKey] : 0);
        Assert.True(cudaLoads == monitor.CudaArenas,
            $"Observed model loads ({cudaLoads}) do not match CUDA execution providers ({monitor.CudaArenas}); " +
            $"a load was unobserved or ran without CUDA. Opens {Json(opens)}, closes {Json(closes)}.");

        // Symptom criteria.
        var failures = new List<string>();
        foreach (string key in _supertonicKeys.Prepend(MotionBricksKey))
        {
            if (loads[key] != 1)
            {
                failures.Add($"{key} loaded {loads[key]} times for {npcs.Count} NPCs (expected once).");
            }
        }

        Assert.True(failures.Count == 0, string.Join(System.Environment.NewLine, failures));

        if (lipSync)
        {
            AssertLipSync(npcs, loads[Wav2ArkitKey]);
        }
    }

    private static void AssertLipSync(List<NpcProbe> npcs, int wav2ArkitLoads)
    {
        // Preconditions: each NPC's lip-sync player initialised on the real model and its speech played.
        Assert.All(npcs, n =>
        {
            LipSyncRecord record = n.LipSync ?? throw new InvalidOperationException($"{n.Name} has no lip-sync record.");
            Assert.True(record.Initialised, $"{n.Name} lip-sync player did not initialise: {record.InitialisationError}");
            Assert.True(string.IsNullOrEmpty(record.PlaybackError), $"{n.Name} lip-sync playback failed: {record.PlaybackError}");
            Assert.True(record.MappedMeshCount > 0 && record.MappedChannelCount > 0,
                $"{n.Name} lip-sync mapped {record.MappedChannelCount} channels on {record.MappedMeshCount} meshes.");
        });

        // Criteria: every NPC's face receives blendshape output, and Wav2Arkit loads once for all of them.
        Assert.All(npcs, n => Assert.True(
            n.LipSync!.AppliedFrameCount > 0 && n.LipSync.WeightChangeEventCount > 0 && n.LipSync.MaxObservedWeightDelta > 0,
            $"{n.Name} applied no lip-sync blendshape output ({n.LipSync.AppliedFrameCount} frames, " +
            $"{n.LipSync.WeightChangeEventCount} weight changes, max delta {n.LipSync.MaxObservedWeightDelta})."));
        Assert.True(wav2ArkitLoads == 1, $"{Wav2ArkitKey} loaded {wav2ArkitLoads} times for {npcs.Count} NPCs (expected once).");
    }

    private static async Task SpeakWithLipSyncAsync(SceneTree tree, List<NpcProbe> npcs)
    {
        // All players request the shared session in the same frame.
        npcs.ForEach(UseWav2ArkitLipSync);
        var wait = Stopwatch.StartNew();
        while (npcs.Any(n => !n.LipSyncPlayer!.IsInitialised && n.LipSyncPlayer.InitialisationError.Length == 0)
            && wait.Elapsed < _completionBudget)
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
        }

        foreach (NpcProbe npc in npcs.Where(n => n.LipSyncPlayer!.IsInitialised))
        {
            await npc.Voice!.SpeakAsync(Utterance);
        }

        static bool Settled(Wav2ArkitLipSyncPlayer player)
        {
            return !player.IsInitialised
                || player.PlaybackError.Length > 0
                || (player.AppliedFrameCount > 0 && player.WeightChangeEventCount > 0);
        }

        while (!npcs.All(n => Settled(n.LipSyncPlayer!)) && wait.Elapsed < _completionBudget)
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
        }

        foreach (NpcProbe npc in npcs)
        {
            Wav2ArkitLipSyncPlayer player = npc.LipSyncPlayer!;
            npc.LipSync = new LipSyncRecord(
                player.IsInitialised,
                player.InitialisationError,
                player.PlaybackError,
                player.MappedMeshCount,
                player.MappedChannelCount,
                player.AppliedFrameCount,
                player.WeightChangeEventCount,
                player.MaxObservedWeightDelta);
        }
    }

    // Test instances only: replaces the template's remote A2F player with the local shared-model Wav2Arkit player,
    // keeping the voice's skeleton and audio wiring.
    // The role template carrying the voice installs after tree entry, so the swap happens on the live NPC; the voice
    // reads its lip-sync player per utterance.
    private static void UseWav2ArkitLipSync(NpcProbe npc)
    {
        AIVoice voice = Descendants(npc.Actor).OfType<AIVoice>().Single();
        LipSyncPlayer template = voice.LipSyncPlayer
            ?? throw new InvalidOperationException($"{npc.Name} voice has no lip-sync player.");
        var player = new Wav2ArkitLipSyncPlayer
        {
            Name = template.Name,
            Skeleton = template.Skeleton,
            AudioPlayer = template.AudioPlayer,
        };
        Node parent = template.GetParent();
        int index = template.GetIndex();
        parent.RemoveChild(template);
        template.QueueFree();
        parent.AddChild(player);
        parent.MoveChild(player, index);
        voice.LipSyncPlayer = player;
        npc.Voice = voice;
        npc.LipSyncPlayer = player;
    }

    private static List<NpcProbe> AddNpcs(Node scene, List<NpcProbe> npcs, int count)
    {
        var added = new List<NpcProbe>();
        for (int i = 0; i < count; i++)
        {
            int index = npcs.Count;
            AlleyCat.Character.Character actor = GD.Load<PackedScene>(NpcFile).Instantiate<AlleyCat.Character.Character>();
            actor.Name = $"Npc{index + 1}";
            actor.Id = $"shared_model_npc_{index + 1}";
            actor.Position = _spawnPositions[index];
            var probe = new NpcProbe(actor);
            npcs.Add(probe);
            added.Add(probe);
            scene.AddChild(actor);
        }

        return added;
    }

    // Flat walkable floor only. The navigation playtest scene is avoided because its recorder replaces the
    // navigation's motion sink and writes playtest recordings.
    private static Node3D CreateFixture()
    {
        var fixture = new Node3D { Name = "SharedModelFixture" };
        var ground = new StaticBody3D { Name = "Ground" };
        ground.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(12, 0.1f, 12) },
            Position = new Vector3(0, -0.05f, 0),
        });
        fixture.AddChild(ground);
        var mesh = new NavigationMesh
        {
            Vertices = [new(-5.8f, 0, -5.8f), new(5.8f, 0, -5.8f), new(5.8f, 0, 5.8f), new(-5.8f, 0, 5.8f)],
        };
        mesh.AddPolygon([0, 1, 2, 3]);
        fixture.AddChild(new NavigationRegion3D { Name = "NavigationRegion3D", NavigationMesh = mesh });
        return fixture;
    }

    private static async Task GenerateMotionAsync(SceneTree tree, List<NpcProbe> npcs)
    {
        var wait = Stopwatch.StartNew();
        while (npcs.Any(n => n.Navigation is null || n.Navigation.Phase == "loading"))
        {
            Assert.True(wait.Elapsed < _completionBudget, "MotionBricks navigation did not initialise.");
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
        }

        foreach (NpcProbe npc in npcs)
        {
            MotionBricksNavigation navigation = npc.Navigation!;
            Assert.True(navigation.Phase != "failed", $"{npc.Name} MotionBricks failed: {navigation.Failure}");
            Transform3D goal = npc.Actor.GlobalTransform;
            goal.Origin += new Vector3(0, 0, 1.5f);
            npc.DestinationResult = navigation.SelectDestination(goal, []);
        }

        while (npcs.Any(n => Volatile.Read(ref n.Generations) == 0) && wait.Elapsed < _completionBudget)
        {
            await TestUtils.WaitForPhysicsFramesAsync(tree, 1);
        }
    }

    private static async Task SynthesiseAsync(List<NpcProbe> npcs)
    {
        static async Task Speak(NpcProbe npc)
        {
            SupertonicSpeechGenerator? generator = Descendants(npc.Actor).OfType<SupertonicSpeechGenerator>().FirstOrDefault();
            if (generator is null)
            {
                npc.SpeechError = "No SupertonicSpeechGenerator installed.";
                return;
            }

            try
            {
                byte[] audio = await generator.Generate(Utterance).WaitAsync(_completionBudget);
                npc.AudioBytes = audio.Length;
            }
            catch (Exception error)
            {
                npc.SpeechError = error.ToString();
            }
        }

        await Task.WhenAll(npcs.Select(Speak));
    }

    private static IEnumerable<Node> Descendants(Node node)
    {
        foreach (Node child in node.GetChildren())
        {
            yield return child;
            foreach (Node descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static Dictionary<string, string> ModelFiles()
    {
        string directory = ProjectSettings.GlobalizePath(ModelsDirectory);
        // Depth 2 reaches <model>/onnx/*.onnx without following self-referencing model symlinks further.
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 };
        return Directory.EnumerateFiles(directory, "*.onnx", options)
            .ToDictionary(p => Path.GetRelativePath(directory, p).Replace('\\', '/'), p => p, StringComparer.Ordinal);
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static void WriteArtefact(string fact, object report)
    {
        string directory = ProjectSettings.GlobalizePath($"res://temp/shared-ai-models/{fact}");
        _ = Directory.CreateDirectory(directory);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.json"), json);
        File.WriteAllText(Path.Combine(directory, "latest.json"), json);
    }

    private sealed class NpcProbe(AlleyCat.Character.Character actor)
    {
        public AlleyCat.Character.Character Actor { get; } = actor;
        public string Name { get; } = actor.Name;
        public MotionBricksNavigation? Navigation
        {
            get; set;
        }
        public int Generations;
        public string? LastMotionError
        {
            get; set;
        }
        public int DestinationResult { get; set; } = -1;
        public string? NavigationPhase
        {
            get; set;
        }
        public string? NavigationFailure
        {
            get; set;
        }
        public int AudioBytes
        {
            get; set;
        }
        public string? SpeechError
        {
            get; set;
        }
        public AIVoice? Voice
        {
            get; set;
        }
        public Wav2ArkitLipSyncPlayer? LipSyncPlayer
        {
            get; set;
        }
        public LipSyncRecord? LipSync
        {
            get; set;
        }
    }

    private sealed record LipSyncRecord(
        bool Initialised,
        string InitialisationError,
        string PlaybackError,
        int MappedMeshCount,
        int MappedChannelCount,
        int AppliedFrameCount,
        int WeightChangeEventCount,
        float MaxObservedWeightDelta);

    private sealed partial class ProbeGame : Game
    {
        public override void _Ready()
        {
        }
    }
}
