using Godot;

namespace AlleyCat.IntegrationTests.Probe;

/// <summary>
/// Tiny probe node used to prove dynamic assembly loading inside Godot.
/// </summary>
public partial class DynamicLoadProbeNode : Node
{
    /// <summary>
    /// Unique output marker emitted when the probe node reaches ready.
    /// </summary>
    public const string ReadyMarker = "ALLEYCAT_DYNAMIC_PROBE_READY";

    /// <summary>
    /// Gets whether <see cref="_Ready"/> has run for this process.
    /// </summary>
    public static bool ReadyRan
    {
        get; private set;
    }

    /// <summary>
    /// Marks the probe as ready and emits a marker to standard output.
    /// </summary>
    public override void _Ready()
    {
        ReadyRan = true;
        GD.Print(ReadyMarker);
    }
}
