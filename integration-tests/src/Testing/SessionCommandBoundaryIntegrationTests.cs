using System.Diagnostics;
using System.Text.Json;
using AlleyCat.TestFramework;
using Xunit;

namespace AlleyCat.IntegrationTests.Testing;

/// <summary>
/// Drives a real <c>TestRuntimeRunner</c> session over redirected standard streams to verify that malformed
/// host-to-runtime commands are logged and ignored without poisoning the session.
/// </summary>
[Headless]
public sealed class SessionCommandBoundaryIntegrationTests
{
    private const string MalformedCommandWarningPrefix =
        "[Warning] AlleyCat.Testing.TestRuntimeRunner: Integration test session: Ignoring malformed session command";
    private const int SessionTimeoutMs = 30_000;

    private static readonly string[] _malformedCommands =
    [
        "{}",
        "[]",
        "null",
        JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "run",
            RequestId = "incomplete-run",
            Type = typeof(SessionCommandBoundaryFixture).FullName,
        }),
        JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "unrecognised",
            RequestId = "unknown-kind",
        }),
    ];

    /// <summary>
    /// Proves malformed newline-delimited host commands are warning-skipped before Godot-thread dispatch, then proves
    /// that the same process still executes one valid request and acknowledges a correlated shutdown.
    /// </summary>
    [Fact]
    public async Task Session_IgnoresMalformedHostCommands_ThenRunsAndShutsDownCleanly()
    {
        await using var session = SessionProcess.Start();
        _ = await session.WaitForProtocolEventAsync(
            protocolEvent => protocolEvent.Kind == "ready" && protocolEvent.Version == 1,
            SessionTimeoutMs);

        foreach (string malformedCommand in _malformedCommands)
        {
            await session.WriteLineAsync(malformedCommand);
        }

        await session.WaitForOutputAsync(
            lines => lines.Count(line => line.Contains(MalformedCommandWarningPrefix, StringComparison.Ordinal))
                == _malformedCommands.Length,
            SessionTimeoutMs);

        Assert.False(session.HasExited);
        Assert.DoesNotContain(session.GetProtocolEvents(), protocolEvent => protocolEvent.Kind == "result");

        string requestId = $"malformed-command-boundary-{Guid.NewGuid():N}";
        await session.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "run",
            RequestId = requestId,
            Type = typeof(SessionCommandBoundaryFixture).FullName!,
            Method = nameof(SessionCommandBoundaryFixture.Passes),
        }));

        SessionProtocolEvent result = await session.WaitForProtocolEventAsync(
            protocolEvent => protocolEvent.Kind == "result" && protocolEvent.RequestId == requestId,
            SessionTimeoutMs);

        Assert.Equal(1, result.Version);
        Assert.Equal("passed", result.Outcome);
        Assert.Equal(true, result.SessionReusable);
        Assert.False(session.HasExited);

        SessionProtocolEvent[] results = [.. session.GetProtocolEvents().Where(protocolEvent => protocolEvent.Kind == "result")];
        SessionProtocolEvent onlyResult = Assert.Single(results);
        Assert.Equal(requestId, onlyResult.RequestId);

        string shutdownRequestId = $"shutdown-{Guid.NewGuid():N}";
        await session.WriteLineAsync(JsonSerializer.Serialize(new
        {
            Version = 1,
            Kind = "shutdown",
            RequestId = shutdownRequestId,
        }));
        session.CloseStandardInput();

        SessionProtocolEvent shutdownComplete = await session.WaitForProtocolEventAsync(
            protocolEvent => protocolEvent.Kind == "shutdown-complete" && protocolEvent.RequestId == shutdownRequestId,
            SessionTimeoutMs);

        Assert.Equal(1, shutdownComplete.Version);
        Assert.False(string.IsNullOrWhiteSpace(shutdownComplete.RequestId));
        await session.WaitForExitAsync(SessionTimeoutMs);
        Assert.Equal(0, session.ExitCode);

        string[] allOutput = session.GetAllOutput();
        Assert.Equal(
            _malformedCommands.Length,
            allOutput.Count(line => line.Contains(MalformedCommandWarningPrefix, StringComparison.Ordinal)));
        Assert.All(_malformedCommands, command => Assert.Contains(allOutput, line => line.Contains(command, StringComparison.Ordinal)));
    }
}

/// <summary>
/// Deliberately minimal passing target for <see cref="SessionCommandBoundaryIntegrationTests"/>'s child session.
/// It remains independently valid under ordinary integration-test discovery.
/// </summary>
[Headless]
public sealed class SessionCommandBoundaryFixture
{
    /// <summary>Passes deterministically when dispatched through the child session.</summary>
    [Fact]
    public void Passes()
    {
    }
}

internal sealed class SessionProcess : IAsyncDisposable
{
    private const string SessionLinePrefix = "ALLEYCAT_INTEGRATION_SESSION:";
    private const string GodotPathEnvironmentVariable = "GODOT_PATH";
    private const string RuntimeContextEnvironmentVariable = "ALLEYCAT_RUNTIME_CONTEXT";
    private const string IntegrationTestRuntimeContext = "integration-test";

    private readonly Process _process;
    private readonly List<string> _standardOutputLines = [];
    private readonly List<string> _standardErrorLines = [];
    private readonly Lock _outputLock = new();
    private readonly Task _standardOutputReader;
    private readonly Task _standardErrorReader;
    private bool _standardInputClosed;

