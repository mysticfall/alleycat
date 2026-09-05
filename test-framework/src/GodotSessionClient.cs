using System.Text.Json;

namespace AlleyCat.TestFramework;

/// <summary>
/// Wire-protocol constants for the persistent Godot <c>--integration-test-session</c> runtime.
/// </summary>
internal static class GodotSessionProtocol
{
    public const string SessionCommandArg = "--integration-test-session";
    public const string LinePrefix = "ALLEYCAT_INTEGRATION_SESSION:";
    public const int Version = 1;
    public const string ReadyEventKind = "ready";
    public const string ResultEventKind = "result";
    public const string ShutdownCompleteEventKind = "shutdown-complete";
    public const string RunCommandKind = "run";
    public const string ShutdownCommandKind = "shutdown";
    public const string PassedOutcome = "passed";
    public const string FailedOutcome = "failed";
    public const string ErrorOutcome = "error";
}

/// <summary>
/// Terminal outcome of one test executed inside a session.
/// </summary>
internal enum GodotSessionTestOutcome
{
    Passed,

    Failed,

    Error,
}

/// <summary>
/// Result of one session-dispatched test execution, including the post-test verdict on whether the session may
/// execute further tests.
/// </summary>
internal sealed record GodotSessionTestResult(GodotSessionTestOutcome Outcome, Exception? Error, bool SessionReusable);

/// <summary>
/// A framework or protocol failure raised while driving a Godot integration-test session.
/// </summary>
internal sealed class GodotSessionException : Exception
{
    public GodotSessionException(string message)
        : base(message)
    {
    }

    public GodotSessionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Host-side client for one persistent Godot <c>--integration-test-session</c> process, correlating newline-delimited
/// JSON protocol events read from stdout with <c>run</c> and <c>shutdown</c> commands written to stdin.
/// </summary>
/// <remarks>
/// <para>
/// Only lines prefixed with <c>ALLEYCAT_INTEGRATION_SESSION:</c> are treated as protocol; all other output is retained
/// as diagnostics. Results are correlated by <c>RequestId</c> and only one test may be in flight at a time.
/// </para>
/// <para>
/// Any protocol-shape violation — malformed JSON, an unknown kind, an unsupported version, or a result for an unknown
/// request — taints the session: the in-flight request fails with a <see cref="GodotSessionException"/> and
/// <see cref="IsUsable"/> turns <c>false</c> so the host replaces the session for the remaining tests.
/// </para>
/// </remarks>
internal sealed class GodotSessionClient : IDisposable
{
    private const int ExitDrainGraceTimeoutMs = 500;
    private const int RetainedOutputLineLimit = 10_000;
    private const int MalformedLinePreviewLength = 200;

    private static readonly JsonSerializerOptions _commandSerialisationOptions = new();
    private static readonly JsonSerializerOptions _eventParsingOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IGodotProcess _process;
    private readonly Lock _sync = new();
    private readonly List<string> _outputLines = [];
    private readonly Dictionary<string, TaskCompletionSource<SessionWireEvent>> _pendingResults = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<SessionWireEvent> _readyCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<SessionWireEvent> _shutdownCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _exitCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifetimeCancellationTokenSource = new();

    private string? _protocolFaultMessage;
    private string? _shutdownRequestId;
    private int _outputMark;
    private bool _closed;

    private GodotSessionClient(IGodotProcess process)
    {
        _process = process;
        _ = Task.Run(ReadStandardOutputAsync);
        _ = Task.Run(ReadStandardErrorAsync);
        _ = Task.Run(MonitorExitAsync);
    }

    /// <summary>
    /// Gets whether the session may execute further tests: the process is alive and no protocol fault has occurred.
    /// </summary>
    public bool IsUsable
    {
        get
        {
            lock (_sync)
            {
                return !_closed
                    && _protocolFaultMessage is null
                    && !_exitCompletion.Task.IsCompleted;
            }
        }
    }

