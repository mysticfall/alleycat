using System.Diagnostics;

namespace AlleyCat.TestFramework;

/// <summary>
/// Detects Godot processes on the host that the framework did not launch, for isolated tests that observe process
/// activity across the whole host. Run inside an isolated session, every session process the framework launched has
/// already been shut down, so any Godot process other than the calling one was started by something else.
/// </summary>
public static class HostGodotProcesses
{
    /// <summary>One running process, as seen by the detector.</summary>
    /// <param name="Id">Operating-system process ID.</param>
    /// <param name="Name">Process name as reported by the operating system.</param>
    public readonly record struct Entry(int Id, string Name);

    /// <summary>
    /// Describes every Godot process on the host other than the current one, or returns <c>null</c> when there is
    /// none.
    /// </summary>
    /// <returns>A human-readable list such as <c>godot-mono (pid 4242)</c>, or <c>null</c>.</returns>
    public static string? DescribeOthers() => DescribeOthers(Snapshot(), Environment.ProcessId);

    /// <summary>
    /// Describes every Godot process in <paramref name="processes" /> except <paramref name="currentProcessId" />, or
    /// returns <c>null</c> when there is none. A process counts as Godot when its name contains <c>godot</c>, ignoring
    /// case, which covers <c>godot</c>, <c>godot-mono</c> and versioned release binaries.
    /// </summary>
    /// <param name="processes">The processes to inspect.</param>
    /// <param name="currentProcessId">The calling process, which is never reported.</param>
    /// <returns>A human-readable list ordered by process ID, or <c>null</c>.</returns>
    public static string? DescribeOthers(IEnumerable<Entry> processes, int currentProcessId)
    {
        ArgumentNullException.ThrowIfNull(processes);
        string[] others =
        [
            .. processes
                .Where(process => process.Id != currentProcessId
                    && process.Name.Contains("godot", StringComparison.OrdinalIgnoreCase))
                .OrderBy(process => process.Id)
                .Select(process => $"{process.Name} (pid {process.Id})"),
        ];
        return others.Length == 0 ? null : string.Join(", ", others);
    }

    private static List<Entry> Snapshot()
    {
        List<Entry> entries = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    entries.Add(new Entry(process.Id, process.ProcessName));
                }
                catch (InvalidOperationException)
                {
                    // The process exited between enumeration and inspection.
                }
            }
        }

        return entries;
    }
}