    private SessionProcess(Process process)
    {
        _process = process;
        _standardOutputReader = ReadLinesAsync(_process.StandardOutput, _standardOutputLines, _outputLock);
        _standardErrorReader = ReadLinesAsync(_process.StandardError, _standardErrorLines, _outputLock);
    }

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public static SessionProcess Start()
    {
        string workspaceRoot = ResolveWorkspaceRoot();
        ProcessStartInfo startInfo = new()
        {
            FileName = Environment.GetEnvironmentVariable(GodotPathEnvironmentVariable) ?? "godot-mono",
            WorkingDirectory = workspaceRoot,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment[RuntimeContextEnvironmentVariable] = IntegrationTestRuntimeContext;
        startInfo.ArgumentList.Add("--headless");
        startInfo.ArgumentList.Add("--xr-mode");
        startInfo.ArgumentList.Add("off");
        startInfo.ArgumentList.Add("--path");
        startInfo.ArgumentList.Add("game");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--integration-test-session");
        startInfo.ArgumentList.Add("--probe-assembly");
        startInfo.ArgumentList.Add(typeof(SessionCommandBoundaryIntegrationTests).Assembly.Location);

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The child Godot integration-test session failed to start.");
        }

        process.StandardInput.AutoFlush = true;
        return new SessionProcess(process);
    }

    public async Task WriteLineAsync(string command)
        => await _process.StandardInput.WriteLineAsync(command.AsMemory(), CancellationToken.None);

    public void CloseStandardInput()
    {
        if (_standardInputClosed)
        {
            return;
        }

        _standardInputClosed = true;
        _process.StandardInput.Close();
    }

    public async Task<SessionProtocolEvent> WaitForProtocolEventAsync(
        Func<SessionProtocolEvent, bool> predicate,
        int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            foreach (SessionProtocolEvent protocolEvent in GetProtocolEvents())
            {
                if (predicate(protocolEvent))
                {
                    return protocolEvent;
                }
            }

            ThrowIfExitedBeforeExpectedEvent();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for a session protocol event.{RenderOutput()}");
    }

    public async Task WaitForOutputAsync(Func<string[], bool> predicate, int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);

        while (!timeout.IsCancellationRequested)
        {
            if (predicate(GetAllOutput()))
            {
                return;
            }

            ThrowIfExitedBeforeExpectedEvent();
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException($"Timed out after {timeoutMs}ms waiting for session output.{RenderOutput()}");
    }

    public SessionProtocolEvent[] GetProtocolEvents()
    {
        string[] outputLines;
        lock (_outputLock)
        {
            outputLines = [.. _standardOutputLines];
        }

        return
        [
            .. outputLines
                .Where(line => line.StartsWith(SessionLinePrefix, StringComparison.Ordinal))
                .Select(TryParseProtocolEvent)
                .OfType<SessionProtocolEvent>(),
        ];
    }

    public string[] GetAllOutput()
    {
        lock (_outputLock)
        {
            return [.. _standardOutputLines, .. _standardErrorLines];
        }
    }

    public async Task WaitForExitAsync(int timeoutMs)
    {
        using var timeout = new CancellationTokenSource(timeoutMs);
        await _process.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(_standardOutputReader, _standardErrorReader);
    }

    public async ValueTask DisposeAsync()
    {
        CloseStandardInput();

        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        try
        {
            await _process.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process has already been disposed by a preceding startup failure.
        }

        await Task.WhenAll(_standardOutputReader, _standardErrorReader);
        _process.Dispose();
    }

    private static async Task ReadLinesAsync(StreamReader reader, List<string> destination, Lock outputLock)
    {
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (outputLock)
                {
                    destination.Add(line);
                }
            }
        }
        catch (IOException)
        {
            // Killing the child session closes redirected streams before all buffered data can be read.
        }
        catch (ObjectDisposedException)
        {
            // Disposal after failed startup or forced cleanup closes redirected streams.
        }
    }

    private static SessionProtocolEvent? TryParseProtocolEvent(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line[SessionLinePrefix.Length..]);
            JsonElement root = document.RootElement;
            return new SessionProtocolEvent(
                root.TryGetProperty("Version", out JsonElement version) && version.TryGetInt32(out int parsedVersion)
                    ? parsedVersion
                    : null,
                root.TryGetProperty("Kind", out JsonElement kind) ? kind.GetString() : null,
                root.TryGetProperty("RequestId", out JsonElement requestId) ? requestId.GetString() : null,
                root.TryGetProperty("Outcome", out JsonElement outcome) ? outcome.GetString() : null,
                root.TryGetProperty("SessionReusable", out JsonElement sessionReusable)
                    && (sessionReusable.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    ? sessionReusable.GetBoolean()
                    : null);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ResolveWorkspaceRoot()
    {
        for (DirectoryInfo? current = new(Directory.GetCurrentDirectory()); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "game", "project.godot")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to resolve the workspace root containing game/project.godot.");
    }

    private void ThrowIfExitedBeforeExpectedEvent()
    {
        if (HasExited)
        {
            throw new InvalidOperationException(
                $"The child Godot integration-test session exited with code {ExitCode} before the expected event.{RenderOutput()}");
        }
    }

    private string RenderOutput()
    {
        string[] output = GetAllOutput();
        return output.Length == 0
            ? $"{Environment.NewLine}Captured child session output: <empty>"
            : $"{Environment.NewLine}Captured child session output:{Environment.NewLine}{string.Join(Environment.NewLine, output)}";
    }
}

internal sealed record SessionProtocolEvent(
    int? Version,
    string? Kind,
    string? RequestId,
    string? Outcome,
    bool? SessionReusable);
