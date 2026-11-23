using AlleyCat.Models;
using AlleyCat.Speech.LipSync;
using AlleyCat.TestFramework;
using Godot;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;

namespace AlleyCat.IntegrationTests.Speech;

/// <summary>
/// Runtime coverage for the shared Wav2Arkit session (SPCH-001 TR9, AC5, AC9; CORE-011-TAC-002/007): players share
/// one loaded session, readiness never blocks on the load, initialisation settles asynchronously, and a load failure,
/// including a cached one, surfaces through <see cref="LipSyncPlayer.InitialisationError" />.
/// </summary>
/// <remarks>
/// The session factory is substituted so no ONNX model is needed; the shipped config supplies the per-player
/// blendshape names, sample rate and tensor names.
/// </remarks>
public sealed partial class Wav2ArkitLipSyncPlayerIntegrationTests
{
    private const string SampleSpeechPath = "res://assets/audio/samples/sample-voice.wav";
    private const string ModelPath = "res://models/wav2arkit_cpu/integration-test-fake.onnx";
    private const int ConfigBlendshapeCount = 52;
    // Failure bound only: every wait ends as soon as its condition holds.
    private static readonly TimeSpan _bound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Three players with the same model path load one shared session; their readiness does not wait for the load,
    /// they stay uninitialised until it completes, and each then infers through the shared session with its own
    /// config.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Players_WithSharedRegistry_ShareOneSession_WithoutBlockingReady()
    {
        SceneTree sceneTree = GetSceneTree();
        var registry = new SharedModelRegistry();
        var loadGate = new ManualResetEventSlim();
        var session = new FakeSession();
        int loadAttempts = 0;
        Node3D root = CreateFixture(registry, 3, path =>
        {
            _ = Interlocked.Increment(ref loadAttempts);
            Assert.True(loadGate.Wait(_bound), "The test never released the shared load.");
            Assert.EndsWith("integration-test-fake.onnx", path, StringComparison.Ordinal);
            return session;
        }, out Wav2ArkitLipSyncPlayer[] players);

        try
        {
            // _Ready runs inside AddChild; returning here while the load is held proves readiness did not block.
            sceneTree.Root.AddChild(root);
            await WaitForFramesAsync(sceneTree, 3);

            Assert.All(players, player =>
            {
                Assert.False(player.IsInitialised);
                Assert.Equal(string.Empty, player.InitialisationError);
            });

            AudioStreamWav speech = LoadSpeech();
            players[0].Play(speech);
            Assert.NotEqual(string.Empty, players[0].PlaybackError);
            Assert.False(players[0].IsInitialised);

            loadGate.Set();
            await WaitUntilAsync(sceneTree, () => players.All(p => p.IsInitialised));

            Assert.All(players, player => Assert.Equal(string.Empty, player.InitialisationError));
            Assert.Equal(1, loadAttempts);

            LipSyncPlayer.PreparedPlayback[] prepared = await Task.WhenAll(
                players.Select(p => p.PreparePlaybackAsync(speech))).WaitAsync(_bound);
            Assert.All(prepared, playback =>
            {
                Assert.Equal(ConfigBlendshapeCount, playback.BlendshapeNames.Count);
                Assert.Equal(FakeSession.FrameCount, playback.Frames.Length);
            });
            Assert.Equal(3, session.InferenceCount);
            Assert.All(session.InputNames, name => Assert.Equal("audio_waveform", name));
        }
        finally
        {
            loadGate.Set();
            root.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
        }

        Assert.False(session.IsDisposed);
        await registry.Stop().WaitAsync(_bound);
        Assert.True(session.IsDisposed);
    }

