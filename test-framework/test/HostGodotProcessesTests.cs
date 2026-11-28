using Xunit;

namespace AlleyCat.TestFramework.Tests;

/// <summary>
/// Deterministic coverage of the TR-35 host check: which processes count as other Godot processes and how the found
/// processes are named in the skip reason.
/// </summary>
public sealed class HostGodotProcessesTests
{
    private const int CurrentProcess = 100;

    /// <summary>A clean host, holding only the calling Godot process and unrelated processes, reports nothing.</summary>
    [Fact]
    public void DescribeOthers_ReturnsNull_WhenOnlyTheCurrentGodotProcessIsRunning()
    {
        HostGodotProcesses.Entry[] processes =
        [
            new(CurrentProcess, "godot-mono"),
            new(7, "dotnet"),
            new(8, "Xorg"),
        ];

        Assert.Null(HostGodotProcesses.DescribeOthers(processes, CurrentProcess));
    }

    /// <summary>
    /// Every other Godot process is named with its process ID, in ID order, whatever the binary's spelling: the
    /// mono build, a versioned release binary truncated by the kernel, or a plain editor.
    /// </summary>
    [Fact]
    public void DescribeOthers_NamesEveryOtherGodotProcess_IgnoringCase()
    {
        HostGodotProcesses.Entry[] processes =
        [
            new(CurrentProcess, "godot-mono"),
            new(4242, "godot-mono"),
            new(31, "Godot_v4.5-stab"),
            new(9, "dotnet"),
            new(512, "godot"),
        ];

        Assert.Equal(
            "Godot_v4.5-stab (pid 31), godot (pid 512), godot-mono (pid 4242)",
            HostGodotProcesses.DescribeOthers(processes, CurrentProcess));
    }
}
