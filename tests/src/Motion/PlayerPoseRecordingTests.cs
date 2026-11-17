using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AlleyCat.Control;
using AlleyCat.Motion;
using AlleyCat.Navigation.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AlleyCat.Tests.Motion;

/// <summary>Recorder observer failures remain diagnostic transport failures, never provider exceptions.</summary>
public sealed class PlayerPoseRecordingTests
{
    /// <inheritdoc/>
    [Fact]
    public async Task LegacyWriteFailureCannotBecomeProviderDisposalFailure()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../game"));
        string file = Path.Combine(root, "temp/motionbricks/ctrl-turn-footwork-recording-01/unit", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "not a directory");
        var trace = new PlayerInputTrace(Path.Combine(file, "trace.jsonl"), NullLogger.Instance);
        try
        {
            await trace.Completion;
        }
        catch { /* The assertion concerns isolation at the provider-disposal boundary, not hiding IO evidence. */ }
        var provider = new NoGenerationSession();
        Exception? error = Record.Exception(() => trace.Wrap(provider).Dispose());
        Assert.True(provider.Disposed);
        Assert.Null(error);
    }

    /// <inheritdoc/>
    [Fact]
    public async Task WorkerDrainRetainsLateReturnAndBoundedTransportMarksDrops()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../game"));
        string path = Path.Combine(root, "temp/motionbricks/ctrl-turn-footwork-recording-01/unit", Guid.NewGuid().ToString("N"));
        var recording = new PlayerPoseRecording(path, new
        {
            synthetic = true
        }, root, "");
        var worker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task finish = recording.FinishAfterAsync(worker.Task);
        Assert.False(finish.IsCompleted);
        var link = new StrongBox<PlayerPoseRecording?>(recording);
        Action<SomaInferenceWorker.Result> observer = PlayerPoseRecording.ObserveReturns(link);
        Assert.Same(link, Assert.Single(observer.Target!.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).GetValue(observer.Target));
        observer(new SomaInferenceWorker.Result(7, [], null));
        worker.SetResult(true);
        await finish;
        Assert.Contains(NavigationSessionReader.ReadCheckpoint(path), e => e.Event.GetProperty("kind").GetString() == "provider-returned");
        var bounded = new NavigationSessionWriter(path + "-bounded", new
        {
            synthetic = true
        }, byteLimit: 256, capacity: 1);
        bool copied = false;
        Assert.False(bounded.Record("oversize", new
        {
        }, 1000, () => { copied = true; return new float[1000]; }));
        Assert.False(copied);
        Assert.True(await bounded.FinishAsync());
        Assert.Equal(1, bounded.Dropped);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path + "-bounded", "manifest.json")));
        Assert.Equal("lossy", manifest.RootElement.GetProperty("integrity").GetString());
    }

    /// <inheritdoc/>
    [Fact]
    public async Task MalformedObserverPayloadIsIsolatedAndTransportClosesTruthfully()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../game"));
        string path = Path.Combine(root, "temp/motionbricks/ctrl-turn-footwork-recording-01/unit", Guid.NewGuid().ToString("N"));
        var recording = new PlayerPoseRecording(path, new
        {
            synthetic = true
        }, root, "");
        recording.Motion("bad-observer", new
        {
        }, null!);
        recording.Record("provider-unaffected", new
        {
        });
        await recording.FinishAfterAsync(null);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")));
        Assert.Equal("completed", manifest.RootElement.GetProperty("state").GetString());
        Assert.Equal("failed", manifest.RootElement.GetProperty("integrity").GetString());
        Assert.NotEqual(JsonValueKind.Null, manifest.RootElement.GetProperty("error").ValueKind);
        Assert.True(recording.Completion.IsCompletedSuccessfully);
    }

    private sealed class NoGenerationSession : SomaInferenceWorker.ISession
    {
        internal bool Disposed;
        public Godot.Transform3D[][] Generate(Godot.Transform3D[][] context, Godot.Transform3D[][] target) =>
            throw new InvalidOperationException("No generation is authorised by this IO isolation fixture.");
        public void Dispose() => Disposed = true;
    }
}
