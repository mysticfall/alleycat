using Godot;

namespace AlleyCat.Tests.Testing.Installer;

/// <summary>
/// Focused scene fixture exposing generic exported values and references for installer reconciliation tests.
/// </summary>
[GlobalClass]
public partial class InstallerOverrideProbe : Node3D
{
    /// <summary>
    /// Gets or sets the value that a derived target scene explicitly overrides.
    /// </summary>
    [Export]
    public string PreservedValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value inherited from the base target scene without a local override.
    /// </summary>
    [Export]
    public string RefreshValue { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a template-local node reference used to verify stale reference replacement.
    /// </summary>
    [Export]
    public Node? Target
    {
        get; set;
    }
}
