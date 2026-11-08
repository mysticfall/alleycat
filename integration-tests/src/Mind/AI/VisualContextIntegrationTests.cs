using System.Globalization;
using System.Text.Json;
using AlleyCat.Character;
using AlleyCat.Core;
using AlleyCat.IntegrationTests.Scene;
using AlleyCat.IntegrationTests.Support;
using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Prompting;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using AlleyCat.Scene;
using AlleyCat.Scene.MiniMap;
using AlleyCat.Sense;
using AlleyCat.Vision;
using Godot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AlleyCat.IntegrationTests.Support.TestUtils;
using CharacterHub = AlleyCat.Character.Character;

namespace AlleyCat.IntegrationTests.Mind.AI;

/// <summary>
/// Godot-runtime coverage for the behavioural half of AI-013: the mode-dependent inventory of the two
/// zero-argument visual tools with unknown-tool rejection in the other modes, the minimap tool's real render path
/// through the DI renderer with the gold <c>*You*</c> subject override, the view tool's fresh capture and
/// contained failure results, and the auto-mode per-logical-request visual-context message — placement, reuse
/// within its request cycle, rebuild at the next logical request, and failure omission (TR-2–TR-8, TR-12).
/// Real captures and renders need the actual renderer, so the class runs windowed.
/// </summary>
public sealed class VisualContextIntegrationTests
{
    private const string SubjectId = "vadim";

    private static readonly Vector3 _subjectPosition = new(10f, 0f, -5f);

    /// <summary>
    /// The <c>tools</c> default composes both zero-argument visual tools beside the production inventory, with the
    /// shipped descriptions and empty model-facing schemas; <c>auto</c> and <c>off</c> compose neither
    /// (AI-013 TR-2, TR-7, TR-12).
    /// </summary>
    [Theory]
    [InlineData(VisualContextMode.Tools)]
    [InlineData(VisualContextMode.Auto)]
    [InlineData(VisualContextMode.Off)]
    public async Task Inventory_ComposesBothVisualToolsOnlyInToolsMode(VisualContextMode mode)
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(mode);
        try
        {
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 1);

            IReadOnlyDictionary<string, CapturedTool> tools = fixture.Provider.Captures[0].Tools;
            Assert.Equal(
                mode == VisualContextMode.Tools,
                tools.ContainsKey(MinimapTool.ProductionToolName));
            Assert.Equal(
                mode == VisualContextMode.Tools,
                tools.ContainsKey(CaptureViewTool.ProductionToolName));
            if (mode != VisualContextMode.Tools)
            {
                return;
            }

            foreach (string name in (string[])
                [MinimapTool.ProductionToolName, CaptureViewTool.ProductionToolName])
            {
                CapturedTool tool = tools[name];
                Assert.Equal(
                    name == MinimapTool.ProductionToolName
                        ? MinimapTool.ProductionToolDescription
                        : CaptureViewTool.ProductionToolDescription,
                    tool.Description);
                Assert.Empty(tool.ParameterProperties);
                Assert.Empty(tool.RequiredParameters);
            }

