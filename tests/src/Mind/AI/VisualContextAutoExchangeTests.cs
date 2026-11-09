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
    /// On a Chat-Completions-kind endpoint each fabricated result is text-only and each image rides exactly one
    /// adjacent user message carrier — that image's instructional text plus the image part — placed after the
    /// fabricated exchange (TR-10).
    /// </summary>
    [Fact]
    public void Compose_OnAChatCompletionsEndpoint_CarriesEachImageInAnAdjacentUserMessage()
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
            VisualContextAutoExchange.MinimapInstruction,
            Assert.IsType<string>(results[0].Result));
        Assert.Equal(
            VisualContextAutoExchange.ViewInstruction,
            Assert.IsType<string>(results[1].Result));

        VisualContextImage[] images = [minimap, view];
        for (int index = 0; index < images.Length; index++)
        {
            ChatMessage carrier = exchange[2 + index];
            Assert.Equal(ChatRole.User, carrier.Role);
            Assert.Equal(
                VisualContextAutoExchange.DescribeInstruction(images[index].Kind),
                Assert.IsType<TextContent>(Assert.Single(carrier.Contents.OfType<TextContent>())).Text);
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

    private static VisualContextImage Minimap()
        => VisualContextImage.Minimap(_minimapBytes, centreX: 1.5, centreY: 2.5, radiusMetres: 10, gameTimeSeconds: 42.5);

    private static VisualContextImage View() => VisualContextImage.View(_viewBytes, gameTimeSeconds: 42.5);
}
