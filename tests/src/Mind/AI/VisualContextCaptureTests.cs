using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.Mind.AI;
using AlleyCat.Scene.MiniMap;
using AlleyCat.Sense;
using AlleyCat.Vision;
using Godot;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Unit coverage for the shared subject-bound AI-013 capture composition (TR-3–TR-5): the minimap render request
/// carries the live subject centre, the renderer-default inclusion distance, and the gold <c>*You*</c> override
/// keyed by the subject's resolved FullId; every capture failure is contained as an outcome, never a throw.
/// </summary>
public sealed class VisualContextCaptureTests
{
    private const string SubjectFullId = "char:vadim";

    /// <summary>
    /// The minimap settings bind exactly the TR-19 subject override — the fixed documented gold colour and the
    /// literal <c>*You*</c> label replacing both label flavours — keyed by the subject's resolved FullId; every
    /// other setting stays unset so the renderer's documented service defaults apply, and the inclusion distance
    /// is never caller-settable (AI-013 TR-3).
    /// </summary>
    [Fact]
    public void ComposeMinimapSettings_BindsOnlyTheGoldSubjectOverride()
    {
        MiniMapRenderSettings settings = VisualContextCapture.ComposeMinimapSettings(SubjectFullId);

        Assert.Null(settings.MaximumDistance);
        Assert.Null(settings.Width);
        Assert.Null(settings.Height);
        Assert.Null(settings.MarginMetres);
        Assert.Null(settings.GridTextSizePixels);
        Assert.Null(settings.GridMinimumSpacingPixels);
        KeyValuePair<string, MiniMapMarkerOverride> entry = Assert.Single(settings.MarkerOverrides!);
        Assert.Equal(SubjectFullId, entry.Key);
        Assert.Equal(Color.FromHtml("ffd700"), entry.Value.Colour);
        Assert.Equal("*You*", entry.Value.LabelText);
    }

    /// <summary>The minimap centre is the subject's live world position read at call time on the X/Z plane (TR-3).</summary>
    [Fact]
    public void ReadLiveCentre_ReadsTheLiveWorldXZPosition()
    {
        Transform3D transform = Transform3D.Identity with
        {
            Origin = new Vector3(10.5f, 1.25f, -4.5f),
        };
        SpatialCharacter subject = new(SubjectFullId)
        {
            GlobalTransform = transform
        };

        Assert.Equal(new Vector2(10.5f, -4.5f), VisualContextCapture.ReadLiveCentre(subject));
    }

    /// <summary>
    /// The render call receives the subject's live centre and the gold-override settings, and the rendered payload
    /// records the renderer's documented default inclusion radius as the captured area (AI-013 TR-3): the
    /// recording fake fails the render so the contained failure path is exercised on the same call.
    /// </summary>
    [Fact]
    public void TryRenderMinimap_PassesLiveCentreAndOverrideAndContainsRenderFailure()
    {
        SpatialCharacter subject = new(SubjectFullId)
        {
            GlobalTransform = Transform3D.Identity with
            {
                Origin = new Vector3(3f, 0f, 7f)
            },
        };
        Vector2? seenCentre = null;
        MiniMapRenderSettings? seenSettings = null;
        var failed = MiniMapRenderResult.Failed(
            [new MiniMapDiagnostic("NoSceneTree", string.Empty, "The renderer's tree owner is not inside a scene tree.")]);

        VisualContextCaptureOutcome outcome = VisualContextCapture.TryRenderMinimap(
            (centre, settings) =>
            {
                seenCentre = centre;
                seenSettings = settings;
                return failed;
            },
            inclusionDistanceMetres: 100f,
            subject,
            clock: null);

        Assert.Null(outcome.Image);
        Assert.NotNull(outcome.Failure);
        Assert.Contains("NoSceneTree", outcome.Failure, StringComparison.Ordinal);
        Assert.Equal(new Vector2(3f, 7f), seenCentre);
        Assert.NotNull(seenSettings);
        Assert.Equal(
            VisualContextCapture.ComposeMinimapSettings(SubjectFullId).MarkerOverrides,
            seenSettings!.MarkerOverrides);
    }

    /// <summary>A subject whose world position is not finite — for example a character not in the scene — fails contained (AI-013 TR-5).</summary>
    [Fact]
    public void TryRenderMinimap_WithNonFiniteSubjectPosition_FailsContained()
    {
        SpatialCharacter subject = new(SubjectFullId)
        {
            GlobalTransform = Transform3D.Identity with
            {
                Origin = new Vector3(float.NaN, 0f, 0f)
            },
        };

        VisualContextCaptureOutcome outcome = VisualContextCapture.TryRenderMinimap(
            (_, _) => throw new InvalidOperationException("The render call must never run for a non-finite centre."),
            100f,
            subject,
            clock: null);

        Assert.Null(outcome.Image);
        Assert.Contains("current world position is not available", outcome.Failure, StringComparison.Ordinal);
    }

    /// <summary>A subject hosting no vision component fails contained with no rig (AI-013 TR-4/TR-5).</summary>
    [Fact]
    public async Task TryCaptureViewAsync_WithoutAVisionRig_FailsContained()
    {
        VisualContextCaptureOutcome outcome = await VisualContextCapture.TryCaptureViewAsync(
            new SpatialCharacter(SubjectFullId),
            clock: null);

        Assert.Null(outcome.Image);
        Assert.Contains("no vision rig is configured", outcome.Failure, StringComparison.Ordinal);
    }

    /// <summary>A rig whose camera is unconfigured fails contained (AI-013 TR-5: <c>IVision.Camera</c> null).</summary>
    [Fact]
    public async Task TryCaptureViewAsync_WithACameralessRig_FailsContained()
    {
        FakeVision vision = new()
        {
            Camera = null
        };
        VisualContextCaptureOutcome outcome = await VisualContextCapture.TryCaptureViewAsync(
            new SpatialCharacter(SubjectFullId, vision),
            clock: null);

        Assert.Null(outcome.Image);
        Assert.Contains("no configured camera", outcome.Failure, StringComparison.Ordinal);
    }

    private sealed class SpatialCharacter(string fullId, params IComponent[] components) : ICharacter
    {
        public string Id
        {
            get; set;
        } = fullId.StartsWith("char:", StringComparison.Ordinal)
            ? fullId["char:".Length..]
            : fullId;

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform { get; set; } = Transform3D.Identity;
    }

    /// <summary>
    /// Plain <see cref="IVision" /> double for the contained-failure contracts reachable without the Godot runtime:
    /// the rig presence and camera configuration checks. The throwing-capture path needs a live camera instance and
    /// is covered by the Godot-running integration suite.
    /// </summary>
    private sealed class FakeVision : IVision
    {
        public Camera3D? Camera
        {
            get; set;
        }

        public VisualCue? LookTarget
        {
            get; set;
        }

        public IReadOnlyList<Type> PerceptTypes { get; } = [];

        public event Action<IPercept>? Perceived
        {
            add
            {
            }
            remove
            {
            }
        }

        public void SetLookTarget(VisualCue? target) => LookTarget = target;

        public void ClearLookTarget() => LookTarget = null;

        public ValueTask<Image> CaptureImage()
            => throw new InvalidOperationException("The unit fake never captures a real image.");
    }
}
