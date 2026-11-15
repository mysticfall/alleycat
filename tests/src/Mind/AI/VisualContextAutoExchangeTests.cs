using AlleyCat.Mind.AI;
using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using Microsoft.Extensions.AI;
using Xunit;

namespace AlleyCat.Tests.Mind.AI;

/// <summary>
/// Verifies the composition of the AI-013 auto-mode fabricated visual-context exchange (TR-8/TR-10/TR-11): the
/// real tool names with zero-argument arguments, the endpoint-dependent result and carrier shapes, the verbatim
/// instructional texts, and the distinct fabricated call-ID sub-namespace.
/// </summary>
public sealed class VisualContextAutoExchangeTests
{
    private static readonly byte[] _minimapBytes = [1, 2, 3, 4];

    private static readonly byte[] _viewBytes = [5, 6, 7, 8];

    /// <summary>The normative instructional texts are exactly the TR-11 wording, one per kind.</summary>
    [Fact]
    public void DescribeInstruction_RendersTheNormativeTextsVerbatim()
    {
        Assert.Equal(
            "A top-down minimap of your current surroundings, centred on your own position with your marker "
                + "highlighted. Consider it a visual cue for where things stand around you in the current scene.",
            VisualContextAutoExchange.MinimapInstruction);
        Assert.Equal(
            VisualContextAutoExchange.MinimapInstruction,
            VisualContextAutoExchange.DescribeInstruction(VisualContextImageKind.Minimap));
        Assert.Equal(
            "This is what you currently see through your own eyes at this moment. Consider it a visual cue for the "
                + "current scene.",
            VisualContextAutoExchange.ViewInstruction);
        Assert.Equal(
            VisualContextAutoExchange.ViewInstruction,
            VisualContextAutoExchange.DescribeInstruction(VisualContextImageKind.View));
    }

    /// <summary>
    /// On a Responses-kind endpoint the fabricated exchange carries both images in-result (TR-8/TR-10): one
    /// assistant message with one zero-argument call per successful capture under its real tool name, one
    /// tool-result message whose results are instructional text plus the image part — and no adjacent user
    /// message anywhere.
    /// </summary>
    [Fact]
    public void Compose_OnAResponsesEndpoint_CarriesEachImageInsideItsFabricatedResult()
    {
        VisualContextImage minimap = Minimap();
        VisualContextImage view = View();
        IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
            [minimap, view],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(1));

        Assert.Equal([ChatRole.Assistant, ChatRole.Tool], exchange.Select(static message => message.Role));

        FunctionCallContent[] calls = [.. exchange[0].Contents.OfType<FunctionCallContent>()];
        Assert.Equal(
            [MinimapTool.ProductionToolName, CaptureViewTool.ProductionToolName],
            calls.Select(static call => call.Name));
        Assert.All(
            calls,
            call => Assert.Empty(call.Arguments ?? new Dictionary<string, object?>()));