    /// <summary>
    /// Starts one session process and begins reading its redirected output streams.
    /// </summary>
    /// <param name="processFactory">The factory creating the Godot process.</param>
    /// <param name="commandLineArguments">The session launch arguments.</param>
    /// <returns>A client driving the started process.</returns>
    /// <exception cref="GodotSessionException">The process could not be started.</exception>
    public static GodotSessionClient Start(IGodotProcessFactory processFactory, IReadOnlyList<string> commandLineArguments)
    {
        IGodotProcess process = processFactory.Create(commandLineArguments);

        try
        {
            if (!process.Start())
            {
                throw new GodotSessionException("The Godot session process failed to start.");
            }
        }
        catch (Exception exception) when (exception is not GodotSessionException)
        {
            process.Dispose();
            throw new GodotSessionException($"Unable to start the Godot session process: {exception.Message}", exception);
        }
        catch (GodotSessionException)
        {
            process.Dispose();
            throw;
        }

        return new GodotSessionClient(process);
    }

    /// <summary>
    /// Waits for the session's <c>ready</c> protocol event.
    /// </summary>
    /// <param name="timeoutMs">Maximum wait in milliseconds.</param>
    /// <param name="cancellationToken">Host cancellation token.</param>
    /// <exception cref="GodotSessionException">
    /// The session did not become ready in time, exited first, or reported a startup failure.
    /// </exception>
    public async Task WaitForReadyAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        SessionWireEvent readyEvent;

        try
        {
            using var readyCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyCancellationTokenSource.CancelAfter(timeoutMs);
            readyEvent = await _readyCompletion.Task.WaitAsync(readyCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GodotSessionException(
                $"The Godot session did not report ready within {timeoutMs}ms.{BuildFullOutputSnapshot()}");
        }

        if (string.Equals(readyEvent.Outcome, GodotSessionProtocol.ErrorOutcome, StringComparison.Ordinal))
        {
            throw new GodotSessionException(
                $"The Godot session failed during startup: {readyEvent.Message ?? "<no message>"}.{BuildFullOutputSnapshot()}");
        }

        if (readyEvent.Outcome is not null)
        {
            throw new GodotSessionException(
                $"The Godot session reported an unexpected ready outcome '{readyEvent.Outcome}'.{BuildFullOutputSnapshot()}");
        }
    }

    /// <summary>
    /// Dispatches one test into the session and awaits its correlated <c>result</c> event.
    /// </summary>
    /// <param name="requestId">The stable test UID correlating command and result.</param>
    /// <param name="typeName">The test class full name.</param>
    /// <param name="methodName">The test method name.</param>
    /// <param name="timeoutMs">Per-request timeout in milliseconds.</param>
    /// <param name="cancellationToken">Host cancellation token.</param>
    /// <returns>The terminal session result for the test, never retried by this client.</returns>
    public async Task<GodotSessionTestResult> RunTestAsync(
        string requestId,
        string typeName,
        string methodName,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        TaskCompletionSource<SessionWireEvent> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(GodotSessionClient));
            }

            if (_protocolFaultMessage is not null)
            {
                return new GodotSessionTestResult(
                    GodotSessionTestOutcome.Error,
                    new GodotSessionException(_protocolFaultMessage),
                    SessionReusable: false);
            }

