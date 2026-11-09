using Godot;

// Exported setters emit Changed so the editor preview of SurfaceMarkings re-bakes on
// inspector edits; each needs a manual backing field.
#pragma warning disable IDE0032 // Use auto property

namespace AlleyCat.Markings;

/// <summary>
/// The authoring root for surface markings: the exact base maps a target surface's material
/// already references, plus the ordered layers stamped into copies of those maps. The base
/// references must be the same resource instances the material's texture slots hold, because
/// the bake discovers slots by texture identity rather than by slot name.
/// </summary>
/// <remarks>
/// Every exported setter emits <see cref="Resource.Changed"/> so the editor preview of
/// <see cref="SurfaceMarkings"/> can re-bake on inspector edits — the engine does not emit
/// that signal for script resources automatically.
/// </remarks>
[Tool]
[GlobalClass]
public sealed partial class MarkingOverlay : Resource
{
    private Texture2D? _albedoBase;
    private Texture2D? _normalBase;
    private Texture2D? _roughnessBase;
    private MarkingLayer[] _layers = [];

    /// <summary>
    /// Gets or sets the albedo base map the markings stamp over. Must be the exact texture
    /// referenced by the target material's albedo slot(s). Required when any marking supplies
    /// an albedo overlay.
    /// </summary>
    [ExportGroup("Channel Bases")]
    [Export]
    public Texture2D? AlbedoBase
    {
        get => _albedoBase;
        set
        {
            _albedoBase = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the normal base map the markings stamp over. Must be the exact texture
    /// referenced by the target material's normal slot(s). Required when any marking supplies
    /// a normal overlay.
    /// </summary>
    [Export]
    public Texture2D? NormalBase
    {
        get => _normalBase;
        set
        {
            _normalBase = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the roughness base map the markings stamp over. Must be the exact texture
    /// referenced by the target material's roughness slot(s). Required when any marking
    /// supplies a roughness overlay.
    /// </summary>
    [Export]
    public Texture2D? RoughnessBase
    {
        get => _roughnessBase;
        set
        {
            _roughnessBase = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the marking layers, composited in order across all channels.
    /// </summary>
    [ExportGroup("Layers")]
    [Export]
    public MarkingLayer[] Layers
    {
        get => _layers;
        set
        {
            _layers = value;
            EmitChanged();
        }
    }
}
