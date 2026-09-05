using System.Text.Json;
using System.Threading.Channels;

namespace AlleyCat.TestFramework.Tests;

internal enum FakeOutputStream
{
    StdOut,

    StdErr,
}

internal sealed record FakeOutputEvent(TimeSpan Delay, FakeOutputStream Stream, string Line);

/// <summary>
/// One staged reaction to a single stdin command: timed output lines followed by an optional process exit. A stage
/// without output events or exit models a hanging command.
/// </summary>
internal sealed record FakeSessionStage(
    IReadOnlyList<FakeOutputEvent> OutputEvents,
    int? ExitCode = null,
    TimeSpan ExitDelay = default,
    bool EmitMatchingShutdownComplete = false);

/// <summary>
/// Fake Godot process modelling a persistent session: startup output timeline, staged responses consumed one per
/// stdin command, captured stdin writes, and explicit close, kill, and exit behaviour.
/// </summary>
internal sealed class FakeGodotProcess : IGodotProcess
{
    private const string SessionLinePrefix = "ALLEYCAT_INTEGRATION_SESSION:";

    private readonly Channel<string> _stdOutChannel = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stdErrChannel = Channel.CreateUnbounded<string>();
    private readonly IReadOnlyList<FakeOutputEvent> _startupEvents;
    private readonly TimeSpan _naturalExitDelay;
    private readonly int _naturalExitCode;
    private readonly Queue<FakeSessionStage> _stages;
    private readonly Exception? _shutdownWriteException;
    private readonly TaskCompletionSource _exitTaskSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();
    private readonly Lock _sync = new();
    private readonly List<string> _writtenLines = [];

    private FakeGodotProcess(
        IReadOnlyList<FakeOutputEvent> startupEvents,
        TimeSpan naturalExitDelay,
        int naturalExitCode,
        IEnumerable<FakeSessionStage>? stages,
        Exception? shutdownWriteException = null)
    {
        _startupEvents = startupEvents;
        _naturalExitDelay = naturalExitDelay;
        _naturalExitCode = naturalExitCode;
        _stages = new Queue<FakeSessionStage>(stages ?? []);
        _shutdownWriteException = shutdownWriteException;
    }

    public bool KillCalled
    {
        get;
        private set;
    }

    public bool KillTreeCalled
    {
        get;
        private set;
    }

    public bool StandardInputClosed
    {
        get;
        private set;
    }

    public bool HasExited => _exitTaskSource.Task.IsCompleted;

    public int ExitCode
    {
        get;
        private set;
    }

    public IReadOnlyList<string> WrittenLines
    {
        get
        {
            lock (_sync)
            {
                return [.. _writtenLines];
            }
        }
    }

    /// <summary>
    /// Creates a process that emits the supplied startup output and then exits naturally, as used by preflight
    /// probes and crashed sessions.
    /// </summary>
    public static FakeGodotProcess Create(
        IReadOnlyList<FakeOutputEvent> outputEvents,
        TimeSpan naturalExitDelay,
        int naturalExitCode = 0)
        => new(outputEvents, naturalExitDelay, naturalExitCode, stages: null);

    /// <summary>
    /// Creates a persistent session process: it emits its ready line plus any additional startup output, stays
    /// alive, and reacts to each stdin command by consuming the next staged response. A final graceful shutdown
    /// stage is appended automatically.
    /// </summary>
    public static FakeGodotProcess CreateSession(
        IReadOnlyList<FakeOutputEvent>? startupEvents = null,
        IEnumerable<FakeSessionStage>? commandStages = null,
        int naturalExitCode = 0,
        Exception? shutdownWriteException = null)
        => new(
            startupEvents ?? [SessionReadyEvent()],
            Timeout.InfiniteTimeSpan,
            naturalExitCode,
            (commandStages ?? []).Concat([CreateGracefulShutdownStage()]),
            shutdownWriteException);

    /// <summary>
    /// Creates the protocol line a healthy session emits once its assembly is loaded and its baseline captured.
    /// </summary>
    public static FakeOutputEvent SessionReadyEvent(TimeSpan? delay = null)
        => new(delay ?? TimeSpan.FromMilliseconds(5), FakeOutputStream.StdOut, SessionReadyLine());

