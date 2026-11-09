using Godot;

// Exported setters emit Changed so the editor preview of SurfaceMarkings re-bakes on
// inspector edits; each needs a manual backing field.
#pragma warning disable IDE0032 // Use auto property

namespace AlleyCat.Markings;

/// <summary>
/// One authored marking: an art image placed at a rectangle in the target surface's UV space,
/// optionally supplying albedo, normal, and roughness overlays. Small art pieces (single
/// letters, symbols) are composed into bigger markings through per-marking placement; there is
/// no text rendering at runtime.
/// </summary>
/// <remarks>
/// Every exported setter emits <see cref="Resource.Changed"/> so the editor preview of
/// <see cref="SurfaceMarkings"/> can re-bake on inspector edits — the engine does not emit
/// that signal for script resources automatically.
/// </remarks>
[Tool]
[GlobalClass]
public sealed partial class SurfaceMarking : Resource
{
    private Vector2 _position = new(0.5f, 0.5f);
    private float _rotationDegrees;
    private Vector2 _scale = new(0.1f, 0.1f);
    private Texture2D? _albedoTexture;
    private Texture2D? _normalTexture;
    private Texture2D? _roughnessTexture;
    private Color _tint = Colors.White;
    private float _strength = 1f;
    private MarkingBlendMode _albedoBlend = MarkingBlendMode.Multiply;
    private MarkingBlendMode _roughnessBlend = MarkingBlendMode.Multiply;

    /// <summary>
    /// Gets or sets the centre of the stamp in the base texture's UV space.
    /// </summary>
    [ExportGroup("Placement")]
    [Export]
    public Vector2 Position
    {
        get => _position;
        set
        {
            _position = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the rotation of the stamp rectangle in degrees. Positive values rotate
    /// clockwise as seen in the texture image.
    /// </summary>
    [Export(PropertyHint.Range, "-360,360,0.1")]
    public float RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            _rotationDegrees = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the full extent of the stamp rectangle in UV units. The source image is
    /// mapped onto the whole rectangle; because UV space is not isometric, compensate aspect
    /// by eye against the editor preview.
    /// </summary>
    [Export]
    public Vector2 Scale
    {
        get => _scale;
        set
        {
            _scale = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the optional albedo ink image. RGB carry the ink colour and A carries
    /// coverage; the image is treated as sRGB.
    /// </summary>
    [ExportGroup("Art")]
    [Export]
    public Texture2D? AlbedoTexture
    {
        get => _albedoTexture;
        set
        {
            _albedoTexture = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the optional tangent-space normal overlay. RG carry tilt around the flat
    /// (0.5, 0.5); the packed AO in B of the base normal map is always preserved.
    /// </summary>
    [Export]
    public Texture2D? NormalTexture
    {
        get => _normalTexture;
        set
        {
            _normalTexture = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the optional roughness overlay. The green channel carries the scalar
    /// roughness (the skin shader reads roughness from <c>.g</c>); alpha carries coverage.
    /// </summary>
    [Export]
    public Texture2D? RoughnessTexture
    {
        get => _roughnessTexture;
        set
        {
            _roughnessTexture = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the linear-space tint multiplied into the albedo ink colour. White keeps
    /// the authored ink colour unchanged. Applies to the albedo channel only.
    /// </summary>
    [ExportGroup("Ink")]
    [Export]
    public Color Tint
    {
        get => _tint;
        set
        {
            _tint = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets how strongly the marking covers the surface, from none at zero to the full
    /// blend at one. Coverage multiplies the source alpha on every channel.
    /// </summary>
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float Strength
    {
        get => _strength;
        set
        {
            _strength = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the blend operation for the albedo channel. All albedo blending happens in
    /// linear space.
    /// </summary>
    [Export]
    public MarkingBlendMode AlbedoBlend
    {
        get => _albedoBlend;
        set
        {
            _albedoBlend = value;
            EmitChanged();
        }
    }

    /// <summary>
    /// Gets or sets the blend operation for the roughness channel's green scalar.
    /// </summary>
    [Export]
    public MarkingBlendMode RoughnessBlend
    {
        get => _roughnessBlend;
        set
        {
            _roughnessBlend = value;
            EmitChanged();
        }
    }
}