    /// <summary>
    /// A failed shared load sets <see cref="LipSyncPlayer.InitialisationError" /> after readiness and leaves the player
    /// uninitialised; a player added later receives the cached failure without a second load attempt.
    /// </summary>
    [Fact]
    [Headless]
    public async Task Players_WhenSharedLoadFails_ReportInitialisationError_AndCachedFailureReachesLaterPlayer()
    {
        SceneTree sceneTree = GetSceneTree();
        var registry = new SharedModelRegistry();
        var loadGate = new ManualResetEventSlim();
        int loadAttempts = 0;
        IWav2ArkitInferenceSession Factory(string path)
        {
            _ = Interlocked.Increment(ref loadAttempts);
            Assert.True(loadGate.Wait(_bound), "The test never released the shared load.");
            throw new InvalidOperationException("Injected shared Wav2Arkit load failure.");
        }

        Node3D first = CreateFixture(registry, 1, Factory, out Wav2ArkitLipSyncPlayer[] firstPlayers);
        Node3D later = CreateFixture(registry, 1, Factory, out Wav2ArkitLipSyncPlayer[] laterPlayers);

        try
        {
            sceneTree.Root.AddChild(first);
            await WaitForFramesAsync(sceneTree, 3);
            Assert.False(firstPlayers[0].IsInitialised);
            Assert.Equal(string.Empty, firstPlayers[0].InitialisationError);

            loadGate.Set();
            await WaitUntilAsync(sceneTree, () => firstPlayers[0].InitialisationError.Length > 0);
            Assert.False(firstPlayers[0].IsInitialised);
            Assert.Contains("Injected shared Wav2Arkit load failure.", firstPlayers[0].InitialisationError, StringComparison.Ordinal);

            sceneTree.Root.AddChild(later);
            await WaitUntilAsync(sceneTree, () => laterPlayers[0].InitialisationError.Length > 0);
            Assert.False(laterPlayers[0].IsInitialised);
            Assert.Contains("Injected shared Wav2Arkit load failure.", laterPlayers[0].InitialisationError, StringComparison.Ordinal);
            Assert.Equal(1, loadAttempts);
        }
        finally
        {
            loadGate.Set();
            first.QueueFree();
            later.QueueFree();
            await WaitForFramesAsync(sceneTree, 2);
            await registry.Stop().WaitAsync(_bound);
        }
    }

    private static Node3D CreateFixture(
        ISharedModelRegistry registry,
        int playerCount,
        Func<string, IWav2ArkitInferenceSession> factory,
        out Wav2ArkitLipSyncPlayer[] players)
    {
        Node3D root = new()
        {
            Name = "Wav2ArkitSharedSessionRoot"
        };
        players = new Wav2ArkitLipSyncPlayer[playerCount];
        for (int i = 0; i < playerCount; i++)
        {
            AudioStreamPlayer3D audioPlayer = new()
            {
                Name = $"Audio{i}"
            };
            Skeleton3D skeleton = new()
            {
                Name = $"Skeleton{i}"
            };
            Wav2ArkitLipSyncPlayer player = new()
            {
                Name = $"LipSyncPlayer{i}",
                AudioPlayer = audioPlayer,
                Skeleton = skeleton,
                ModelPath = ModelPath,
            };
            player.OverrideSharedModelsForTesting(registry, factory);
            root.AddChild(audioPlayer);
            root.AddChild(skeleton);
            root.AddChild(player);
            players[i] = player;
        }

        return root;
    }

    private static AudioStreamWav LoadSpeech()
        => GD.Load<AudioStreamWav>(SampleSpeechPath)
            ?? throw new InvalidOperationException($"Failed to load sample speech clip at '{SampleSpeechPath}'.");

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && clock.Elapsed < _bound)
        {
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {_bound.TotalSeconds} s.");
    }

    private sealed class FakeSession : IWav2ArkitInferenceSession
    {
        public const int FrameCount = 4;

        private int _inferenceCount;

        public int InferenceCount => Volatile.Read(ref _inferenceCount);

        public System.Collections.Concurrent.ConcurrentQueue<string> InputNames { get; } = new();

        public bool IsDisposed
        {
            get; private set;
        }

        public float[][] Infer(float[] monoWaveform, string inputName, string outputName)
        {
            Assert.NotEmpty(monoWaveform);
            InputNames.Enqueue(inputName);
            _ = Interlocked.Increment(ref _inferenceCount);
            return [.. Enumerable.Range(0, FrameCount).Select(i => Enumerable.Repeat(i * 0.25f, ConfigBlendshapeCount).ToArray())];
        }

        public void Dispose() => IsDisposed = true;
    }
}