    private static string SessionReadyLine()
        => SessionLinePrefix + JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "ready",
        });

    public bool Start()
    {
        _ = RunStartupAsync();
        return true;
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken)
        => await _exitTaskSource.Task.WaitAsync(cancellationToken);

    public async Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
    {
        return await _stdOutChannel.Reader.WaitToReadAsync(cancellationToken)
            ? await _stdOutChannel.Reader.ReadAsync(cancellationToken)
            : null;
    }

    public async Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken)
    {
        return await _stdErrChannel.Reader.WaitToReadAsync(cancellationToken)
            ? await _stdErrChannel.Reader.ReadAsync(cancellationToken)
            : null;
    }

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        string? shutdownRequestId = GetShutdownRequestId(line);
        if (shutdownRequestId is not null && _shutdownWriteException is not null)
        {
            return Task.FromException(_shutdownWriteException);
        }

        lock (_sync)
        {
            _writtenLines.Add(line);
        }

        FakeSessionStage? stage = _stages.Count > 0 ? _stages.Dequeue() : null;
        if (stage is not null)
        {
            _ = RunStageAsync(stage, shutdownRequestId);
        }

        return Task.CompletedTask;
    }

    public void CloseStandardInput() => StandardInputClosed = true;

    public void Kill(bool entireProcessTree)
    {
        if (HasExited)
        {
            return;
        }

        KillCalled = true;
        KillTreeCalled = entireProcessTree;
        _lifetimeCancellationTokenSource.Cancel();
        Complete(exitCode: -1);
    }

    public void Dispose()
    {
        _lifetimeCancellationTokenSource.Cancel();
        Complete(exitCode: ExitCode);
        _lifetimeCancellationTokenSource.Dispose();
    }

    private static FakeSessionStage CreateGracefulShutdownStage() => new(
        OutputEvents: [],
        ExitCode: 0,
        ExitDelay: TimeSpan.FromMilliseconds(10),
        EmitMatchingShutdownComplete: true);

    private async Task RunStartupAsync()
    {
        try
        {
            foreach (FakeOutputEvent outputEvent in _startupEvents)
            {
                await Task.Delay(outputEvent.Delay, _lifetimeCancellationTokenSource.Token);
                await WriteAsync(outputEvent);
            }

            await Task.Delay(_naturalExitDelay, _lifetimeCancellationTokenSource.Token);
            Complete(_naturalExitCode);
        }
        catch (OperationCanceledException)
        {
            Complete(exitCode: -1);
        }
    }

    private async Task RunStageAsync(FakeSessionStage stage, string? shutdownRequestId)
    {
        try
        {
            foreach (FakeOutputEvent outputEvent in stage.OutputEvents)
            {
                await Task.Delay(outputEvent.Delay, _lifetimeCancellationTokenSource.Token);
                await WriteAsync(outputEvent);
            }

            if (stage.EmitMatchingShutdownComplete && !string.IsNullOrWhiteSpace(shutdownRequestId))
            {
                await WriteAsync(new FakeOutputEvent(
                    TimeSpan.Zero,
                    FakeOutputStream.StdOut,
                    SessionLinePrefix + JsonSerializer.Serialize(new
                    {
                        Version = 1,
                        Kind = "shutdown-complete",
                        RequestId = shutdownRequestId,
                    })));
            }

            if (stage.ExitCode is int exitCode)
            {
                await Task.Delay(stage.ExitDelay, _lifetimeCancellationTokenSource.Token);
                Complete(exitCode);
            }
        }
        catch (OperationCanceledException)
        {
            Complete(exitCode: -1);
        }
    }

    private async Task WriteAsync(FakeOutputEvent outputEvent)
    {
        Channel<string> channel = outputEvent.Stream == FakeOutputStream.StdOut
            ? _stdOutChannel
            : _stdErrChannel;
        await channel.Writer.WriteAsync(outputEvent.Line, _lifetimeCancellationTokenSource.Token);
    }

    private static string? GetShutdownRequestId(string command)
    {
        try
        {
            JsonElement payload = JsonSerializer.Deserialize<JsonElement>(command);
            return payload.TryGetProperty("Kind", out JsonElement kind)
                && string.Equals(kind.GetString(), "shutdown", StringComparison.Ordinal)
                && payload.TryGetProperty("RequestId", out JsonElement requestId)
                ? requestId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Complete(int exitCode)
    {
        if (HasExited)
        {
            return;
        }

        ExitCode = exitCode;
        _ = _stdOutChannel.Writer.TryComplete();
        _ = _stdErrChannel.Writer.TryComplete();
        _ = _exitTaskSource.TrySetResult();
    }
}

internal sealed class FakeGodotProcessFactory(params FakeGodotProcess[] processes) : IGodotProcessFactory
{
    private readonly Queue<FakeGodotProcess> _processes = new(processes);
    private readonly List<FakeGodotProcess> _createdProcesses = [];
    private List<IReadOnlyList<string>> _invocations { get; } = [];

    public IReadOnlyList<IReadOnlyList<string>> Invocations => _invocations;

    public IReadOnlyList<FakeGodotProcess> CreatedProcesses => _createdProcesses;

    public IGodotProcess Create(IReadOnlyList<string> commandLineArguments)
    {
        _invocations.Add([.. commandLineArguments]);

        FakeGodotProcess process = _processes.Dequeue();
        _createdProcesses.Add(process);
        return process;
    }
}