            // The visual tools sit beside the production inventory without displacing it (AI-002 TR-13 as amended).
            foreach (string productionName in (string[])["speak", "wait", "read_lore", "unwatch"])
            {
                Assert.True(tools.ContainsKey(productionName), $"Expected production tool '{productionName}'.");
            }
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// In <c>auto</c> mode a model attempt to call <c>minimap</c> is rejected as an unknown tool by the existing
    /// validation — the invalid response is discarded, the call never executes, recovery issues a fresh request,
    /// and the session continues (AI-013 TR-7).
    /// </summary>
    [Fact]
    public async Task AutoMode_ModelAttemptToCallTheMinimapTool_IsRejectedAsAnUnknownTool()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Auto, enableTranscriptLogging: true);
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(fixture.TranscriptRoot);
            fixture.Provider.EnqueueCall(MinimapTool.ProductionToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            // The rejected call never executed: no tool result for it exists in any request, and recovery
            // proceeded to a follow-up request.
            foreach (CapturedRequest request in fixture.Provider.Captures)
            {
                Assert.DoesNotContain(
                    request.Messages.SelectMany(static message => message.Contents)
                        .OfType<FunctionResultContent>(),
                    static result => result.Result?.ToString()?.Contains("Minimap", StringComparison.Ordinal) == true);
            }

            string firstTurnPage = File.ReadAllText(Path.Combine(
                fixture.CharacterTranscriptDirectory,
                MindSessionTranscriptRecorder.CreateTurnFileName(1)));
            Assert.Contains("unknown tool 'minimap'", firstTurnPage, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// A model call to <c>minimap</c> renders through the DI renderer with the subject's live world position as
    /// the centre, the documented default framing, and the gold <c>*You*</c> override keyed by the subject's
    /// FullId: the result image is a real minimap PNG whose subject marker renders in the fixed gold near the map
    /// centre. The produced PNG is saved under <c>res://temp/visual-context/</c> as the review evidence artefact
    /// (AI-013 TR-3).
    /// </summary>
    [Fact]
    public async Task MinimapTool_ThroughTheRealRenderer_DeliversASubjectCentredGoldHighlightedMap()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Tools);
        try
        {
            fixture.Provider.EnqueueCall(MinimapTool.ProductionToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            // The settled exchange presents the image within the exchange, immediately after the tool result
            // (AI-013 TR-5/TR-10), and the textual result channel carries the capture description.
            IReadOnlyList<ChatMessage> replay = fixture.Provider.Captures[1].Messages;
            FunctionCallContent call = Assert.Single(
                replay.SelectMany(static message => message.Contents).OfType<FunctionCallContent>(),
                candidate => candidate.Name == MinimapTool.ProductionToolName);
            FunctionResultContent result = Assert.Single(
                replay.SelectMany(static message => message.Contents).OfType<FunctionResultContent>(),
                candidate => candidate.CallId == call.CallId);
            Assert.Contains("centred at (10, -5)", result.Result?.ToString(), StringComparison.Ordinal);
            int toolResultIndex = Assert.Single(
                Enumerable.Range(0, replay.Count),
                index => replay[index].Role == ChatRole.Tool
                    && replay[index].Contents.OfType<FunctionResultContent>()
                        .Any(toolResult => toolResult.CallId == call.CallId));
            ChatMessage presentation = replay[toolResultIndex + 1];
            VisualContextImage minimap = Assert.IsType<VisualContextImage>(
                Assert.Single(presentation.Contents.OfType<VisualContextImage>()));
            Assert.Equal(VisualContextImageKind.Minimap, minimap.Kind);
            Assert.Equal("image/png", minimap.MediaType);
            Assert.Equal(10d, minimap.CentreX);
            Assert.Equal(-5d, minimap.CentreY);
            // The renderer's documented service default inclusion distance is recorded as the captured radius.
            MiniMapRenderer renderer = Game.Instance.GetRequiredService<MiniMapRenderer>();
            Assert.Equal(renderer.Defaults.MaximumDistance, minimap.RadiusMetres);

            byte[] png = minimap.Data.ToArray();
            AssertPng(png);
            Image decoded = new();
            Assert.Equal(Error.Ok, decoded.LoadPngFromBuffer(png));
            Assert.Equal(renderer.Defaults.Width, decoded.GetWidth());
            Assert.Equal(renderer.Defaults.Height, decoded.GetHeight());
            AssertGoldSubjectHighlightNearTheCentre(decoded);

            // The review evidence artefact: the exact tool-generated PNG through the real render path.
            string evidenceDirectory = ProjectSettings.GlobalizePath("res://temp/visual-context");
            _ = DirAccess.MakeDirRecursiveAbsolute(evidenceDirectory);
            File.WriteAllBytes(Path.Combine(evidenceDirectory, "minimap-tool-evidence.png"), png);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// An unavailable renderer service turns the minimap tool call into a contained error result: a textual
    /// failure at the exchange position, no image content anywhere, and the session continues normally
    /// (AI-013 TR-5).
    /// </summary>
    [Fact]
    public async Task MinimapTool_WithoutARendererService_ReturnsAContainedErrorResult()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Tools, rendererAvailable: false);
        try
        {
            fixture.Provider.EnqueueCall(MinimapTool.ProductionToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            IReadOnlyList<ChatMessage> replay = fixture.Provider.Captures[1].Messages;
            FunctionResultContent result = Assert.Single(
                replay.SelectMany(static message => message.Contents).OfType<FunctionResultContent>());
            Assert.Contains("Minimap unavailable", result.Result?.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "renderer service is not available",
                result.Result?.ToString(),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                replay.SelectMany(static message => message.Contents),
                static content => content is VisualContextImage);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// A model call to <c>capture_view</c> takes exactly one fresh frame from the reference NPC rig's own vision
    /// camera and returns it as a JPEG view capture (AI-013 TR-4).
    /// </summary>
    [Fact]
    public async Task CaptureViewTool_FromTheReferenceNpcRig_DeliversAFreshJpegView()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Tools, useReferenceNpcRig: true);
        try
        {
            fixture.Provider.EnqueueCall(CaptureViewTool.ProductionToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            IReadOnlyList<ChatMessage> replay = fixture.Provider.Captures[1].Messages;
            VisualContextImage view = Assert.IsType<VisualContextImage>(Assert.Single(
                replay.SelectMany(static message => message.Contents).OfType<VisualContextImage>(),
                static candidate => candidate.Kind == VisualContextImageKind.View));
            Assert.Equal(VisualContextImageKind.View, view.Kind);
            Assert.Equal("image/jpeg", view.MediaType);
            byte[] jpeg = view.Data.ToArray();
            Assert.True(
                jpeg.Length > 3 && jpeg[0] == 0xFF && jpeg[1] == 0xD8 && jpeg[2] == 0xFF,
                "The view tool result must carry a real JPEG payload.");

            string evidenceDirectory = ProjectSettings.GlobalizePath("res://temp/visual-context");
            _ = DirAccess.MakeDirRecursiveAbsolute(evidenceDirectory);
            File.WriteAllBytes(Path.Combine(evidenceDirectory, "capture-view-tool-evidence.jpg"), jpeg);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// A throwing capture is contained: the tool result is a textual error at the exchange position, no image
    /// content reaches any request, and the session continues (AI-013 TR-5).
    /// </summary>
    [Fact]
    public async Task CaptureViewTool_WhenTheCaptureThrows_ReturnsAContainedErrorResult()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Tools, visionOverride: new ThrowingVision());
        try
        {
            fixture.Provider.EnqueueCall(CaptureViewTool.ProductionToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            IReadOnlyList<ChatMessage> replay = fixture.Provider.Captures[1].Messages;
            FunctionResultContent result = Assert.Single(
                replay.SelectMany(static message => message.Contents).OfType<FunctionResultContent>());
            Assert.Contains("View capture unavailable", result.Result?.ToString(), StringComparison.Ordinal);
            Assert.Contains("invalidated mid-capture", result.Result?.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                replay.SelectMany(static message => message.Contents),
                static content => content is VisualContextImage);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// In <c>auto</c> mode every logical request carries exactly one visual-context user message immediately
    /// before its fresh scene-status message, with both captures — minimap PNG then view JPEG — labelled by kind
    /// and never duplicated elsewhere in the request (AI-013 TR-8).
    /// </summary>
    [Fact]
    public async Task AutoMode_EveryLogicalRequestCarriesExactlyOneVisualContextMessageBeforeTheSceneStatus()
    {
        SceneTree sceneTree = GetSceneTree();
        CountingVision vision = new();
        SessionFixture fixture = new(VisualContextMode.Auto, visionOverride: vision);
        try
        {
            fixture.Provider.EnqueueCall(DummyToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            foreach (CapturedRequest request in fixture.Provider.Captures)
            {
                IReadOnlyList<ChatMessage> messages = request.Messages;
                ChatMessage sceneStatus = messages[^1];
                Assert.Equal(ChatRole.User, sceneStatus.Role);
                Assert.Contains("Fresh scene status.", sceneStatus.Text, StringComparison.Ordinal);
                ChatMessage visualContext = messages[^2];
                Assert.Equal(ChatRole.User, visualContext.Role);
                Assert.StartsWith("Current visual context:", visualContext.Text, StringComparison.Ordinal);
                Assert.Contains("minimap image centred at (10, -5)", visualContext.Text, StringComparison.Ordinal);
                Assert.Contains("view capture through your own eyes", visualContext.Text, StringComparison.Ordinal);
                VisualContextImage[] images =
                    [.. visualContext.Contents.OfType<VisualContextImage>()];
                Assert.Equal(VisualContextImageKind.Minimap, images[0].Kind);
                Assert.Equal("image/png", images[0].MediaType);
                Assert.Equal(VisualContextImageKind.View, images[1].Kind);
                Assert.Equal("image/jpeg", images[1].MediaType);
                // Exactly one visual-context message per request, and the images appear nowhere else (TR-8).
                Assert.Equal(2, images.Length);
                _ = Assert.Single(
                    messages,
                    static message => message.Contents.Any(static content => content is VisualContextImage));
            }

            Assert.Equal(2, vision.CaptureCount);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// The visual-context message persists through its request cycle's recovery rematerialisation — the recovery
    /// request reuses the same captures, so the view capture count does not grow — and is rebuilt at the next
    /// logical request after an accepted response, which captures afresh (AI-013 TR-8).
    /// </summary>
    [Fact]
    public async Task AutoMode_RecoveryRematerialisationReusesTheMessageAndTheNextLogicalRequestRebuilds()
    {
        SceneTree sceneTree = GetSceneTree();
        CountingVision vision = new();
        SessionFixture fixture = new(VisualContextMode.Auto, visionOverride: vision);
        try
        {
            fixture.Provider.EnqueueResponse(new ChatMessage(ChatRole.Assistant, "invalid prose"));
            fixture.Provider.EnqueueCall(DummyToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 3);

            byte[] initialView = ImagePartsOf(fixture.Provider.Captures[0])[^1].Data.ToArray();
            byte[] recoveryView = ImagePartsOf(fixture.Provider.Captures[1])[^1].Data.ToArray();
            byte[] rebuiltView = ImagePartsOf(fixture.Provider.Captures[2])[^1].Data.ToArray();

            // The invalid response was discarded and its recovery request rematerialised the same logical
            // request: the recovery request reuses the original captures — identical view bytes, and the two
            // requests together consumed exactly one view capture (AI-013 TR-8).
            Assert.Equal(initialView, recoveryView);
            // The accepted follow-up response completes the logical request; its next request rebuilds from a
            // fresh capture with different bytes — two view captures in total for the three requests
            // (AI-013 TR-8).
            Assert.Equal(2, vision.CaptureCount);
            Assert.NotEqual(initialView, rebuiltView);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// Each failed auto-mode capture is omitted with a warning while the turn proceeds: a character with no vision
    /// rig presents the minimap alone, and a cycle where nothing captured successfully presents no visual-context
    /// message at all (AI-013 TR-8).
    /// </summary>
    [Fact]
    public async Task AutoMode_FailedCapturesAreOmittedAndATotallyFailedCyclePresentsNoMessage()
    {
        SceneTree sceneTree = GetSceneTree();

        SessionFixture? minimapOnly = new(VisualContextMode.Auto);
        try
        {
            minimapOnly.EnqueueHoldForever();
            await minimapOnly.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => minimapOnly.Provider.Captures.Count >= 1);

            IReadOnlyList<ChatMessage> messages = minimapOnly.Provider.Captures[0].Messages;
            ChatMessage visualContext = messages[^2];
            VisualContextImage image = Assert.IsType<VisualContextImage>(
                Assert.Single(visualContext.Contents.OfType<VisualContextImage>()));
            Assert.Equal(VisualContextImageKind.Minimap, image.Kind);
            Assert.StartsWith("Current visual context: minimap image", visualContext.Text, StringComparison.Ordinal);
        }
        finally
        {
            await minimapOnly.CleanupAsync(sceneTree);
        }

        SessionFixture failedCycle = new(VisualContextMode.Auto, rendererAvailable: false);
        try
        {
            failedCycle.EnqueueHoldForever();
            await failedCycle.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => failedCycle.Provider.Captures.Count >= 1);

            IReadOnlyList<ChatMessage> messages = failedCycle.Provider.Captures[0].Messages;
            _ = Assert.Single(
                messages,
                static message => message.Text.Contains("Fresh scene status.", StringComparison.Ordinal));
            Assert.DoesNotContain(
                messages.SelectMany(static message => message.Contents),
                static content => content is VisualContextImage);
            Assert.DoesNotContain(
                messages,
                static message => message.Text.StartsWith("Current visual context:", StringComparison.Ordinal));
        }
        finally
        {
            await failedCycle.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// With transcript logging enabled, an auto-mode cycle's message-carried images are written exactly once as
    /// sibling PNG/JPEG files beside the generating turn page and relatively linked at the carrying message's
    /// position (AI-013 TR-8; AI-011 TR-26–TR-27).
    /// </summary>
    [Fact]
    public async Task AutoMode_WithTranscriptLogging_WritesMessageCarriedSiblingFilesBesideTheTurnPage()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Auto, visionOverride: new CountingVision(), enableTranscriptLogging: true);
        try
        {
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(fixture.TranscriptRoot);
            fixture.Provider.EnqueueCall(DummyToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            string characterDirectory = fixture.CharacterTranscriptDirectory;
            await WaitUntilAsync(
                sceneTree,
                () => File.Exists(Path.Combine(characterDirectory, "turn-0001-minimap-1.png"))
                    && File.Exists(Path.Combine(characterDirectory, "turn-0001-view-1.jpg")));

            Assert.Equal(
                [
                    "index.md",
                    "turn-0001-minimap-1.png",
                    "turn-0001-view-1.jpg",
                    "turn-0001.md",
                ],
                Directory.GetFiles(characterDirectory).Select(Path.GetFileName).Order());
            byte[] minimapPng = File.ReadAllBytes(Path.Combine(characterDirectory, "turn-0001-minimap-1.png"));
            AssertPng(minimapPng);
            string turnPage = File.ReadAllText(Path.Combine(characterDirectory, "turn-0001.md"));
            Assert.Contains("![minimap](turn-0001-minimap-1.png)", turnPage, StringComparison.Ordinal);
            Assert.Contains("![view](turn-0001-view-1.jpg)", turnPage, StringComparison.Ordinal);
            Assert.Contains("Current visual context:", turnPage, StringComparison.Ordinal);
            Assert.Contains(
                "- **minimap:** [turn-0001-minimap-1.png](turn-0001-minimap-1.png) —",
                turnPage,
                StringComparison.Ordinal);
            Assert.Contains(
                "- **view:** [turn-0001-view-1.jpg](turn-0001-view-1.jpg) —",
                turnPage,
                StringComparison.Ordinal);

            // The invocation index stays unchanged and carries no image links (AI-011 TR-27).
            string index = File.ReadAllText(Path.Combine(characterDirectory, "index.md"));
            Assert.DoesNotContain(".png)", index, StringComparison.Ordinal);
            Assert.DoesNotContain(".jpg)", index, StringComparison.Ordinal);
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    /// <summary>
    /// In <c>off</c> mode nothing visual exists at all: neither tool is offered, no capture or render is requested,
    /// and no request message carries image content (AI-013 TR-12).
    /// </summary>
    [Fact]
    public async Task OffMode_ComposesNoToolsAndAttachesNoImagesAnywhere()
    {
        SceneTree sceneTree = GetSceneTree();
        SessionFixture fixture = new(VisualContextMode.Off);
        try
        {
            fixture.Provider.EnqueueCall(DummyToolName);
            fixture.EnqueueHoldForever();
            await fixture.StartAsync(sceneTree);
            await WaitUntilAsync(sceneTree, () => fixture.Provider.Captures.Count >= 2);

            IReadOnlyDictionary<string, CapturedTool> tools = fixture.Provider.Captures[0].Tools;
            Assert.False(tools.ContainsKey(MinimapTool.ProductionToolName));
            Assert.False(tools.ContainsKey(CaptureViewTool.ProductionToolName));
            foreach (CapturedRequest request in fixture.Provider.Captures)
            {
                Assert.DoesNotContain(
                    request.Messages.SelectMany(static message => message.Contents),
                    static content => content is VisualContextImage);
                Assert.DoesNotContain(
                    request.Messages,
                    static message => message.Text.StartsWith("Current visual context:", StringComparison.Ordinal));
            }
        }
        finally
        {
            await fixture.CleanupAsync(sceneTree);
        }
    }

    private const string DummyToolName = "dummy_action";

    private static VisualContextImage[] ImagePartsOf(CapturedRequest request)
        => [.. request.Messages
            .SelectMany(static message => message.Contents)
            .OfType<VisualContextImage>()];

    private static void AssertPng(byte[] png)
        => Assert.True(
            png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47,
            "The minimap tool result must carry a real PNG payload.");

    /// <summary>
    /// Objective anatomical check on the rendered minimap: the subject's own marker — recoloured by the fixed gold
    /// override — renders in the fixed gold near the map centre, which is the subject's live world position
    /// (AI-013 TR-3). Both the subject's circle and its <c>*You*</c> label glyphs paint in the override colour.
    /// </summary>
    private static void AssertGoldSubjectHighlightNearTheCentre(Image decoded)
    {
        var gold = Color.FromHtml(VisualContextCapture.SubjectHighlightGoldHtml);
        int width = decoded.GetWidth();
        int height = decoded.GetHeight();
        int centreX = width / 2;
        int centreY = height / 2;
        int halfWindow = Math.Max(width, height) / 8;
        int goldPixels = 0;
        for (int y = centreY - halfWindow; y <= centreY + halfWindow; y += 2)
        {
            for (int x = centreX - halfWindow; x <= centreX + halfWindow; x += 2)
            {
                if (x < 0 || y < 0 || x >= width || y >= height)
                {
                    continue;
                }

                Color pixel = decoded.GetPixel(x, y);
                if (Math.Abs(pixel.R - gold.R) < 0.12f
                    && Math.Abs(pixel.G - gold.G) < 0.12f
                    && Math.Abs(pixel.B - gold.B) < 0.12f)
                {
                    goldPixels++;
                }
            }
        }

        Assert.True(
            goldPixels >= 40,
            $"Expected the gold *You* subject highlight near the map centre; found {goldPixels} matching sampled "
                + $"pixels in the central window.");
    }

    private static async Task WaitUntilAsync(SceneTree sceneTree, Func<bool> predicate, int maxFrames = 900)
    {
        for (int frame = 0; frame < maxFrames && !predicate(); frame++)
        {
            await WaitForNextFrameAsync(sceneTree);
        }

        Assert.True(predicate(), $"Condition was not met within {maxFrames} frames.");
    }

    /// <summary>
    /// One scripted visual-context session scope: a subject character positioned at the known coordinate hosting
    /// a minimap marker (or the reference NPC rig for real vision capture), a mind with hermetically injected
    /// diagnostics, mode, and renderer resolution, and a capturing scripted provider.
    /// </summary>
    private sealed class SessionFixture
    {
        private readonly Node _fixtureRoot;
        private readonly FixturePlayerCharacter _player;
        private readonly ICharacter _owner;
        private readonly CharacterHub? _referenceHost;
        private readonly AgenticMind? _embeddedMind;
        private readonly DummyTool _dummyTool = new();
        private readonly bool _rendererAvailable;
        private AgenticMind? _mind;

        public SessionFixture(
            VisualContextMode mode,
            bool rendererAvailable = true,
            bool useReferenceNpcRig = false,
            IVision? visionOverride = null,
            bool enableTranscriptLogging = false)
        {
            _rendererAvailable = rendererAvailable;
            _fixtureRoot = new Node3D { Name = $"VisualContext-{mode}-{Guid.NewGuid():N}" };
            _player = new FixturePlayerCharacter();
            _fixtureRoot.AddChild(_player);
            if (useReferenceNpcRig)
            {
                // The rig renders into a real environment so the captured view carries inspectable content
                // (the test-environment fixture is the skill's default for lit world fixtures), with an explicit
                // ground plane exactly like the proven NPC vision-capture fixture.
                _fixtureRoot.AddChild(LoadPackedScene("res://assets/testing/test_environment.tscn").Instantiate());
                MeshInstance3D ground = new()
                {
                    Name = "Ground",
                    Mesh = new BoxMesh { Size = new Vector3(200f, 0.1f, 200f) },
                    Position = new Vector3(0f, -0.05f, 0f),
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.44f, 0.47f) },
                };
                _fixtureRoot.AddChild(ground);
                CharacterHub npc = LoadPackedScene("res://assets/characters/templates/reference_female/reference_female_npc.tscn")
                    .Instantiate<CharacterHub>();
                npc.Id = SubjectId;
                npc.Position = _subjectPosition;
                // The runtime role installer finalises the authored capability references this way; the fixture
                // path mirrors it so the character's component projection exposes the real eyes rig.
                npc.RefreshComponents();
                // The template's own production mind — with its authored perception faculties — is detached
                // before the world enters the tree; it rejoins the settled character below, so its session
                // starts after the player membership resolved instead of mid-entry.
                _embeddedMind = npc.GetNode<AgenticMind>("Mind");
                npc.RemoveChild(_embeddedMind);
                _fixtureRoot.AddChild(npc);
                _referenceHost = npc;
                _owner = npc;
            }
            else if (visionOverride is not null)
            {
                _owner = new ComponentCharacter(SubjectId, _subjectPosition, visionOverride);
            }
            else
            {
                // The marker's identity resolves to char:<SubjectId> through the character host's ancestry
                // (SCN-002 TR-7), so the gold *You* override key matches the owning subject.
                CharacterHub host = new()
                {
                    Name = "VadimHost",
                    Id = SubjectId,
                    Position = _subjectPosition,
                };
                host.AddChild(CreateSubjectMarker());
                _fixtureRoot.AddChild(host);
                _owner = host;
            }

            Provider = new CapturingSessionClientProvider();
            TranscriptRoot = Path.Combine(
                Path.GetTempPath(),
                "AlleyCat.VisualContextIntegrationTests",
                Guid.NewGuid().ToString("N"));
            Mode = mode;
            EnableTranscriptLogging = enableTranscriptLogging;
        }

        public VisualContextMode Mode
        {
            get;
        }

        public bool EnableTranscriptLogging
        {
            get;
        }

        public CapturingSessionClientProvider Provider
        {
            get;
        }

        public string TranscriptRoot
        {
            get;
        }

        public string CharacterTranscriptDirectory
        {
            get
            {
                string runDirectory = Assert.Single(Directory.GetDirectories(TranscriptRoot));
                return Assert.Single(Directory.GetDirectories(runDirectory, SubjectId));
            }
        }

        public async Task StartAsync(SceneTree sceneTree)
        {
            Assert.Null(_mind);
            AgenticMind mind = _embeddedMind is { } embedded
                ? embedded
                : new TestAgenticMind(_owner);
            mind.SystemInstruction = new PromptStack
            {
                Sections = [new TextPromptSection { Text = "Static session guidance.", Name = "Static" }],
            };
            mind.CurrentSceneStatus = new PromptStack
            {
                Sections =
                [
                    new TextPromptSection { Text = "Fresh scene status.", Name = "Status" },
                ],
            };
            mind.ClientProvider = Provider;
            mind.Tools = [_dummyTool];
            mind.ObservationImportanceThreshold = 1f;
            mind.SetDiagnosticsSettingsLoaderForTesting(() => new AIDiagnosticsSettings(
                EnableRequestResponseLogging: false,
                EnableReasoningLogging: true,
                EnableSessionTranscriptLogging: EnableTranscriptLogging));
            mind.SetVisualContextModeLoaderForTesting(() => Mode);
            if (!_rendererAvailable)
            {
                mind.SetMiniMapRendererLoaderForTesting(() => null);
            }

            mind.SetSceneContextLoaderForTesting(() => new SceneContext([_owner, _player]));

            // The fixture world — the subject host with its marker and the player — enters first and settles;
            // the mind then joins the already-entered tree (the reference rig's own mind rejoins its character),
            // so its session starts from settled scene state with the player group membership resolved.
            _ = sceneTree.Root.CallDeferred(Node.MethodName.AddChild, _fixtureRoot);
            await WaitForFramesAsync(sceneTree, 3);
            if (_referenceHost is { } host)
            {
                host.AddChild(mind);
            }
            else
            {
                _fixtureRoot.AddChild(mind);
            }

            await WaitForFramesAsync(sceneTree, 3);
            _mind = mind;
        }

        public void EnqueueHoldForever() => Provider.EnqueueHoldForever();

        public async Task CleanupAsync(SceneTree sceneTree)
        {
            if (_mind is not null)
            {
                if (_mind.GetParent() is { } parent)
                {
                    parent.RemoveChild(_mind);
                }

                for (int frame = 0; frame < 2000 && _mind.ActiveRunner is not null; frame++)
                {
                    await WaitForNextFrameAsync(sceneTree);
                }

                Assert.True(_mind.ActiveRunner is null, "The visual-context session must fully end before cleanup.");
                _mind.Free();
                _mind = null;
            }

            _fixtureRoot.QueueFree();
            _dummyTool.Free();
            Provider.Free();
            await WaitForFramesAsync(sceneTree, 3);
            MindSessionTranscriptRecorder.SetRootPathOverrideForTesting(null);
            MindSessionTranscriptRecorder.ResetTurnCountersForTesting();
            try
            {
                Directory.Delete(TranscriptRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
                // Nothing was ever written under this root.
            }
        }

        /// <summary>
        /// The subject's marker: an authored circle plus both label flavours, so the gold override's colour and
        /// text replacement apply to every primitive of the subject's marker.
        /// </summary>
        private static MiniMapMarker CreateSubjectMarker()
        {
            MiniMapCircle circle = new()
            {
                Name = "Body",
                Radius = 0.5f,
                FillColour = new Color("4a7fb5"),
            };
            MiniMapLabel identityLabel = new()
            {
                Name = "IdentityLabel",
                Flavour = MiniMapLabelFlavour.Identity,
                TextSize = 20,
                Position = new Vector3(0f, 0f, -1f),
            };
            MiniMapLabel configuredLabel = new()
            {
                Name = "NameLabel",
                Flavour = MiniMapLabelFlavour.Configured,
                Text = "Vadim",
                TextSize = 14,
            };
            return MiniMapTestHarness.CreateMarker("SubjectMarker", drawOrder: 10, circle, identityLabel, configuredLabel);
        }
    }

    private sealed class ComponentCharacter(string id, Vector3 position, params IComponent[] components) : ICharacter
    {
        public string Id { get; set; } = id;

        public IReadOnlyList<IComponent> Components { get; } = components;

        public IReadOnlyList<VisualCue> VisualCues { get; } = [];

        public Transform3D GlobalTransform
        {
            get; set;
        } =
            Transform3D.Identity with
            {
                Origin = position
            };
    }

    /// <summary>Ordinary extra tool whose calls drive accepted responses without visual side effects.</summary>
    private sealed class DummyTool : AgentTool
    {
        public DummyTool()
        {
            ToolName = DummyToolName;
            ToolDescription = "Do nothing, successfully.";
        }

        protected override Delegate CreateDelegate() => Act;

        private ValueTask<AgentToolResult> Act(ScenarioContext context)
            => ValueTask.FromResult(new AgentToolResult("Done."));
    }

    /// <summary>
    /// Fake vision whose camera is a real runtime node and whose capture throws, modelling a rig failure
    /// (AI-013 TR-5).
    /// </summary>
    private sealed class ThrowingVision : IVision
    {
        public Camera3D? Camera { get; set; } = new();

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
            => ValueTask.FromException<Image>(
                new InvalidOperationException("The vision camera was invalidated mid-capture."));
    }

    /// <summary>
    /// Fake vision producing one distinct solid-colour frame per capture, so auto-mode rebuild assertions can
    /// count captures and distinguish reused from freshly captured view images.
    /// </summary>
    private sealed class CountingVision : IVision
    {
        private static readonly Color[] _frameColours = [new("e03131"), new("1c7ed6"), new("2f9e44")];

        public int CaptureCount
        {
            get; private set;
        }

        public Camera3D? Camera { get; set; } = new();

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
        {
            int index = CaptureCount++;
            var frame = Image.CreateEmpty(24, 24, false, Image.Format.Rgb8);
            frame.FillRect(new Rect2I(0, 0, 24, 24), _frameColours[Math.Min(index, _frameColours.Length - 1)]);
            return ValueTask.FromResult(frame);
        }
    }

    private sealed partial class TestAgenticMind(ICharacter owner) : AgenticMind
    {
        protected override ICharacter ResolveOwningCharacter() => owner;
    }

    /// <summary>One captured model-facing tool with the fields the inventory contract asserts.</summary>
    private sealed record CapturedTool(string Description, IReadOnlyList<string> ParameterProperties, IReadOnlyList<string> RequiredParameters);

    /// <summary>Immutable snapshot of one provider request: messages plus the model-facing tool surface.</summary>
    private sealed record CapturedRequest(IReadOnlyList<ChatMessage> Messages, IReadOnlyDictionary<string, CapturedTool> Tools);

    private sealed partial class CapturingSessionClientProvider : ClientProvider
    {
        private readonly Queue<Func<CancellationToken, Task<ChatResponse>>> _steps = new();

        public List<CapturedRequest> Captures { get; } = [];

        public void EnqueueHoldForever()
            => _steps.Enqueue(async cancellationToken =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse();
            });

        public void EnqueueCall(string toolName)
            => _steps.Enqueue(_ => Task.FromResult(new ChatResponse(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            $"call-{Captures.Count.ToString(CultureInfo.InvariantCulture)}",
                            toolName,
                            new Dictionary<string, object?>()),
                    ]))));

        public void EnqueueResponse(ChatMessage message)
            => _steps.Enqueue(_ => Task.FromResult(new ChatResponse(message)));

        public override IChatClient CreateChatClient() => new CapturingClient(this);

        private sealed class CapturingClient(CapturingSessionClientProvider owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                owner.Captures.Add(new CapturedRequest(
                    [.. messages],
                    options?.Tools?.OfType<AIFunction>().ToDictionary(
                        static function => function.Name,
                        static function => Describe(function))
                    ?? []));
                cancellationToken.ThrowIfCancellationRequested();
                return owner._steps.Count == 0
                    ? throw new InvalidOperationException(
                        "The capturing session client received an unexpected request.")
                    : owner._steps.Dequeue()(cancellationToken);
            }

            private static CapturedTool Describe(AIFunction function)
            {
                List<string> properties = [];
                if (function.JsonSchema.ValueKind == JsonValueKind.Object
                    && function.JsonSchema.TryGetProperty("properties", out JsonElement propertiesElement)
                    && propertiesElement.ValueKind == JsonValueKind.Object)
                {
                    properties.AddRange(propertiesElement.EnumerateObject().Select(static property => property.Name));
                }

                List<string> required = [];
                if (function.JsonSchema.ValueKind == JsonValueKind.Object
                    && function.JsonSchema.TryGetProperty("required", out JsonElement requiredElement)
                    && requiredElement.ValueKind == JsonValueKind.Array)
                {
                    required.AddRange(requiredElement.EnumerateArray().Select(static item => item.GetString() ?? string.Empty));
                }

                return new CapturedTool(function.Description ?? string.Empty, properties, required);
            }

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
}