            _outputMark = _outputLines.Count;
            _pendingResults[requestId] = completion;
        }

        try
        {
            string command = JsonSerializer.Serialize(
                new SessionRunCommandPayload(
                    GodotSessionProtocol.Version,
                    GodotSessionProtocol.RunCommandKind,
                    requestId,
                    typeName,
                    methodName),
                _commandSerialisationOptions);
            await _process.WriteLineAsync(command, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            RemovePendingRequest(requestId);
            throw;
        }
        catch (Exception exception)
        {
            RemovePendingRequest(requestId);
            return BuildErrorResult(
                new GodotSessionException(
                    $"Failed to send the run command to the Godot session: {exception.Message}{BuildOutputSnapshotSinceMark()}",
                    exception),
                sessionReusable: false);
        }

        SessionWireEvent resultEvent;

        try
        {
            using var requestCancellationTokenSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellationTokenSource.CancelAfter(timeoutMs);
            resultEvent = await completion.Task.WaitAsync(requestCancellationTokenSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HandleRequestTimeout(requestId, timeoutMs);
        }
        catch (GodotSessionException exception)
        {
            // The exit monitor or a protocol fault condemned the in-flight request.
            return BuildErrorResult(exception, sessionReusable: false);
        }
        finally
        {
            RemovePendingRequest(requestId);
        }

        return BuildResultFromWireEvent(resultEvent);
    }

    /// <summary>
    /// Requests graceful shutdown, closes standard input, waits for the matching acknowledgement and process exit
    /// within the cleanup grace, then force-kills the process tree best-effort when either condition is not met.
    /// </summary>
    /// <param name="cleanupTimeoutMs">Graceful-exit grace in milliseconds before the forced kill.</param>
    public async Task ShutdownAsync(int cleanupTimeoutMs)
    {
        string requestId = $"shutdown-{Guid.NewGuid():N}";
        bool shouldSendCommand;

        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _shutdownRequestId = requestId;
            shouldSendCommand = !_exitCompletion.Task.IsCompleted;
        }

        using var cleanupCancellationTokenSource = new CancellationTokenSource();
        cleanupCancellationTokenSource.CancelAfter(cleanupTimeoutMs);
        Exception? shutdownFailure = null;

        if (shouldSendCommand)
        {
            try
            {
                string command = JsonSerializer.Serialize(
                    new SessionShutdownCommandPayload(
                        GodotSessionProtocol.Version,
                        GodotSessionProtocol.ShutdownCommandKind,
                        requestId),
                    _commandSerialisationOptions);
                await _process.WriteLineAsync(command, cleanupCancellationTokenSource.Token);
            }
            catch (Exception exception)
            {
                shutdownFailure = new GodotSessionException(
                    $"Failed to send shutdown request '{requestId}': {exception.Message}{BuildOutputSnapshotSinceMark()}",
                    exception);
            }
        }

        try
        {
            _process.CloseStandardInput();
        }
        catch
        {
            // Best effort.
        }

        if (shutdownFailure is null)
        {
            try
            {
                _ = await _shutdownCompletion.Task.WaitAsync(cleanupCancellationTokenSource.Token);
                _ = await _exitCompletion.Task.WaitAsync(cleanupCancellationTokenSource.Token);
                return;
            }
            catch (OperationCanceledException)
            {
                shutdownFailure = new GodotSessionException(
                    $"The Godot session did not report matching shutdown completion and exit within {cleanupTimeoutMs}ms.{BuildOutputSnapshotSinceMark()}");
            }
            catch (GodotSessionException exception)
            {
                shutdownFailure = exception;
            }
            catch (Exception exception)
            {
                shutdownFailure = new GodotSessionException(
                    $"The Godot session shutdown failed: {exception.Message}{BuildOutputSnapshotSinceMark()}",
                    exception);
            }
        }

        FaultProtocol(shutdownFailure.Message);
        KillProcessIfAlive();
        throw shutdownFailure;
    }

    /// <summary>
    /// Kills the process when still alive and releases its resources.
    /// </summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        _lifetimeCancellationTokenSource.Cancel();
        KillProcessIfAlive();
        _process.Dispose();
        _lifetimeCancellationTokenSource.Dispose();
    }

    private GodotSessionTestResult HandleRequestTimeout(string requestId, int timeoutMs)
    {
        RemovePendingRequest(requestId);
        string output = BuildOutputSnapshotSinceMark();
        KillProcessIfAlive();

        return BuildErrorResult(
            new TimeoutException($"The Godot session test timed out after {timeoutMs}ms.{output}"),
            sessionReusable: false);
    }

    private GodotSessionTestResult BuildResultFromWireEvent(SessionWireEvent resultEvent)
    {
        bool sessionReusable = resultEvent.SessionReusable == true && IsUsable;

        switch (resultEvent.Outcome)
        {
            case GodotSessionProtocol.PassedOutcome:
                // A passed test with a non-reusable session means baseline restoration failed; the runtime moved the
                // diagnostics into the message and is quitting, so the test is reported as a framework error.
                return resultEvent.SessionReusable == false
                    ? new GodotSessionTestResult(
                        GodotSessionTestOutcome.Error,
                        BuildFailureException(resultEvent.Message, resultEvent.Stack),
                        SessionReusable: false)
                    : new GodotSessionTestResult(GodotSessionTestOutcome.Passed, null, sessionReusable);
            case GodotSessionProtocol.FailedOutcome:
                return new GodotSessionTestResult(
                    GodotSessionTestOutcome.Failed,
                    BuildFailureException(resultEvent.Message, resultEvent.Stack),
                    sessionReusable);
            case GodotSessionProtocol.ErrorOutcome:
                return new GodotSessionTestResult(
                    GodotSessionTestOutcome.Error,
                    BuildFailureException(resultEvent.Message, resultEvent.Stack),
                    sessionReusable);
            default:
                string details =
                    $"A session 'result' event carried unknown outcome '{resultEvent.Outcome ?? "<missing>"}'.{BuildOutputSnapshotSinceMark()}";
                FaultProtocol(details);
                return BuildErrorResult(new GodotSessionException(details), sessionReusable: false);
        }
    }

    private static Exception BuildFailureException(string? message, string? stack)
        => new(BuildFailureMessage(message, stack));

    /// <summary>
    /// Combines a wire failure message and stack into one diagnostic string with a fallback when both are empty.
    /// </summary>
    internal static string BuildFailureMessage(string? message, string? stack)
    {
        return string.IsNullOrWhiteSpace(message)
            && string.IsNullOrWhiteSpace(stack)
            ? "The Godot session reported an unknown failure."
            : string.IsNullOrWhiteSpace(stack)
            ? message!
            : string.IsNullOrWhiteSpace(message)
            ? stack!
            : $"{message}{Environment.NewLine}{stack}";
    }

    private static GodotSessionTestResult BuildErrorResult(Exception error, bool sessionReusable)
        => new(GodotSessionTestOutcome.Error, error, sessionReusable);

    private void RemovePendingRequest(string requestId)
    {
        lock (_sync)
        {
            _ = _pendingResults.Remove(requestId);
        }
    }

    private async Task ReadStandardOutputAsync()
    {
        try
        {
            while (true)
            {
                string? line = await _process.ReadStandardOutputLineAsync(_lifetimeCancellationTokenSource.Token);
                if (line is null)
                {
                    return;
                }

                HandleStandardOutputLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Stream draining stops when the session is torn down.
        }
        catch
        {
            // A broken stdout pipe is equivalent to session termination; the exit monitor reports it.
        }
    }

    private async Task ReadStandardErrorAsync()
    {
        try
        {
            while (true)
            {
                string? line = await _process.ReadStandardErrorLineAsync(_lifetimeCancellationTokenSource.Token);
                if (line is null)
                {
                    return;
                }

                lock (_sync)
                {
                    AppendOutputLineUnsafe($"[stderr] {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stream draining stops when the session is torn down.
        }
        catch
        {
            // A broken stderr pipe is equivalent to session termination; the exit monitor reports it.
        }
    }

    private void HandleStandardOutputLine(string line)
    {
        lock (_sync)
        {
            AppendOutputLineUnsafe(line);

            if (_closed || !line.StartsWith(GodotSessionProtocol.LinePrefix, StringComparison.Ordinal))
            {
                return;
            }

            string payload = line[GodotSessionProtocol.LinePrefix.Length..];
            SessionWireEvent? wireEvent = TryParseWireEvent(payload, out string? parseError);

            if (wireEvent is null)
            {
                FaultProtocolUnsafe(
                    $"A malformed session protocol line was received ({parseError}): {TruncateLine(payload)}");
                return;
            }

            if (wireEvent.Version != GodotSessionProtocol.Version)
            {
                FaultProtocolUnsafe(
                    $"A session protocol line with unsupported version '{wireEvent.Version?.ToString() ?? "<missing>"}' was received.");
                return;
            }

            switch (wireEvent.Kind)
            {
                case GodotSessionProtocol.ReadyEventKind:
                    if (!_readyCompletion.TrySetResult(wireEvent))
                    {
                        FaultProtocolUnsafe("A duplicate session 'ready' event was received.");
                    }

                    return;
                case GodotSessionProtocol.ResultEventKind:
                    CompleteResultUnsafe(wireEvent);
                    return;
                case GodotSessionProtocol.ShutdownCompleteEventKind:
                    CompleteShutdownUnsafe(wireEvent);
                    return;
                default:
                    FaultProtocolUnsafe($"A session protocol line with unknown kind '{wireEvent.Kind ?? "<missing>"}' was received.");
                    return;
            }
        }
    }

    private void CompleteResultUnsafe(SessionWireEvent wireEvent)
    {
        string requestId = wireEvent.RequestId ?? string.Empty;

        if (_pendingResults.Remove(requestId, out TaskCompletionSource<SessionWireEvent>? pending))
        {
            _ = pending.TrySetResult(wireEvent);
            return;
        }

        FaultProtocolUnsafe($"A session 'result' event was received for unknown request '{requestId}'.");
    }

    private void CompleteShutdownUnsafe(SessionWireEvent wireEvent)
    {
        string requestId = wireEvent.RequestId!;

        if (!string.Equals(requestId, _shutdownRequestId, StringComparison.Ordinal))
        {
            FaultProtocolUnsafe($"A session 'shutdown-complete' event was received for unknown request '{requestId}'.");
            return;
        }

        if (!_shutdownCompletion.TrySetResult(wireEvent))
        {
            FaultProtocolUnsafe("A duplicate session 'shutdown-complete' event was received.");
        }
    }

    private async Task MonitorExitAsync()
    {
        try
        {
            await _process.WaitForExitAsync(_lifetimeCancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Process telemetry failures are treated as exit; the condemnation below still applies.
        }

        int? exitCode = TryGetExitCode();

        lock (_sync)
        {
            _ = _exitCompletion.TrySetResult(true);
        }

        try
        {
            // Give buffered protocol lines a brief chance to drain so a result emitted just before exit is kept.
            await Task.Delay(ExitDrainGraceTimeoutMs, _lifetimeCancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            string output = RenderOutputSnapshot([.. _outputLines.Skip(Math.Clamp(_outputMark, 0, _outputLines.Count))]);

            if (!_readyCompletion.Task.IsCompleted)
            {
                _ = _readyCompletion.TrySetException(new GodotSessionException(
                    $"The Godot session process exited before reporting ready. ExitCode={FormatExitCode(exitCode)}.{output}"));
            }

            foreach (KeyValuePair<string, TaskCompletionSource<SessionWireEvent>> pending in _pendingResults)
            {
                _ = pending.Value.TrySetException(new GodotSessionException(
                    $"The Godot session process exited before the test result arrived. ExitCode={FormatExitCode(exitCode)}.{output}"));
            }

            _pendingResults.Clear();
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatExitCode(int? exitCode) => exitCode?.ToString() ?? "<unknown>";

    private void FaultProtocol(string message)
    {
        lock (_sync)
        {
            FaultProtocolUnsafe(message);
        }
    }

    private void FaultProtocolUnsafe(string message)
    {
        if (_closed || _protocolFaultMessage is not null)
        {
            return;
        }

        _protocolFaultMessage = $"{message}{RenderOutputSnapshot([.. _outputLines.Skip(Math.Clamp(_outputMark, 0, _outputLines.Count))])}";
        var protocolException = new GodotSessionException(_protocolFaultMessage);

        if (!_readyCompletion.Task.IsCompleted)
        {
            _ = _readyCompletion.TrySetException(protocolException);
        }

        foreach (KeyValuePair<string, TaskCompletionSource<SessionWireEvent>> pending in _pendingResults)
        {
            _ = pending.Value.TrySetException(protocolException);
        }

        _pendingResults.Clear();
        _ = _shutdownCompletion.TrySetException(protocolException);
    }

    private void KillProcessIfAlive()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private void AppendOutputLineUnsafe(string line)
    {
        _outputLines.Add(line);

        if (_outputLines.Count <= RetainedOutputLineLimit)
        {
            return;
        }

        int excess = _outputLines.Count - RetainedOutputLineLimit;
        _outputLines.RemoveRange(0, excess);
        _outputMark = Math.Max(0, _outputMark - excess);
    }

    private string BuildFullOutputSnapshot()
    {
        string[] lines;

        lock (_sync)
        {
            lines = [.. _outputLines];
        }

        return RenderOutputSnapshot(lines);
    }

    private string BuildOutputSnapshotSinceMark()
    {
        string[] lines;

        lock (_sync)
        {
            lines = [.. _outputLines.Skip(Math.Clamp(_outputMark, 0, _outputLines.Count))];
        }

        return RenderOutputSnapshot(lines);
    }

    private static string RenderOutputSnapshot(IReadOnlyList<string> lines)
        => lines.Count == 0
            ? $"{Environment.NewLine}Captured session output: <empty>"
            : $"{Environment.NewLine}Captured session output:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";

    private static SessionWireEvent? TryParseWireEvent(string payload, out string? parseError)
    {
        try
        {
            SessionWireEvent? wireEvent = JsonSerializer.Deserialize<SessionWireEvent>(payload, _eventParsingOptions);
            if (wireEvent is null)
            {
                parseError = "payload deserialised to null";
                return null;
            }

            return TryValidateWireEvent(wireEvent, out parseError) ? wireEvent : null;
        }
        catch (JsonException exception)
        {
            parseError = $"JSON parsing failed: {exception.Message}";
            return null;
        }
    }

    private static bool TryValidateWireEvent(SessionWireEvent wireEvent, out string? error)
    {
        if (wireEvent.Version != GodotSessionProtocol.Version)
        {
            error = $"Version must be {GodotSessionProtocol.Version}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(wireEvent.Kind))
        {
            error = "Kind was missing or empty";
            return false;
        }

        switch (wireEvent.Kind)
        {
            case GodotSessionProtocol.ReadyEventKind:
                return TryValidateReadyEvent(wireEvent, out error);
            case GodotSessionProtocol.ResultEventKind:
                return TryValidateResultEvent(wireEvent, out error);
            case GodotSessionProtocol.ShutdownCompleteEventKind:
                if (string.IsNullOrWhiteSpace(wireEvent.RequestId))
                {
                    error = "'shutdown-complete' requires a non-empty RequestId";
                    return false;
                }

                error = null;
                return true;
            default:
                error = $"Kind '{wireEvent.Kind}' was not recognised";
                return false;
        }
    }

    private static bool TryValidateReadyEvent(SessionWireEvent wireEvent, out string? error)
    {
        if (wireEvent.Outcome is null && wireEvent.Message is null)
        {
            error = null;
            return true;
        }

        if (string.Equals(wireEvent.Outcome, GodotSessionProtocol.ErrorOutcome, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(wireEvent.Message))
        {
            error = null;
            return true;
        }

        error = "'ready' must omit Outcome and Message or provide Outcome 'error' with a non-empty Message";
        return false;
    }

    private static bool TryValidateResultEvent(SessionWireEvent wireEvent, out string? error)
    {
        if (string.IsNullOrWhiteSpace(wireEvent.RequestId))
        {
            error = "'result' requires a non-empty RequestId";
            return false;
        }

        if (wireEvent.Outcome is not GodotSessionProtocol.PassedOutcome
            and not GodotSessionProtocol.FailedOutcome
            and not GodotSessionProtocol.ErrorOutcome)
        {
            error = $"'result' carried unsupported Outcome '{wireEvent.Outcome ?? "<missing>"}'";
            return false;
        }

        if (!wireEvent.SessionReusable.HasValue)
        {
            error = "'result' requires SessionReusable";
            return false;
        }

        error = null;
        return true;
    }

    private static string TruncateLine(string line)
        => line.Length <= MalformedLinePreviewLength ? line : $"{line[..MalformedLinePreviewLength]}[...]";

    /// <summary>
    /// One protocol event received from the runtime, with nullable fields so malformed shapes are rejected by
    /// validation rather than by deserialisation.
    /// </summary>
    private sealed record SessionWireEvent(
        int? Version,
        string? Kind,
        string? Outcome,
        string? Message,
        string? RequestId,
        string? Stack,
        bool? SessionReusable);

    private sealed record SessionRunCommandPayload(int Version, string Kind, string RequestId, string Type, string Method);

    private sealed record SessionShutdownCommandPayload(int Version, string Kind, string RequestId);
}
