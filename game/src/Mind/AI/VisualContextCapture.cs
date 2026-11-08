using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Core.Time;
using AlleyCat.Scene.MiniMap;
using AlleyCat.Vision;
using Godot;

namespace AlleyCat.Mind.AI;

/// <summary>
/// One visual-context capture outcome (AI-013 TR-3–TR-5): either the generated image payload, or a contained
/// failure description the caller presents as an error tool result or a logged omission — never both, never
/// neither.
/// </summary>
internal readonly record struct VisualContextCaptureOutcome(VisualContextImage? Image, string? Failure)
{
    /// <summary>Creates a failed outcome carrying its contained failure description.</summary>
    public static VisualContextCaptureOutcome Failed(string failure) => new(null, failure);
}

/// <summary>
/// Shared subject-bound capture routines of AI-013 visual context, used by both the <c>tools</c>-mode tools and
/// the <c>auto</c>-mode request-context captures under identical contracts (AI-013 TR-3/TR-4): the minimap is
/// always centred on the subject's live world position with the renderer's documented default inclusion distance
/// and the fixed gold <c>*You*</c> marker override keyed by the subject's resolved <see cref="IIdentifiable.FullId" />
/// (SCN-002 TR-19); the view is exactly one fresh <see cref="IVision.CaptureImage" /> frame from the subject's own
/// rig. Every failure is contained here (AI-013 TR-5): the outcome describes it and nothing throws except
/// cancellation.
/// </summary>
internal static class VisualContextCapture
{
    /// <summary>
    /// The fixed documented gold highlight colour of the subject's own minimap marker (AI-013 TR-3): CSS gold
    /// <c>#ffd700</c>, recolouring every primitive of the subject's marker — shapes and label glyphs alike.
    /// </summary>
    internal const string SubjectHighlightGoldHtml = "ffd700";

    /// <summary>
    /// The fixed label text replacing both label flavours of the subject's own minimap marker (AI-013 TR-3): the
    /// literal string <c>*You*</c>, asterisks included.
    /// </summary>
    internal const string SubjectHighlightLabel = "*You*";

    /// <summary>The gold colour value of <see cref="SubjectHighlightGoldHtml" />.</summary>
    internal static readonly Color SubjectHighlightColour = Color.FromHtml(SubjectHighlightGoldHtml);

    /// <summary>
    /// Composes the minimap render settings of one subject (AI-013 TR-3): the only per-request input is the
    /// TR-19 marker override keyed by the subject's resolved <see cref="IIdentifiable.FullId" /> with the fixed
    /// documented gold colour and the literal <c>*You*</c> label text; every other setting stays unset so the
    /// renderer's documented service defaults apply — the inclusion distance is never caller-settable.
    /// </summary>
    internal static MiniMapRenderSettings ComposeMinimapSettings(string subjectFullId)
        => new(
            MarkerOverrides: new Dictionary<string, MiniMapMarkerOverride>
            {
                [subjectFullId] = new(SubjectHighlightColour, SubjectHighlightLabel),
            });

    /// <summary>
    /// Reads the subject's live world position at call time as the minimap centre's X/Z plane coordinates
    /// (AI-013 TR-3): the transform is read fresh, never cached, so the map follows the subject's current
    /// position at invocation.
    /// </summary>
    internal static Vector2 ReadLiveCentre(ISpatial subject)
    {
        ArgumentNullException.ThrowIfNull(subject);
        return new Vector2(subject.GlobalTransform.Origin.X, subject.GlobalTransform.Origin.Z);
    }

    /// <summary>
    /// Renders the subject-centred minimap through a concrete renderer (AI-013 TR-3): the centre is the subject's
    /// live world position, the inclusion distance is the renderer's documented default, and the request carries
    /// the gold <c>*You*</c> override. Failures — including renderer failures and a subject outside the scene —
    /// are contained (AI-013 TR-5).
    /// </summary>
    internal static VisualContextCaptureOutcome TryRenderMinimap(
        MiniMapRenderer renderer,
        ICharacter subject,
        IGameClock? clock)
        => TryRenderMinimap(
            renderer.Render,
            renderer.Defaults.MaximumDistance,
            subject,
            clock);

    /// <summary>
    /// Core minimap capture over an injectable render call: unit coverage drives the failure contracts through
    /// synthetic render outcomes, while production and integration paths exercise the real renderer. The PNG
    /// payload records the live centre and the renderer's default inclusion radius as the captured area, with the
    /// game time of the capture (AI-013 TR-9's placeholder record).
    /// </summary>
    internal static VisualContextCaptureOutcome TryRenderMinimap(
        Func<Vector2, MiniMapRenderSettings, MiniMapRenderResult> render,
        float inclusionDistanceMetres,
        ICharacter subject,
        IGameClock? clock)
    {
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(subject);

        try
        {
            Vector2 centre = ReadLiveCentre(subject);
            if (!centre.IsFinite())
            {
                return VisualContextCaptureOutcome.Failed(
                    "the subject's current world position is not available");
            }

            MiniMapRenderResult result = render(centre, ComposeMinimapSettings(subject.FullId));
            if (result.Status != MiniMapRenderStatus.Ready || result.Image is null)
            {
                return VisualContextCaptureOutcome.Failed(DescribeRenderFailure(result));
            }

            byte[] png = result.Image.SavePngToBuffer();
            return png.Length == 0
                ? VisualContextCaptureOutcome.Failed("the rendered minimap image could not be encoded")
                : new VisualContextCaptureOutcome(
                    VisualContextImage.Minimap(
                        png,
                        centre.X,
                        centre.Y,
                        radiusMetres: inclusionDistanceMetres,
                        gameTimeSeconds: clock?.NowSeconds ?? 0d),
                    null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return VisualContextCaptureOutcome.Failed(exception.Message);
        }
    }

    /// <summary>
    /// Captures exactly one fresh view frame from the subject's own vision rig (AI-013 TR-4): the rig resolves
    /// through <see cref="IHasVision" />, a missing rig or unconfigured camera is a contained failure, and the
    /// single <see cref="IVision.CaptureImage" /> call honours VISION-001's bounded, side-effect-free one-shot
    /// contract. A throwing capture is contained (AI-013 TR-5).
    /// </summary>
    internal static async Task<VisualContextCaptureOutcome> TryCaptureViewAsync(
        ICharacter subject,
        IGameClock? clock,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!subject.TryGetVision(out IVision? vision) || vision is null)
            {
                return VisualContextCaptureOutcome.Failed("no vision rig is configured");
            }

            if (vision.Camera is null)
            {
                return VisualContextCaptureOutcome.Failed("the vision rig has no configured camera");
            }

            Image captured = await vision.CaptureImage();
            cancellationToken.ThrowIfCancellationRequested();
            byte[] jpeg = captured.SaveJpgToBuffer();
            return jpeg.Length == 0
                ? VisualContextCaptureOutcome.Failed("the captured view image could not be encoded")
                : new VisualContextCaptureOutcome(
                    VisualContextImage.View(jpeg, clock?.NowSeconds ?? 0d),
                    null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return VisualContextCaptureOutcome.Failed(exception.Message);
        }
    }

    private static string DescribeRenderFailure(MiniMapRenderResult result)
        => result.Diagnostics.Length == 0
            ? "the minimap render failed"
            : string.Join("; ", result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
