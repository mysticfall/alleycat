using System.Globalization;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Kind of generated visual context an agent tool can return (AI-013 TR-3/TR-4).
/// </summary>
public enum VisualContextImageKind
{
    /// <summary>A top-down spatial minimap render, encoded as PNG.</summary>
    Minimap,

    /// <summary>A first-person view capture from the subject's own vision rig, encoded as JPEG.</summary>
    View,
}

/// <summary>
/// Typed immutable image payload a tool result can carry (AI-013 TR-5/TR-9): the encoded image bytes with their
/// fixed per-kind media type, plus the capture metadata — area or centre where meaningful and the game time of
/// capture — reused by the ephemeral-lifetime placeholder and the transcript image files.
/// </summary>
/// <remarks>
/// <para>
/// Deriving from <see cref="DataContent" /> makes the payload directly model-visible as an image part wherever a
/// data image can travel: both shipped OpenAI protocol adapters map data image parts in user messages onto their
/// wire image shapes, so the runtime attaches this content inside the tool exchange's presentation message
/// (AI-013 TR-10). The transcript recorder and formatter consume the same instance, keeping the transcript free of
/// Godot types (AI-011 TR-5).
/// </para>
/// <para>
/// The media type is fixed by the kind — minimap PNG, view JPEG (AI-011 TR-26) — and the payload is immutable
/// after construction.
/// </para>
/// <para>
/// Primary-constructor parameters: <paramref name="kind" /> selects the visual-context kind and fixes the media
/// type; <paramref name="data" /> holds the encoded image bytes — PNG for a minimap, JPEG for a view capture — and
/// must be non-empty; <paramref name="centreX" />/<paramref name="centreY" /> give the world-space capture centre
/// and <paramref name="radiusMetres" /> the captured area's inclusion radius where meaningful for the kind;
/// <paramref name="gameTimeSeconds" /> is the game clock time of the capture.
/// </para>
/// </remarks>
public sealed class VisualContextImage(
    VisualContextImageKind kind,
    ReadOnlyMemory<byte> data,
    double? centreX = null,
    double? centreY = null,
    double? radiusMetres = null,
    double gameTimeSeconds = 0)
    : DataContent(
        data.IsEmpty ? throw new ArgumentException("Tool-result image data cannot be empty.", nameof(data)) : data,
        DescribeMediaType(kind))
{
    /// <summary>Which kind of visual context this image is.</summary>
    public VisualContextImageKind Kind
    {
        get;
    } = kind;

    /// <summary>World-space X of the capture's centre, or null when not meaningful for this kind.</summary>
    public double? CentreX
    {
        get;
    } = centreX;

    /// <summary>World-space Y of the capture's centre, or null when not meaningful for this kind.</summary>
    public double? CentreY
    {
        get;
    } = centreY;

    /// <summary>Inclusion radius of the captured area in metres, or null when not meaningful for this kind.</summary>
    public double? RadiusMetres
    {
        get;
    } = radiusMetres;

    /// <summary>Game clock time of the capture, in seconds.</summary>
    public double GameTimeSeconds
    {
        get;
    } = gameTimeSeconds;

    /// <summary>
    /// Creates a minimap capture payload: encoded PNG bytes plus the captured area's centre and inclusion radius
    /// where meaningful, and the game time of capture (AI-013 TR-3).
    /// </summary>
    public static VisualContextImage Minimap(
        byte[] png,
        double? centreX = null,
        double? centreY = null,
        double? radiusMetres = null,
        double gameTimeSeconds = 0)
        => new(VisualContextImageKind.Minimap, png, centreX, centreY, radiusMetres, gameTimeSeconds);

    /// <summary>
    /// Creates a view-capture payload: encoded JPEG bytes from the subject's own vision rig and the game time of
    /// capture (AI-013 TR-4). A view has no meaningful area or centre.
    /// </summary>
    public static VisualContextImage View(byte[] jpeg, double gameTimeSeconds = 0)
        => new(VisualContextImageKind.View, jpeg, gameTimeSeconds: gameTimeSeconds);

    /// <summary>The media type fixed by the image kind: minimap PNG, view JPEG (AI-011 TR-26).</summary>
    public static string DescribeMediaType(VisualContextImageKind kind)
        => kind switch
        {
            VisualContextImageKind.Minimap => "image/png",
            VisualContextImageKind.View => "image/jpeg",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>The lowercase transcript and file-name label of one image kind.</summary>
    public static string DescribeKind(VisualContextImageKind kind)
        => kind switch
        {
            VisualContextImageKind.Minimap => "minimap",
            VisualContextImageKind.View => "view",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Describes the capture for model-facing text: the image kind, its area or centre where meaningful, and the
    /// game time of capture (AI-013 TR-9).
    /// </summary>
    public string DescribeCapture()
    {
        string kindDescription = Kind switch
        {
            VisualContextImageKind.Minimap => "minimap image",
            VisualContextImageKind.View => "view capture through your own eyes",
            _ => "image",
        };

        string area = CentreX is { } centreX && CentreY is { } centreY
            ? $" centred at ({Format(centreX)}, {Format(centreY)})"
            : string.Empty;
        if (RadiusMetres is { } radius)
        {
            area += $" covering a {Format(radius)} m radius";
        }

        return $"{kindDescription}{area}, captured at game time {Format(GameTimeSeconds)} s";
    }

    /// <summary>
    /// The textual placeholder that replaces the pixels when the ephemeral image lifetime ends (AI-013 TR-9): it
    /// records the image kind, its area or centre, and the game time of capture, and nudges the model to request a
    /// fresh capture for current visuals.
    /// </summary>
    public string ToDisposalPlaceholder()
        => $"[Image no longer available — {DescribeCapture()}. Capture again to see current visuals.]";

    private static string Format(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// Internal delivery envelope returned by the runtime-bound agent tool wrapper when a tool result carries a
/// generated image (AI-013 TR-5/TR-9): the model-facing textual result travels unchanged while the runner also
/// receives the image payload, which it presents within the tool exchange and disposes at the next fresh
/// scheduling turn.
/// </summary>
/// <param name="Message">The textual tool result delivered as the function result content; may be null.</param>
/// <param name="Image">The image generated by the tool call.</param>
internal sealed record VisualContextDelivery(string? Message, VisualContextImage Image);

