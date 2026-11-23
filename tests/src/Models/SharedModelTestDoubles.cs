using AlleyCat.Models;

namespace AlleyCat.Tests.Models;

internal static class SharedModelTestTimeouts
{
    // Failure bound only: every wait completes as soon as its signal fires.
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
}

internal sealed record TestModelKey(string Path, string Provider = "cpu") : ModelKey;

internal sealed class TestBackend : IDisposable
{
    private int _disposeCount;

    public int DisposeCount => Volatile.Read(ref _disposeCount);

    public Func<bool>? ObserveOnDispose
    {
        get; set;
    }

    public bool? ObservedOnDispose
    {
        get; private set;
    }

    public void Dispose()
    {
        ObservedOnDispose = ObserveOnDispose?.Invoke();
        _ = Interlocked.Increment(ref _disposeCount);
    }
}

internal sealed class TestDefinition(
    ModelKey key,
    Func<CancellationToken, Task<TestBackend>> load,
    int maxConcurrency = 1,
    Func<IModelSchedulingPolicy>? policy = null) : ModelDefinition<TestBackend>
{
    private int _loadCount;

    public int LoadCount => Volatile.Read(ref _loadCount);

    public override ModelKey Key => key;

    public override int MaxConcurrency => maxConcurrency;

    public static TestDefinition Immediate(ModelKey key, TestBackend backend, int maxConcurrency = 1) =>
        new(key, _ => Task.FromResult(backend), maxConcurrency);

    public override Task<TestBackend> LoadAsync(CancellationToken cancellationToken)
    {
        _ = Interlocked.Increment(ref _loadCount);
        return load(cancellationToken);
    }

    public override IModelSchedulingPolicy CreateSchedulingPolicy() =>
        policy?.Invoke() ?? base.CreateSchedulingPolicy();
}

/// <summary>A backend call whose start is observable and whose return the test controls.</summary>
internal sealed class GatedCall(int result = 0)
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _finished;

    public Task Started => _started.Task;

    public bool HasStarted => _started.Task.IsCompleted;

    public bool Finished => Volatile.Read(ref _finished) != 0;

    public Exception? Failure
    {
        get; init;
    }

    public async Task<int> Execute(TestBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _ = _started.TrySetResult();
        await _release.Task.ConfigureAwait(false);
        _ = Interlocked.Exchange(ref _finished, 1);
        return Failure is null ? result : throw Failure;
    }

    public void Release() => _release.TrySetResult();
}

internal sealed class RecordingObserver : ISharedModelObserver
{
    private readonly Lock _gate = new();
    private readonly List<SharedModelEvent> _events = [];

    public IReadOnlyList<SharedModelEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public int Count(SharedModelEventKind kind) => Events.Count(e => e.Kind == kind);

    public void OnEvent(SharedModelEvent modelEvent)
    {
        lock (_gate)
        {
            _events.Add(modelEvent);
        }
    }
}

/// <summary>Minimal model-specific handle used to exercise the reusable handle base.</summary>
internal sealed class TestHandle(ISharedModel<TestBackend> model, string name)
    : SharedModelHandle<TestBackend>(model, name)
{
    public Task<int> Run(Func<TestBackend, Task<int>> execute, object? key = null, CancellationToken token = default) =>
        SubmitAsync(execute, key, token);

    public Task<T> RunSync<T>(Func<TestBackend, T> execute, object? key = null) => Submit(execute, key);
}
