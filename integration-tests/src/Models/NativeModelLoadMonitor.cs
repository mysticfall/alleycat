using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace AlleyCat.IntegrationTests.Models;

/// <summary>
/// Process-wide observer of native ONNX Runtime model loads and CUDA execution-provider creation.
/// </summary>
/// <remarks>
/// Both counters sit below every managed caller, so routing consumers through a shared registry cannot bypass them:
/// <list type="bullet">
/// <item>Kernel inotify <c>IN_OPEN</c>/<c>IN_CLOSE_NOWRITE</c> watches on every model file attribute each native load
/// to its model file; ONNX Runtime opens the model file once per session constructed from a path. A dedicated reader
/// thread drains the queue continuously because the kernel merges an event identical to the unread queue tail.</item>
/// <item>The ONNX Runtime environment logger counts CUDA arena creations, which happen at environment log level for
/// every CUDA execution-provider instance regardless of a session's own log severity.</item>
/// </list>
/// The ONNX Runtime environment can be configured only before its first use in a process, so the monitor requires a
/// fresh Godot process and must be installed before any model consumer starts.
/// </remarks>
internal sealed class NativeModelLoadMonitor : IDisposable
{
    private const string SessionInitialised = "Session successfully initialized.";
    private const string CudaArenaCreated = "Creating BFCArena for Cuda ";
    private const int InotifyNonBlocking = 0x800;
    private const int InotifyCloseOnExec = 0x80000;
    private const uint InotifyOpen = 0x20;
    private const uint InotifyCloseNoWrite = 0x10;
    private const short PollIn = 0x1;

    private static readonly Lock _sinkLock = new();
    private static NativeModelLoadMonitor? _sink;
    // ONNX Runtime keeps the native callback for the process lifetime; the managed delegate must stay rooted.
    private static DOrtLoggingFunction? _logger;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<int, string> _watches = [];
    private readonly Dictionary<string, int> _opens = [];
    private readonly Dictionary<string, int> _closes = [];
    private readonly int _inotify;
    private readonly Thread _reader;
    private volatile bool _stopping;
    private int _loggedSessions;
    private int _cudaArenas;
    private bool _disposed;

    private NativeModelLoadMonitor(IReadOnlyDictionary<string, string> modelFiles)
    {
        _inotify = inotify_init1(InotifyNonBlocking | InotifyCloseOnExec);
        if (_inotify < 0)
        {
            throw new InvalidOperationException($"inotify_init1 failed with errno {Marshal.GetLastPInvokeError()}.");
        }

        foreach ((string key, string path) in modelFiles)
        {
            int watch = inotify_add_watch(_inotify, path, InotifyOpen | InotifyCloseNoWrite);
            if (watch < 0)
            {
                throw new InvalidOperationException(
                    $"inotify_add_watch failed for '{path}' with errno {Marshal.GetLastPInvokeError()}.");
            }

            // Several paths to one inode share a watch; keep the first key.
            _ = _watches.TryAdd(watch, key);
            _opens[key] = 0;
            _closes[key] = 0;
        }

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Model load inotify reader" };
        _reader.Start();
    }

    /// <summary>
    /// Sessions that logged their own successful initialisation; sessions created with a log severity above
    /// information are not included, so this is diagnostic only.
    /// </summary>
    public int LoggedSessions
    {
        get
        {
            lock (_stateLock)
            {
                return _loggedSessions;
            }
        }
    }

    /// <summary>CUDA arenas created, one per CUDA execution-provider instance.</summary>
    public int CudaArenas
    {
        get
        {
            lock (_stateLock)
            {
                return _cudaArenas;
            }
        }
    }

    /// <summary>
    /// Installs the process-wide observer. Fails when ONNX Runtime was already used in this process.
    /// </summary>
    /// <param name="modelFiles">Model key to absolute model file path.</param>
    public static NativeModelLoadMonitor Install(IReadOnlyDictionary<string, string> modelFiles)
    {
        if (OrtEnv.IsCreated)
        {
            throw new InvalidOperationException(
                "ONNX Runtime was already initialised in this Godot process; run this fact alone in a fresh session.");
        }

        var monitor = new NativeModelLoadMonitor(modelFiles);
        lock (_sinkLock)
        {
            _sink = monitor;
        }

        _logger = static (_, _, _, _, _, message) =>
        {
            NativeModelLoadMonitor? sink;
            lock (_sinkLock)
            {
                sink = _sink;
            }

            sink?.Observe(message);
        };
        var options = new EnvironmentCreationOptions
        {
            logId = "alleycat-model-load-monitor",
            logLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_INFO,
            loggingFunction = _logger,
        };
        _ = OrtEnv.CreateInstanceWithOptions(ref options);
        return monitor;
    }

    /// <summary>Returns the model-file open counts observed so far, by model key.</summary>
    public IReadOnlyDictionary<string, int> SnapshotOpens()
    {
        lock (_stateLock)
        {
            return new SortedDictionary<string, int>(_opens, StringComparer.Ordinal);
        }
    }

    /// <summary>Returns the model-file read-only close counts observed so far, by model key.</summary>
    public IReadOnlyDictionary<string, int> SnapshotCloses()
    {
        lock (_stateLock)
        {
            return new SortedDictionary<string, int>(_closes, StringComparer.Ordinal);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sinkLock)
        {
            if (ReferenceEquals(_sink, this))
            {
                _sink = null;
            }
        }

        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;
        _reader.Join();
        _ = close(_inotify);

        if (OrtEnv.IsCreated)
        {
            OrtEnv.Instance().EnvLogLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
        }
    }

    private void Observe(string message)
    {
        if (message.StartsWith(CudaArenaCreated, StringComparison.Ordinal))
        {
            lock (_stateLock)
            {
                _cudaArenas++;
            }
        }
        else if (message.StartsWith(SessionInitialised, StringComparison.Ordinal))
        {
            lock (_stateLock)
            {
                _loggedSessions++;
            }
        }
    }

    private void ReadLoop()
    {
        byte[] buffer = new byte[4096];
        var poll = new PollDescriptor[] { new() { Descriptor = _inotify, Events = PollIn } };
        while (true)
        {
            // Check the flag before the final drain so events queued before Dispose are still counted.
            bool stopping = _stopping;
            _ = Poll(poll, 1, stopping ? 0 : 50);
            while (true)
            {
                nint read = ReadEvents(_inotify, buffer, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                Record(buffer, (int)read);
            }

            if (stopping)
            {
                return;
            }
        }
    }

    private void Record(byte[] buffer, int length)
    {
        lock (_stateLock)
        {
            int offset = 0;
            while (offset < length)
            {
                int watch = BitConverter.ToInt32(buffer, offset);
                uint mask = BitConverter.ToUInt32(buffer, offset + 4);
                uint nameLength = BitConverter.ToUInt32(buffer, offset + 12);
                if (_watches.TryGetValue(watch, out string? key))
                {
                    if ((mask & InotifyOpen) != 0)
                    {
                        _opens[key]++;
                    }

                    if ((mask & InotifyCloseNoWrite) != 0)
                    {
                        _closes[key]++;
                    }
                }

                offset += 16 + (int)nameLength;
            }
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_init1(int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int inotify_add_watch(
        int descriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mask);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadEvents(int descriptor, [Out] byte[] buffer, nint count);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int Poll([In, Out] PollDescriptor[] descriptors, nuint count, int timeout);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollDescriptor
    {
        public int Descriptor;
        public short Events;
        public short ReturnedEvents;
    }
}
