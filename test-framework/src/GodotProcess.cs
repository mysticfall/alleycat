using System.Diagnostics;

namespace AlleyCat.TestFramework;

/// <summary>
/// Creates <see cref="IGodotProcess"/> instances for Godot session and preflight launches.
/// </summary>
internal interface IGodotProcessFactory
{
    /// <summary>
    /// Creates a process wrapper for one Godot launch with the supplied command-line arguments.
    /// </summary>
    /// <param name="commandLineArguments">Command-line arguments passed to the Godot binary.</param>
    IGodotProcess Create(IReadOnlyList<string> commandLineArguments);
}

/// <summary>
/// A redirected Godot process with line-oriented access to standard input, output, and error.
/// </summary>
internal interface IGodotProcess : IDisposable
{
    /// <summary>Starts the process.</summary>
    bool Start();

    /// <summary>Gets whether the process has exited.</summary>
    bool HasExited
    {
        get;
    }

    /// <summary>Gets the process exit code.</summary>
    int ExitCode
    {
        get;
    }

    /// <summary>Waits asynchronously for the process to exit.</summary>
    /// <param name="cancellationToken">Cancellation token aborting the wait.</param>
    Task WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Reads one line from redirected standard output, or <c>null</c> at end of stream.</summary>
    /// <param name="cancellationToken">Cancellation token aborting the read.</param>
    Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken);

    /// <summary>Reads one line from redirected standard error, or <c>null</c> at end of stream.</summary>
    /// <param name="cancellationToken">Cancellation token aborting the read.</param>
    Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes one command line to the process's redirected standard input.
    /// </summary>
    /// <param name="line">The line to write, without a line terminator.</param>
    /// <param name="cancellationToken">Cancellation token aborting the write.</param>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Gracefully closes the process's redirected standard input, signalling that the host will send no
    /// further commands.
    /// </summary>
    void CloseStandardInput();

    /// <summary>Kills the process.</summary>
    /// <param name="entireProcessTree">Whether to kill the entire descendant process tree.</param>
    void Kill(bool entireProcessTree);
}

internal sealed class SystemGodotProcessFactory(string godotBinaryPath, string workspaceRootPath) : IGodotProcessFactory
{
    private const string RuntimeContextEnvironmentVariable = "ALLEYCAT_RUNTIME_CONTEXT";
    private const string RuntimeContextIntegrationTestValue = "integration-test";

    public IGodotProcess Create(IReadOnlyList<string> commandLineArguments)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = godotBinaryPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspaceRootPath,
        };

        processStartInfo.EnvironmentVariables[RuntimeContextEnvironmentVariable] = RuntimeContextIntegrationTestValue;

        foreach (string argument in commandLineArguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        return new SystemGodotProcess(processStartInfo);
    }
}

internal sealed class SystemGodotProcess(ProcessStartInfo startInfo) : IGodotProcess
{
    private readonly Process _process = new()
    {
        StartInfo = startInfo,
        EnableRaisingEvents = true,
    };

    public bool Start() => _process.Start();

    public bool HasExited => _process.HasExited;

    public int ExitCode => _process.ExitCode;

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);

    public Task<string?> ReadStandardOutputLineAsync(CancellationToken cancellationToken)
        => _process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

    public Task<string?> ReadStandardErrorLineAsync(CancellationToken cancellationToken)
        => _process.StandardError.ReadLineAsync(cancellationToken).AsTask();

    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteAsync(line.AsMemory(), cancellationToken);
        await _process.StandardInput.WriteAsync(Environment.NewLine.AsMemory(), cancellationToken);
    }

    public void CloseStandardInput() => _process.StandardInput.Close();

    public void Kill(bool entireProcessTree) => _process.Kill(entireProcessTree);

    public void Dispose() => _process.Dispose();
}