        FunctionResultContent[] results = [.. exchange[1].Contents.OfType<FunctionResultContent>()];
        Assert.Equal(
            calls.Select(static call => call.CallId),
            results.Select(static result => result.CallId));
        VisualContextImage[] images = [minimap, view];
        for (int index = 0; index < results.Length; index++)
        {
            AIContent[] parts = Assert.IsType<AIContent[]>(results[index].Result);
            Assert.Equal(
                VisualContextAutoExchange.DescribeInstruction(images[index].Kind),
                Assert.IsType<TextContent>(parts[0]).Text);
            Assert.Same(images[index], parts[1]);
        }
    }

    /// <summary>
    /// On a Chat-Completions-kind endpoint each fabricated result stays text-only with its full composed text
    /// plus the forward-looking delivery notice — inline for the minimap, parenthesised after a blank line for
    /// the captured view (playtest R7) — and each image rides exactly one adjacent user message carrier after the
    /// fabricated exchange and before the scene-status message (AI-013 TR-10). The carrier is image-only
    /// transport — no text part at all (playtest R6): the result is the pointer, the carrier is the delivery.
    /// </summary>
    [Fact]
    public void Compose_OnAChatCompletionsEndpoint_CarriesEachImageInAnImageOnlyUserCarrier()
    {
        VisualContextImage minimap = Minimap();
        VisualContextImage view = View();
        IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
            [minimap, view],
            OpenAIChatClientKind.ChatCompletions,
            () => VisualContextAutoExchange.FormatCallId(1));

        Assert.Equal(
            [ChatRole.Assistant, ChatRole.Tool, ChatRole.User, ChatRole.User],
            exchange.Select(static message => message.Role));

        FunctionResultContent[] results = [.. exchange[1].Contents.OfType<FunctionResultContent>()];
        Assert.All(results, static result => Assert.IsType<string>(result.Result));
        Assert.Equal(
            $"{VisualContextAutoExchange.MinimapInstruction} {VisualContextAutoExchange.ImageDeliveryNotice}",
            Assert.IsType<string>(results[0].Result));
        Assert.Equal(
            $"{VisualContextAutoExchange.ViewInstruction}\n\n({VisualContextAutoExchange.ImageDeliveryNotice})",
            Assert.IsType<string>(results[1].Result));

        VisualContextImage[] images = [minimap, view];
        for (int index = 0; index < images.Length; index++)
        {
            ChatMessage carrier = exchange[2 + index];
            Assert.Equal(ChatRole.User, carrier.Role);
            Assert.Empty(carrier.Contents.OfType<TextContent>());
            Assert.Same(images[index], Assert.Single(carrier.Contents.OfType<VisualContextImage>()));
        }
    }

    /// <summary>
    /// A single successful capture fabricates a single-call exchange with no trace of the failed kind, and an empty
    /// capture list is rejected (TR-8: nothing captured presents no exchange).
    /// </summary>
    [Fact]
    public void Compose_WithOneCapture_FabricatesASingleCallExchangeAndRejectsEmptyCaptures()
    {
        IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
            [Minimap()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(7));

        Assert.Equal([ChatRole.Assistant, ChatRole.Tool], exchange.Select(static message => message.Role));
        FunctionCallContent call = Assert.Single(exchange[0].Contents.OfType<FunctionCallContent>());
        Assert.Equal(MinimapTool.ProductionToolName, call.Name);
        Assert.Equal("runtime-visual-context-7", call.CallId);

        _ = Assert.Throws<ArgumentException>(
            () => VisualContextAutoExchange.Compose(
                [],
                OpenAIChatClientKind.Responses,
                () => "unused"));
    }

    /// <summary>
    /// Fabricated call IDs draw from their distinct sub-namespace of the synthetic call-ID namespace (AI-002
    /// TR-18): one ID per capture in order, never colliding with the <c>scene_update</c> prefix.
    /// </summary>
    [Fact]
    public void Compose_DrawsOneDistinctNamespaceCallIdPerCaptureInOrder()
    {
        long sequence = 0;
        IReadOnlyList<ChatMessage> exchange = VisualContextAutoExchange.Compose(
            [Minimap(), View()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(++sequence));

        string[] callIds = [.. exchange[0].Contents.OfType<FunctionCallContent>().Select(static call => call.CallId!)];
        Assert.Equal(["runtime-visual-context-1", "runtime-visual-context-2"], callIds);
        Assert.All(callIds, callId => Assert.StartsWith(VisualContextAutoExchange.SyntheticCallIDPrefix, callId, StringComparison.Ordinal));
        Assert.All(
            callIds,
            callId => Assert.DoesNotContain(SyntheticSceneUpdateExchange.DeliveryToolName, callId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The subject's own head-direction line (playtest R5) appends to the captured view's instructional text
    /// alone as a trailing inline sentence, in the in-result text and the Chat-Completions tool-result string
    /// alike; the Chat-Completions carriers are image-only and the minimap's normative text stays verbatim.
    /// </summary>
    [Fact]
    public void Compose_AppendsTheSubjectHeadDirectionLineToTheViewInstructionOnly()
    {
        string[] lines =
        [
            "Your head is turned to your right.",
        ];

        IReadOnlyList<ChatMessage> responses = VisualContextAutoExchange.Compose(
            [Minimap(), View()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(1),
            lines);
        FunctionResultContent[] responsesResults =
            [.. responses[1].Contents.OfType<FunctionResultContent>()];
        AIContent[] minimapParts = Assert.IsType<AIContent[]>(responsesResults[0].Result);
        Assert.Equal(
            VisualContextAutoExchange.MinimapInstruction,
            Assert.IsType<TextContent>(minimapParts[0]).Text);
        AIContent[] viewParts = Assert.IsType<AIContent[]>(responsesResults[1].Result);
        string viewText = Assert.IsType<TextContent>(viewParts[0]).Text;
        Assert.StartsWith(VisualContextAutoExchange.ViewInstruction, viewText, StringComparison.Ordinal);
        Assert.EndsWith(string.Join(" ", lines), viewText, StringComparison.Ordinal);

        IReadOnlyList<ChatMessage> chatCompletions = VisualContextAutoExchange.Compose(
            [Minimap(), View()],
            OpenAIChatClientKind.ChatCompletions,
            () => VisualContextAutoExchange.FormatCallId(1),
            lines);
        FunctionResultContent[] chatResults =
            [.. chatCompletions[1].Contents.OfType<FunctionResultContent>()];
        Assert.Equal(
            $"{VisualContextAutoExchange.MinimapInstruction} {VisualContextAutoExchange.ImageDeliveryNotice}",
            Assert.IsType<string>(chatResults[0].Result));
        Assert.EndsWith(
            $"{string.Join(" ", lines)}\n\n({VisualContextAutoExchange.ImageDeliveryNotice})",
            Assert.IsType<string>(chatResults[1].Result),
            StringComparison.Ordinal);
        // The carriers are image-only transport with no text part at all (playtest R6).
        Assert.All(
            chatCompletions.Skip(2),
            carrier =>
            {
                Assert.Empty(carrier.Contents.OfType<TextContent>());
                _ = Assert.Single(carrier.Contents.OfType<VisualContextImage>());
            });
    }

    /// <summary>
    /// The visible-cue description lines append to the captured view's instructional text as one labelled list
    /// block through the shared formatting path; the minimap's normative text stays verbatim.
    /// </summary>
    [Fact]
    public void Compose_AppendsTheVisibleCueListBlockToTheViewInstructionOnly()
    {
        string[] lines =
        [
            "`char:subject`'s face: She looks to be a plain-looking woman in her early 20s.",
            "`char:subject`'s hands: Paint-stained fingers.",
        ];

        IReadOnlyList<ChatMessage> responses = VisualContextAutoExchange.Compose(
            [Minimap(), View()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(1),
            subjectHeadDirectionLines: null,
            visibleCueDescriptionLines: lines);
        FunctionResultContent[] responsesResults =
            [.. responses[1].Contents.OfType<FunctionResultContent>()];
        AIContent[] minimapParts = Assert.IsType<AIContent[]>(responsesResults[0].Result);
        Assert.Equal(
            VisualContextAutoExchange.MinimapInstruction,
            Assert.IsType<TextContent>(minimapParts[0]).Text);
        AIContent[] viewParts = Assert.IsType<AIContent[]>(responsesResults[1].Result);
        string viewText = Assert.IsType<TextContent>(viewParts[0]).Text;
        Assert.StartsWith(VisualContextAutoExchange.ViewInstruction, viewText, StringComparison.Ordinal);
        Assert.EndsWith(VisibleCueDescriptions.FormatListBlock(lines), viewText, StringComparison.Ordinal);
        Assert.Contains("\n- ", viewText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Absent descriptor lines leave the fabricated instructional texts verbatim: the exchange composition is
    /// byte-identical to the pre-descriptor shape when no observed character holds a look target.
    /// </summary>
    [Fact]
    public void Compose_WithoutOrientationLines_LeavesTheInstructionalTextsVerbatim()
    {
        IReadOnlyList<ChatMessage> withDefault = VisualContextAutoExchange.Compose(
            [Minimap()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(1));
        IReadOnlyList<ChatMessage> withEmpty = VisualContextAutoExchange.Compose(
            [Minimap()],
            OpenAIChatClientKind.Responses,
            () => VisualContextAutoExchange.FormatCallId(1),
            []);
        IReadOnlyList<ChatMessage>[] exchanges = [withDefault, withEmpty];
        foreach (IReadOnlyList<ChatMessage> exchange in exchanges)
        {
            FunctionResultContent result = Assert.Single(exchange[1].Contents.OfType<FunctionResultContent>());
            AIContent[] parts = Assert.IsType<AIContent[]>(result.Result);
            Assert.Equal(
                VisualContextAutoExchange.MinimapInstruction,
                Assert.IsType<TextContent>(parts[0]).Text);
        }
    }

    private static VisualContextImage Minimap()
        => VisualContextImage.Minimap(_minimapBytes, centreX: 1.5, centreY: 2.5, radiusMetres: 10, gameTimeSeconds: 42.5);

    private static VisualContextImage View() => VisualContextImage.View(_viewBytes, gameTimeSeconds: 42.5);
}
