using Godot;

// Exported setters emit Changed so the editor preview of SurfaceMarkings re-bakes on
// inspector edits; each needs a manual backing field.
#pragma warning disable IDE0032 // Use auto property

namespace AlleyCat.Markings;

/// <summary>
/// A named, toggleable group of markings — for example "Tattoos" versus "Dirt". Layers are
/// composited in overlay order across every channel they supply, so a later dirt layer reads
/// consistently over earlier tattoos in both albedo and roughness.
/// </summary>
/// <remarks>
/// Every exported setter emits <see cref="Resource.Changed"/> so the editor preview of
/// <see cref="SurfaceMarkings"/> can re-bake on inspector edits — the engine does not emit
/// that signal for script resources automatically.
/// </remarks>
[Tool]
[GlobalClass]
public sealed partial class MarkingLayer : Resource
{
    private string _name = string.Empty;
    private bool _enabled = true;
    private SurfaceMarking[] _markings = [];

    /// <summary>
    /// Gets or sets the layer's display name. Runtime mutation APIs address layers by this
    /// name, so sibling layers must use distinct, non-empty names when runtime mutation is
    /// required; names may be left empty for purely authored overlays.
    /// </summary>
    [Export]
    public string Name
    {
        get => _name;
        set
        {
            _name = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets whether the layer's markings are stamped. Disabled layers are skipped at
    /// bake time without any authoring change.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the layer's markings, stamped in order.
    /// </summary>
    [Export]
    public SurfaceMarking[] Markings
    {
        get => _markings;
        set
        {
            _markings = value;
            EmitChanged();
        }
    }
}
