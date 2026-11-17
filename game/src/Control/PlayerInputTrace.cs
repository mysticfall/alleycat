using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using AlleyCat.Motion;
using Microsoft.Extensions.Logging;

namespace AlleyCat.Control;

// Bounded opt-in event recorder. No file writes or JSON serialisation on the scene thread.
internal sealed class PlayerInputTrace
{
    private readonly Channel<object> _events = Channel.CreateBounded<object>(8192);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _stopped, _dropped;
    private long _sequence;
    private PlayerPoseRecording? _poses;
    private string? _error;
    internal string? Error => Volatile.Read(ref _error);
    internal void AttachPoses(PlayerPoseRecording? poses)
    {
        Volatile.Write(ref _poses, poses);
        if (Error is { } error)
        {
            poses?.Writer.Fault(error);
        }
    }
    public Task Completion
    {
        get;
    }

    public PlayerInputTrace(string path, ILogger logger)
    {
        Completion = Task.Run(async () =>
        {
            try
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                    65536, FileOptions.Asynchronous);
                await using var writer = new StreamWriter(stream);
                var options = new JsonSerializerOptions { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
                await foreach (object entry in _events.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(entry, options)).ConfigureAwait(false);
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    kind = "trace-complete",
                    dropped = _dropped
                })).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
                logger.LogInformation("Player input trace complete: {Path}; dropped events {Dropped}", path, _dropped);
            }
            catch (Exception error)
            {
                _ = Interlocked.Exchange(ref _stopped, 1);
                _ = Interlocked.CompareExchange(ref _error, "legacy-write-" + error.GetType().Name, null);
                Volatile.Read(ref _poses)?.Writer.Fault(Error!);
                _ = _events.Writer.TryComplete();
                try
                {
                    logger.LogError(error, "Player input trace failed: {Path}", path);
                }
                catch { /* Diagnostic logging failure must not become a provider disposal failure. */ }
            }
        });
    }

    public void Record(string kind, object data)
    {
        if (Volatile.Read(ref _stopped) != 0)
        {
            return;
        }
        Volatile.Read(ref _poses)?.Record("input-" + kind, data);
        if (!_events.Writer.TryWrite(new
        {
            sequence = ++_sequence,
            seconds = _clock.Elapsed.TotalSeconds,
            kind,
            data
        }))
        {
            _ = Interlocked.Increment(ref _dropped);
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 0)
        {
            _ = _events.Writer.TryComplete();
        }
    }

    // Native disposal already runs off the scene thread and is drained by Game.MotionWorkers.
    // Finish the associated recorder there too; do not add a blocking exit wait on the VR thread.
    public SomaInferenceWorker.ISession Wrap(SomaInferenceWorker.ISession inner) => new DrainSession(inner, this);
    private sealed class DrainSession(SomaInferenceWorker.ISession inner, PlayerInputTrace trace) : SomaInferenceWorker.ISession
    {
        public Godot.Transform3D[][] Generate(Godot.Transform3D[][] context, Godot.Transform3D[][] target) => inner.Generate(context, target);
        public void Dispose()
        {
            try
            {
                inner.Dispose();
            }
            finally
            {
                trace.Stop();
                trace.Completion.GetAwaiter().GetResult();
            }
        }
    }
}
