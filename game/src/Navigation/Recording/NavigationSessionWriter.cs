using System.IO.Compression;
using System.Text.Json;
using System.Threading.Channels;

namespace AlleyCat.Navigation.Recording;

/// <summary>Bounded value-only session transport. No scene objects or disk work enter producer callbacks.</summary>
internal sealed class NavigationSessionWriter
{
    private sealed record Work(long Sequence, long Clock, string Kind, byte[] Metadata, float[]? Values,
        int Bytes, TaskCompletionSource<bool>? Barrier = null, bool Final = false);
    private readonly Channel<Work> _queue;
    private readonly Lock _gate = new();
    private readonly long _byteLimit;
    private long _queuedBytes, _sequence, _dropped, _savedThrough;
    private int _accepting = 1, _saving;
    private string? _error;
    private Task<bool>? _finish;
    private Task<bool>? _checkpoint;
    private readonly object _identity;
    private readonly List<object> _segments = [];
    internal string DirectoryPath
    {
        get;
    }
    internal long Dropped => Interlocked.Read(ref _dropped);
    internal long SavedThrough => Interlocked.Read(ref _savedThrough);
    internal bool Saving => Volatile.Read(ref _saving) != 0;
    internal string? Error => Volatile.Read(ref _error);
    internal Task Completion
    {
        get;
    }
    internal static JsonSerializerOptions JsonOptions
    {
        get;
    } = new()
    {
        IncludeFields = true,
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    internal NavigationSessionWriter(string path, object identity, long byteLimit = 32 * 1024 * 1024, int capacity = 4096)
    {
        DirectoryPath = path;
        _identity = identity;
        _byteLimit = byteLimit;
        _queue = Channel.CreateBounded<Work>(capacity);
        Completion = Task.Run(RunAsync);
    }

    internal void Fault(string code) => Interlocked.CompareExchange(ref _error, code, null);

    internal bool Record(string kind, object metadata, int floatCount = 0, Func<float[]>? copy = null)
    {
        if (Volatile.Read(ref _accepting) == 0)
        {
            return false;
        }

        try
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions);
            long size = json.Length + ((long)floatCount * sizeof(float)) + 128;
            lock (_gate)
            {
                if (_accepting == 0)
                {
                    return false;
                }

                long sequence = ++_sequence;
                if (size > _byteLimit || _queuedBytes + size > _byteLimit || size > int.MaxValue)
                {
                    _ = Interlocked.Increment(ref _dropped);
                    return false;
                }
                // Reserve before copying potentially large provider arrays; queue retains only immutable values.
                _queuedBytes += size;
                float[]? values;
                try
                {
                    values = copy?.Invoke();
                }
                catch { _queuedBytes -= size; throw; }
                if ((values?.Length ?? 0) != floatCount)
                {
                    _queuedBytes -= size;
                    throw new InvalidDataException("Recording payload size mismatch.");
                }
                if (_queue.Writer.TryWrite(new(sequence, System.Diagnostics.Stopwatch.GetTimestamp(), kind, json, values, (int)size)))
                {
                    return true;
                }

                _queuedBytes -= size;
                _ = Interlocked.Increment(ref _dropped);
                return false;
            }
        }
        catch (Exception error) { Fault("observer-" + error.GetType().Name); _ = Interlocked.Increment(ref _dropped); return false; }
    }

    internal Task<bool> CheckpointAsync()
    {
        lock (_gate)
        {
            return _finish is not null ? _finish : _checkpoint is { IsCompleted: false } ? _checkpoint : _checkpoint = BarrierAsync(false);
        }
    }
    internal Task<bool> FinishAsync()
    {
        lock (_gate)
        {
            _accepting = 0;
            return _finish ??= BarrierAsync(true);
        }
    }
    private async Task<bool> BarrierAsync(bool final)
    {
        _ = Interlocked.Increment(ref _saving);
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue.Writer.WriteAsync(new(0, 0, "", [], null, 0, done, final)).ConfigureAwait(false);
            return await done.Task.ConfigureAwait(false);
        }
        catch (ChannelClosedException) { return false; }
        finally { _ = Interlocked.Decrement(ref _saving); }
    }

    private async Task RunAsync()
    {
        StreamWriter? events = null;
        FileStream? eventFile = null, motionFile = null;
        GZipStream? gzip = null;
        BinaryWriter? binary = null;
        Work? current = null;
        int part = 0;
        long last = 0, sealedThrough = 0, records = 0;
        string eventName = "", motionName = "";
        void Open()
        {
            part++;
            records = 0;
            eventName = $"events-{part:0000}.jsonl";
            motionName = $"motion-{part:0000}.bin.gz";
            eventFile = new(Path.Combine(DirectoryPath, eventName + ".partial"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            events = new(eventFile, leaveOpen: true);
            motionFile = new(Path.Combine(DirectoryPath, motionName + ".partial"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            gzip = new(motionFile, CompressionLevel.Fastest, leaveOpen: true);
            binary = new(gzip, System.Text.Encoding.UTF8, leaveOpen: true);
            binary.Write("ACNAV001"u8);
            binary.Write(1);
        }
        void Manifest(string state)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "alleycat-navigation-session-v1",
                state,
                integrity = Error is not null ? "failed" : Dropped != 0 ? "lossy" : "complete-through-checkpoint",
                savedThroughSequence = sealedThrough,
                attemptedThroughSequence = Interlocked.Read(ref _sequence),
                droppedRecords = Dropped,
                error = Error,
                segments = _segments,
                identity = _identity,
                binary = "ACNAV001, int32 version; repeated int64 sequence, int32 floatCount, float32 values; little-endian; JSONL layout describes each bulk record",
                exactness = "Recorded native outputs and physics/skin/camera states; not fresh-inference/live-physics bit identity or every rendered pixel/frame. Ignore unsealed .partial files."
            }, JsonOptions);
            string temporary = Path.Combine(DirectoryPath, "manifest.json.tmp");
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(true);
            }
            File.Move(temporary, Path.Combine(DirectoryPath, "manifest.json"), overwrite: true);
        }
        void Seal(bool final)
        {
            binary!.Flush();
            binary.Dispose();
            binary = null;
            gzip!.Dispose();
            gzip = null;
            motionFile!.Flush(true);
            motionFile.Dispose();
            motionFile = null;
            events!.Flush();
            events.Dispose();
            events = null;
            eventFile!.Flush(true);
            eventFile.Dispose();
            eventFile = null;
            File.Move(Path.Combine(DirectoryPath, eventName + ".partial"), Path.Combine(DirectoryPath, eventName));
            File.Move(Path.Combine(DirectoryPath, motionName + ".partial"), Path.Combine(DirectoryPath, motionName));
            _segments.Add(new
            {
                events = eventName,
                motion = motionName,
                records,
                throughSequence = last
            });
            sealedThrough = last;
            Manifest(final ? "completed" : "recording");
            _ = Interlocked.Exchange(ref _savedThrough, last);
        }
        try
        {
            _ = Directory.CreateDirectory(DirectoryPath);
            Manifest("recording");
            Open();
            await foreach (Work item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                current = item;
                if (item.Barrier is { } barrier)
                {
                    Seal(item.Final);
                    _ = barrier.TrySetResult(true);
                    if (item.Final)
                    {
                        _ = _queue.Writer.TryComplete();
                        break;
                    }
                    Open();
                    continue;
                }
                if (item.Values is { Length: > 0 } values)
                {
                    binary!.Write(item.Sequence);
                    binary.Write(values.Length);
                    foreach (float value in values)
                    {
                        binary.Write(value);
                    }
                }
                using var metadata = JsonDocument.Parse(item.Metadata);
                events!.WriteLine(JsonSerializer.Serialize(new
                {
                    sequence = item.Sequence,
                    clock = item.Clock,
                    kind = item.Kind,
                    data = metadata.RootElement,
                    bulkFloats = item.Values?.Length ?? 0
                }, JsonOptions));
                last = item.Sequence;
                records++;
                lock (_gate)
                {
                    _queuedBytes -= item.Bytes;
                }
            }
        }
        catch (Exception error)
        {
            Fault("write-" + error.GetType().Name);
            Volatile.Write(ref _accepting, 0);
            _ = _queue.Writer.TryComplete();
            _ = (current?.Barrier?.TrySetResult(false));
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Manifest("failed");
                }
            }
            catch { /* UI Error remains explicit when the device cannot persist its own failure. */ }
        }
        finally
        {
            while (_queue.Reader.TryRead(out Work? item))
            {
                _ = (item.Barrier?.TrySetResult(false));
                if (item.Bytes != 0)
                {
                    _ = Interlocked.Increment(ref _dropped);
                }
            }
            try
            {
                binary?.Dispose();
                gzip?.Dispose();
                events?.Dispose();
                eventFile?.Dispose();
                motionFile?.Dispose();
            }
            catch { }
        }
    }
}
