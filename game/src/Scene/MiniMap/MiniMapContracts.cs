using System.Collections.Immutable;
using Godot;

namespace AlleyCat.Scene.MiniMap;

/// <summary>Render outcome; diagnostics never change a successful pair into a failure.</summary>
public enum MiniMapRenderStatus
{
    /// <summary>Composition succeeded and produced the SVG/image pair.</summary>
    Ready,

    /// <summary>Composition failed loudly; no SVG and no image are returned.</summary>
    Failed
}

/// <summary>Actionable render diagnostic naming the element it refers to.</summary>
/// <param name="Code">Stable diagnostic code.</param>
/// <param name="Reference">Node path or setting the diagnostic names.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record MiniMapDiagnostic(string Code, string Reference, string Message);

/// <summary>
/// Per-request restyling of the markers matching one resolved identity (SCN-002 TR-19). The restyling lives in the
/// request only — it never mutates scene state — and both members unset is a legal, documented inert no-op.
/// </summary>
/// <param name="Colour">
/// Optional replacement fill for every primitive of matching markers — shapes and label glyphs alike, so one knob
/// recolours the whole marker; <c>null</c> keeps authored fill colours.
/// </param>
/// <param name="LabelText">
/// Optional replacement text for every label primitive of matching markers regardless of flavour — flavour decides
/// the default text and the override wins; <c>null</c> keeps the flavour's resolved text. Empty string is legal and
/// renders no glyphs; glyph diagnostics apply to the overridden text.
/// </param>
public sealed record MiniMapMarkerOverride(Color? Colour = null, string? LabelText = null);

/// <summary>
/// Per-request render settings (SCN-002 TR-9). Every member is optional and overrides the documented service default
/// of the same name; the structure is fixed by the specification. The minimum world extent is deliberately absent —
/// it is a fixed service guard, not a per-request setting.
/// </summary>
/// <param name="MaximumDistance">Maximum X/Z-plane inclusion distance in metres from the requested centre; <c>float.PositiveInfinity</c> includes every marker.</param>
/// <param name="Width">Output image width in pixels.</param>
/// <param name="Height">Output image height in pixels.</param>
/// <param name="MarginMetres">Bounding margin added to the content bounds on every side, in world metres.</param>
/// <param name="GridTextSizePixels">Grid label glyph size in pixels.</param>
/// <param name="GridMinimumSpacingPixels">Minimum on-image spacing between adjacent grid lines in pixels.</param>
/// <param name="MarkerOverrides">
/// Per-request marker style overrides (TR-19) keyed by the exact resolved <c>FullId</c> string; <c>null</c> or an
/// empty collection means no overrides. A null key or a null entry fails the render loudly.
/// </param>
public sealed record MiniMapRenderSettings(
    float? MaximumDistance = null,
    int? Width = null,
    int? Height = null,
    float? MarginMetres = null,
    int? GridTextSizePixels = null,
    float? GridMinimumSpacingPixels = null,
    IReadOnlyDictionary<string, MiniMapMarkerOverride>? MarkerOverrides = null);

/// <summary>
/// Documented service defaults merged with per-request settings. Tuning values may change; the structure may not.
/// </summary>
/// <param name="MaximumDistance">Default inclusion distance: 100 metres.</param>
/// <param name="Width">Default output width: 1024 pixels.</param>
/// <param name="Height">Default output height: 1024 pixels.</param>
/// <param name="MarginMetres">Default bounding margin: 2 metres per side.</param>
/// <param name="GridTextSizePixels">Default grid label size: 16 pixels.</param>
/// <param name="GridMinimumSpacingPixels">Default minimum grid spacing: 64 pixels.</param>
public sealed record MiniMapRendererDefaults(
    float MaximumDistance = 100f,
    int Width = 1024,
    int Height = 1024,
    float MarginMetres = 2f,
    int GridTextSizePixels = 16,
    float GridMinimumSpacingPixels = 64f);

/// <summary>
/// Explicit-viewport render request: the same production composition with caller-driven viewport inputs, used by the
/// editor preview's pan and zoom. <paramref name="Centre" /> still drives distance filtering.
/// </summary>
/// <param name="Centre">Filtering centre in world X/Z metres.</param>
/// <param name="WorldViewport">World X/Z rectangle to map to the image.</param>
/// <param name="Settings">Optional per-request settings.</param>
public sealed record MiniMapViewportRequest(Vector2 Centre, Rect2 WorldViewport, MiniMapRenderSettings? Settings = null);

/// <summary>
/// One render outcome (SCN-002 TR-13): the self-contained SVG text and the in-process rasterised image at exactly the
/// requested dimensions, plus the viewport the image actually shows. A failed render carries neither SVG nor image.
/// </summary>
/// <param name="Status">Ready or Failed.</param>
/// <param name="Svg">Self-contained SVG text on success; <c>null</c> on failure.</param>
/// <param name="Image">Rasterised image on success; <c>null</c> on failure.</param>
/// <param name="Viewport">World X/Z viewport the image shows.</param>
/// <param name="Scale">Uniform pixels-per-metre scale of this render.</param>
/// <param name="GridStep">Adaptive grid step in metres actually used.</param>
/// <param name="Diagnostics">Deterministically ordered diagnostics; successful renders may carry non-fatal ones.</param>
public sealed record MiniMapRenderResult(
    MiniMapRenderStatus Status,
    string? Svg,
    Image? Image,
    Rect2 Viewport,
    float Scale,
    float GridStep,
    ImmutableArray<MiniMapDiagnostic> Diagnostics)
{
    /// <summary>Constructs a failure carrying only diagnostics; never a stale or partial image.</summary>
    /// <param name="diagnostics">Diagnostics describing the failure.</param>
    /// <returns>Failed result.</returns>
    public static MiniMapRenderResult Failed(ImmutableArray<MiniMapDiagnostic> diagnostics) =>
        new(MiniMapRenderStatus.Failed, null, null, default, 0, 0, diagnostics);
}

/// <summary>
/// Authoring-time failure that aborts the whole render loudly (SCN-002 TR-16): degenerate or non-finite authored
/// sizes, non-finite transforms, broken or duplicate primitive references. Carries the marker/primitive reference the
/// diagnostic must name.
/// </summary>
internal sealed class MiniMapRenderException(string code, string reference, string message) : InvalidOperationException(message)
{
    internal string Code => code;

    internal string Reference => reference;
}
