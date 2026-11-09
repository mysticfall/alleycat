using AlleyCat.Mind.AI.Provider;
using AlleyCat.Mind.AI.Tool;
using Microsoft.Extensions.AI;

namespace AlleyCat.Mind.AI;

/// <summary>
/// Composes the runtime-fabricated visual-context exchange of AI-013 auto mode (TR-8/TR-10): from the
/// materialisation's successful captures, exactly one synthetic exchange — an assistant message carrying one
/// <c>minimap</c> and/or <c>capture_view</c> function call with zero-argument arguments, using the real tool names
/// of AI-013 TR-2, followed by the corresponding tool-result messages — presented immediately before the fresh
/// current-scene message and never appended to the retained transcript.
/// </summary>
/// <remarks>
/// <para>
/// The transport follows the session's effective chat client kind (AI-013 TR-10): on Responses-capable endpoints
/// each fabricated tool result carries its image in-result — result content of the TR-11 instructional text for
/// its kind plus the image part — so the image rides inside the function-call output; on Chat Completions and
/// compatible backends each fabricated result is text-only and each image rides one adjacent user message carrier
/// holding that image's instructional text plus the image parts, placed after the fabricated exchange and before
/// the current-scene message.
/// </para>
/// <para>
/// Fabricated call IDs use a distinct sub-namespace of the synthetic call-ID namespace (AI-002 TR-18), so they can
/// never collide with a real tool call ID or a <c>scene_update</c> ID.
/// </para>
/// </remarks>
internal static class VisualContextAutoExchange
{
    /// <summary>Distinct sub-namespace prefix of fabricated visual-context call IDs (AI-002 TR-18, AI-013 TR-8).</summary>
    internal const string SyntheticCallIDPrefix = "runtime-visual-context-";

    /// <summary>
    /// The normative instructional text accompanying a live auto-mode minimap image (AI-013 TR-11): a brief
    /// instruction explaining what the image represents, never a capture record.
    /// </summary>
    internal const string MinimapInstruction =
        "A top-down minimap of your current surroundings, centred on your own position with your marker "
        + "highlighted. Consider it a visual cue for where things stand around you in the current scene.";

    /// <summary>
    /// The normative instructional text accompanying a live auto-mode view image (AI-013 TR-11): a brief
    /// instruction explaining what the image represents, never a capture record.
    /// </summary>
    internal const string ViewInstruction =
        "This is what you currently see through your own eyes at this moment. Consider it a visual cue for the "
        + "current scene.";

    /// <summary>Formats one fabricated call ID from its session-scoped monotonic sequence number.</summary>
    internal static string FormatCallId(long sequence) => $"{SyntheticCallIDPrefix}{sequence}";

    /// <summary>The normative instructional text of one image kind (AI-013 TR-11).</summary>
    internal static string DescribeInstruction(VisualContextImageKind kind)
        => kind switch
        {
            VisualContextImageKind.Minimap => MinimapInstruction,
            VisualContextImageKind.View => ViewInstruction,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>The real model-facing tool name of one image kind (AI-013 TR-2/TR-8).</summary>
    internal static string DescribeToolName(VisualContextImageKind kind)
        => kind switch
        {
            VisualContextImageKind.Minimap => MinimapTool.ProductionToolName,
            VisualContextImageKind.View => CaptureViewTool.ProductionToolName,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Composes the fabricated visual-context exchange from the materialisation's successful captures (AI-013
    /// TR-8/TR-10): one assistant message carrying a zero-argument function call per capture using its real tool
    /// name, one tool-result message carrying the corresponding results, and — on Chat Completions only — one
    /// adjacent user message carrier per image placed after the exchange. <paramref name="nextCallId" /> is drawn
    /// exactly once per capture, in capture order.
    /// </summary>
    internal static IReadOnlyList<ChatMessage> Compose(
        IReadOnlyList<VisualContextImage> images,
        OpenAIChatClientKind chatClientKind,
        Func<string> nextCallId)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(nextCallId);
        if (images.Count == 0)
        {
            throw new ArgumentException("At least one successful capture is required.", nameof(images));
        }

        List<FunctionCallContent> calls = [];
        List<FunctionResultContent> results = [];
        List<ChatMessage>? carriers = null;
        foreach (VisualContextImage image in images)
        {
            string callId = nextCallId();
            calls.Add(new FunctionCallContent(callId, DescribeToolName(image.Kind), _emptyArguments));
            string instruction = DescribeInstruction(image.Kind);
            if (chatClientKind == OpenAIChatClientKind.Responses)
            {
                // The image rides inside the fabricated function-call output: result content of the instructional
                // text plus the image part (AI-013 TR-10).
                results.Add(new FunctionResultContent(callId, new AIContent[] { new TextContent(instruction), image }));
            }
            else
            {
                // The Chat Completions tool-result channel is text-only, so the result carries the instructional
                // text and each image rides its own adjacent user message carrier after the exchange (AI-013
                // TR-10).
                results.Add(new FunctionResultContent(callId, instruction));
                (carriers ??= [])
                    .Add(new ChatMessage(ChatRole.User, [new TextContent(instruction), image]));
            }
        }

        List<ChatMessage> exchange =
        [
            new ChatMessage(ChatRole.Assistant, [.. calls]),
            new ChatMessage(ChatRole.Tool, [.. results]),
        ];
        if (carriers is not null)
        {
            exchange.AddRange(carriers);
        }

        return exchange;
    }

    private static readonly IDictionary<string, object?> _emptyArguments = new Dictionary<string, object?>();
}
